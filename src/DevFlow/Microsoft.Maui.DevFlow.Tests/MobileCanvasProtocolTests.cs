using System.Net;
using System.Text;
using Microsoft.Maui.DevFlow.Devices;

namespace Microsoft.Maui.DevFlow.Tests;

/// <summary>
/// Contract tests against a stub that speaks the external device host's real wire protocol.
/// <para>
/// These exist because the adapter's failure mode is silence: a wrong field name, a wrong route,
/// or a missing credential makes every request fail in a way that is indistinguishable from
/// "no device layer installed". Degradation tests alone cannot catch that, because a broken
/// adapter degrades beautifully. Only a successful authenticated round trip proves the binding.
/// </para>
/// </summary>
public class MobileCanvasProtocolTests : IAsyncLifetime
{
    private HttpListener _listener = null!;
    private string _controlToken = "test-control-token";
    private int _port;
    private readonly List<(string Path, string? Authorization)> _requests = [];

    public Task InitializeAsync()
    {
        _port = GetFreePort();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { _listener.Stop(); } catch { }
        return Task.CompletedTask;
    }

    private static int GetFreePort()
    {
        using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch { return; }

            // ASP.NET Core decodes route parameters, so the real host sees the raw device id.
            var path = Uri.UnescapeDataString(context.Request.Url?.AbsolutePath ?? "/");
            var authorization = context.Request.Headers["Authorization"];
            lock (_requests) _requests.Add((path, authorization));

            // The real host authenticates every control request with a bearer token.
            if (authorization != $"Bearer {_controlToken}")
            {
                context.Response.StatusCode = 401;
                context.Response.Close();
                continue;
            }

            var (status, body) = path switch
            {
                "/api/v1/status" => (200, """{"status":"ok","version":"0.1.6","processId":42}"""),
                "/api/v1/devices" => (200, """
                    [{"id":"ios:A1B2","platform":"ios","nativeId":"A1B2","udid":"A1B2","name":"iPhone 16",
                      "state":"booted","isAvailable":true,
                      "display":{"pixelWidth":1170,"pixelHeight":2532,"pointWidth":390,"pointHeight":844,"scale":3,"orientation":"portrait"},
                      "capabilities":{"tap":true,"screenshot":true,"liveStream":true,"boot":true}}]
                    """),
                "/api/v1/devices/ios:A1B2/boot" => (200, "{}"),
                _ => (404, """{"error":"not found"}"""),
            };

            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    private MobileCanvasDeviceSurface CreateSurface(string? token = null, string? schemaVersion = "1.0") =>
        new(stateProvider: () => new MobileCanvasHostState
        {
            Port = _port,
            ProcessId = 42,
            Version = "0.1.6",
            ControlToken = token ?? _controlToken,
            SchemaVersion = schemaVersion,
        });

    [Fact]
    public async Task Health_SucceedsAgainstTheRealStatusRoute()
    {
        using var surface = CreateSurface();

        var health = await surface.GetHealthAsync();

        Assert.True(health.Available);
        Assert.Equal(DeviceHostAvailability.Available, health.Availability);

        // Pin the route: the host serves /status, not /health.
        lock (_requests)
            Assert.Contains(_requests, r => r.Path == "/api/v1/status");
    }

    [Fact]
    public async Task EveryRequest_CarriesTheControlToken()
    {
        using var surface = CreateSurface();

        await surface.GetHealthAsync();
        await surface.ListAsync();

        lock (_requests)
            Assert.All(_requests, r => Assert.Equal($"Bearer {_controlToken}", r.Authorization));
    }

    [Fact]
    public async Task ListDevices_DeserialisesTheRealPayload()
    {
        using var surface = CreateSurface();

        var devices = await surface.ListAsync();

        var device = Assert.Single(devices);
        Assert.Equal("ios:A1B2", device.Id);
        Assert.Equal("A1B2", device.NativeId);
        Assert.Equal("iPhone 16", device.Name);
        Assert.True(device.IsBooted);
        Assert.True(device.Capabilities.Tap);
        Assert.True(device.Capabilities.LiveStream);
        Assert.Equal(390, device.Display!.PointWidth);
        Assert.Equal(3, device.Display.Scale);
    }

    [Fact]
    public async Task PairedDevice_BuildsAUsableCoordinateSpace()
    {
        // The end-to-end point: a device discovered over the wire must yield a coordinate space
        // that can actually place a tap.
        using var surface = CreateSurface();
        var device = Assert.Single(await surface.ListAsync());

        var space = DeviceCoordinateSpace.FromDisplay(device.Display!);
        var app = space.FrameToApp(space.AppToFrame(new AppPoint(100, 200)));

        Assert.NotNull(app);
        Assert.Equal(100, app!.Value.X, 1e-9);
        Assert.Equal(200, app.Value.Y, 1e-9);
    }

    [Fact]
    public async Task Boot_SucceedsAgainstTheRealRoute()
    {
        using var surface = CreateSurface();

        var result = await surface.BootAsync("ios:A1B2");

        Assert.True(result.Success);
    }

    [Fact]
    public async Task WrongToken_ReportsUnauthorized_NotAbsent()
    {
        // The critical distinction: an authentication failure must not impersonate "no device
        // layer installed", or a fixable integration break silently presents as a missing feature.
        using var surface = CreateSurface(token: "wrong-token");

        var health = await surface.GetHealthAsync();

        Assert.False(health.Available);
        Assert.Equal(DeviceHostAvailability.Unauthorized, health.Availability);
        Assert.Contains("control token", health.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WrongToken_ReportsRejection_OnControlOperations()
    {
        using var surface = CreateSurface(token: "wrong-token");

        var result = await surface.BootAsync("ios:A1B2");

        Assert.False(result.Success);
        Assert.Contains("control token", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnsupportedProtocolMajor_ReportsIncompatible_WithoutCallingTheHost()
    {
        using var surface = CreateSurface(schemaVersion: "2.0");

        var health = await surface.GetHealthAsync();

        Assert.Equal(DeviceHostAvailability.Incompatible, health.Availability);
        Assert.Contains("2.0", health.Reason);

        // Refusing before making a request is deliberate: driving a protocol we do not understand
        // risks silently wrong device control rather than a clean failure.
        lock (_requests)
            Assert.DoesNotContain(_requests, r => r.Path == "/api/v1/status");
    }

    [Fact]
    public async Task MissingProtocolVersion_IsAssumedCompatible()
    {
        // An older host predates schema stamping; the routes we use are long-standing, so refusing
        // to work with it would be needlessly strict.
        using var surface = CreateSurface(schemaVersion: null);

        var health = await surface.GetHealthAsync();

        Assert.True(health.Available);
    }

    [Theory]
    [InlineData("1.0", true)]
    [InlineData("1.4", true)]
    [InlineData("1", true)]
    [InlineData("2.0", false)]
    [InlineData("0.9", false)]
    [InlineData("nonsense", false)]
    public void ProtocolSupport_TracksTheMajorVersion(string reported, bool supported)
    {
        Assert.Equal(supported, MobileCanvasProtocol.IsSupported(reported));
    }
}
