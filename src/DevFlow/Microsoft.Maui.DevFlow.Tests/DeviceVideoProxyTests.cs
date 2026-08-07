using System.Net;
using System.Net.WebSockets;
using Microsoft.Maui.Cli.DevFlow.Broker;

namespace Microsoft.Maui.DevFlow.Tests;

/// <summary>
/// Access control on the device video proxy.
/// <para>
/// A WebSocket is not subject to the same-origin policy, so any page a user visits can open one to
/// localhost. Without both gates below, a hostile page could receive a live feed of the user's
/// device screen — with the broker attaching the device host's bearer token on its behalf. A
/// review found exactly that hole, so it is pinned here.
/// </para>
/// </summary>
public class DeviceVideoProxyTests : IAsyncLifetime
{
    private BrokerServer _broker = null!;
    private CancellationTokenSource _cts = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        _port = GetFreePort();
        _broker = new BrokerServer(_port);
        _cts = new CancellationTokenSource();
        _ = _broker.RunAsync(_cts.Token);
        await WaitForBrokerAsync(_port);
    }

    public Task DisposeAsync()
    {
        _cts.Cancel();
        _broker.Dispose();
        return Task.CompletedTask;
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForBrokerAsync(int port)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        for (var i = 0; i < 50; i++)
        {
            try
            {
                var response = await http.GetAsync($"http://localhost:{port}/api/health");
                if (response.IsSuccessStatusCode) return;
            }
            catch { }
            await Task.Delay(100);
        }
    }

    private async Task<WebSocketException?> ConnectAsync(string query, string? origin)
    {
        using var socket = new ClientWebSocket();
        if (origin is not null)
            socket.Options.SetRequestHeader("Origin", origin);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await socket.ConnectAsync(new Uri($"ws://localhost:{_port}/ws/video?{query}"), timeout.Token);
            return null;
        }
        catch (WebSocketException ex)
        {
            return ex;
        }
    }

    [Fact]
    public async Task RejectsAForeignOrigin()
    {
        // The critical case: a page on the open internet must not be able to open this socket.
        var error = await ConnectAsync("deviceId=emulator-5554", origin: "https://evil.example");

        Assert.NotNull(error);
        Assert.Contains("403", error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsALocalPageWithoutTheEmbedToken()
    {
        // A loopback origin proves the caller is local, not that it is an Inspector session. Any
        // other local page — a dev server, a docs site on localhost — must still be refused.
        var error = await ConnectAsync("deviceId=emulator-5554", origin: $"http://localhost:{_port}");

        Assert.NotNull(error);
        Assert.Contains("403", error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsAWrongEmbedToken()
    {
        var error = await ConnectAsync(
            "deviceId=emulator-5554&embed=not-the-token",
            origin: $"http://localhost:{_port}");

        Assert.NotNull(error);
        Assert.Contains("403", error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsARequestWithNoOriginAtAll()
    {
        // A non-browser client has no business on the video channel; it has the CLI and MCP.
        var error = await ConnectAsync("deviceId=emulator-5554&embed=x", origin: null);

        Assert.NotNull(error);
    }
}
