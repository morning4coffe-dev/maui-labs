using System.Net;
using System.Net.WebSockets;
using Microsoft.Maui.DevFlow.Devices;

namespace Microsoft.Maui.Cli.DevFlow.Broker;

/// <summary>
/// Proxies the device host's live video stream to Inspector pages.
/// <para>
/// The proxy exists for a security reason, not a plumbing one. The device host authenticates
/// control clients with a bearer token; a browser cannot attach headers to a WebSocket, and that
/// token must never be placed in a page, a URL, or a DOM where a framed document could read it.
/// Holding it server-side keeps the browser presenting only the broker's embed token, and keeps
/// the broker the single front door for the device layer.
/// </para>
/// </summary>
public partial class BrokerServer
{
    // Video is bursty and frames are small; this is comfortably above a keyframe at phone
    // resolutions and bounds what one misbehaving upstream can buffer.
    private const int VideoBufferBytes = 256 * 1024;

    private async Task HandleDeviceVideoWebSocket(HttpListenerContext context)
    {
        // A WebSocket is NOT subject to the same-origin policy, so any page a user visits could
        // otherwise open this socket and receive a live feed of their device screen — with the
        // broker helpfully attaching the device host's bearer token on its behalf. Both gates are
        // required: loopback origin proves the caller is local, and the embed token proves it is
        // an Inspector session rather than any other local page.
        var origin = context.Request.Headers["Origin"];
        if (!Inspector.LocalOriginValidator.IsAllowed(origin, _port))
        {
            context.Response.StatusCode = 403;
            context.Response.Close();
            return;
        }

        if (!Inspector.InspectorServer.IsTrustedEmbed(_embedToken, context.Request.QueryString["embed"]))
        {
            context.Response.StatusCode = 403;
            context.Response.Close();
            return;
        }

        var deviceId = context.Request.QueryString["deviceId"];
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            context.Response.StatusCode = 400;
            context.Response.Close();
            return;
        }

        var host = MobileCanvasHost.TryRead();
        if (host is null)
        {
            // No device host: fail the upgrade so the client falls back to screenshots instead of
            // waiting on a socket that will never carry a frame.
            context.Response.StatusCode = 503;
            context.Response.Close();
            return;
        }

        WebSocketContext? downstreamContext = null;
        ClientWebSocket? upstream = null;
        try
        {
            upstream = new ClientWebSocket();
            upstream.Options.SetRequestHeader("Authorization", $"Bearer {host.ControlToken}");

            var query = BuildUpstreamQuery(context.Request.QueryString, deviceId);
            var upstreamUri = new Uri($"ws://127.0.0.1:{host.Port}/ws/video?{query}");

            using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await upstream.ConnectAsync(upstreamUri, connectTimeout.Token);

            // Only accept the browser's socket once the upstream is live, so a failure surfaces as
            // a refused upgrade the client can fall back from rather than a silent dead stream.
            downstreamContext = await context.AcceptWebSocketAsync(subProtocol: null);
            await PumpAsync(upstream, downstreamContext.WebSocket);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or HttpRequestException)
        {
            if (downstreamContext is null)
            {
                try
                {
                    context.Response.StatusCode = 502;
                    context.Response.Close();
                }
                catch { /* the client may already be gone */ }
            }
            else
            {
                // The accept succeeded and the pump then failed. Abort rather than leaving an
                // open socket holding its buffers until GC — streams restart on every device
                // change and every stream failure, so these would accumulate over a session.
                try { downstreamContext.WebSocket.Abort(); } catch { }
            }
        }
        finally
        {
            upstream?.Dispose();
            downstreamContext?.WebSocket.Dispose();
        }
    }

    /// <summary>
    /// Rebuilds the upstream query from the client's, allowing only the parameters that shape the
    /// stream. Forwarding blindly would let a page smuggle arbitrary values at a token it cannot
    /// otherwise reach.
    /// </summary>
    private static string BuildUpstreamQuery(System.Collections.Specialized.NameValueCollection query, string deviceId)
    {
        var parts = new List<string> { $"deviceId={Uri.EscapeDataString(deviceId)}" };

        if (int.TryParse(query["fps"], out var fps))
            parts.Add($"fps={Math.Clamp(fps, 1, 60)}");

        if (double.TryParse(query["scale"], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var scale))
        {
            parts.Add($"scale={Math.Clamp(scale, 0.1, 1.0).ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        return string.Join("&", parts);
    }

    /// <summary>
    /// Copies frames upstream-to-downstream until either side closes.
    /// <para>
    /// Deliberately one-directional: the video channel carries frames out and nothing in. Input
    /// travels the HTTP control path, where it is arbitrated by the mutation lease — a socket that
    /// also accepted commands would be a second, unarbitrated way to drive the device.
    /// </para>
    /// </summary>
    private static async Task PumpAsync(WebSocket upstream, WebSocket downstream)
    {
        var buffer = new byte[VideoBufferBytes];

        while (upstream.State == WebSocketState.Open && downstream.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await upstream.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            }
            catch (WebSocketException)
            {
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            try
            {
                await downstream.SendAsync(
                    new ArraySegment<byte>(buffer, 0, result.Count),
                    result.MessageType,
                    result.EndOfMessage,
                    CancellationToken.None);
            }
            catch (WebSocketException)
            {
                break;
            }
        }

        await CloseQuietlyAsync(downstream);
        await CloseQuietlyAsync(upstream);
    }

    private static async Task CloseQuietlyAsync(WebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "stream ended", timeout.Token);
        }
        catch
        {
            // Closing is best effort; the socket is being discarded either way.
        }
    }
}
