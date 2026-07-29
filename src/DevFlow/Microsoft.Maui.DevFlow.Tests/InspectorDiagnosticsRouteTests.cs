using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.Cli.DevFlow.Inspector;
using Microsoft.Maui.DevFlow.Driver;

namespace Microsoft.Maui.DevFlow.Tests;

/// <summary>
/// Web Inspector coverage for the on-demand diagnostics routes.
///
/// The layout scan is a read (available during a replay); the performance session is not (starting
/// a profiler mid-replay would perturb the run being replayed). Both expose more than the visible
/// tree, so both must be read-token gated.
/// </summary>
public class InspectorDiagnosticsRouteTests
{
    [Fact]
    public void DiagnosticsRoutesAreTokenGated()
    {
        Assert.True(InspectorServer.IsTokenGatedPath("/api/diagnostics/layout"));
        Assert.True(InspectorServer.IsTokenGatedPath("/api/performance/start"));
        Assert.True(InspectorServer.IsTokenGatedPath("/api/performance/snapshot"));
        Assert.True(InspectorServer.IsTokenGatedPath("/api/performance/stop"));
    }

    [Fact]
    public void LayoutScanIsAReadThatStaysAvailableDuringReplay()
    {
        Assert.False(InspectorServer.IsMutation("/api/diagnostics/layout"));
        Assert.False(InspectorServer.IsBlockedDuringReplay("/api/diagnostics/layout"));
    }

    [Fact]
    public void PerformanceSessionControlIsBlockedDuringReplay()
    {
        Assert.True(InspectorServer.IsBlockedDuringReplay("/api/performance/start"));
        Assert.True(InspectorServer.IsBlockedDuringReplay("/api/performance/stop"));
        // Reading the current numbers does not change agent state.
        Assert.False(InspectorServer.IsBlockedDuringReplay("/api/performance/snapshot"));
        // Neither claims the UI writer lease: they never touch the app's UI.
        Assert.False(InspectorServer.IsMutation("/api/performance/start"));
        Assert.False(InspectorServer.IsMutation("/api/performance/stop"));
    }

    [Fact]
    public async Task LayoutRoute_WithoutTheReadToken_IsForbidden()
    {
        await using var agent = new FakeDiagnosticsAgent();
        var port = FreePort();
        var inspector = new InspectorServer(port, "127.0.0.1", agent.Port);
        inspector.Start();
        try
        {
            using var http = new HttpClient();
            var response = await http.PostAsync($"http://127.0.0.1:{port}/api/diagnostics/layout", Json("{}"));

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally
        {
            await inspector.StopAsync();
        }
    }

    [Fact]
    public async Task LayoutRoute_ReturnsTheReportTheTabRenders()
    {
        await using var agent = new FakeDiagnosticsAgent();
        var port = FreePort();
        var inspector = new InspectorServer(port, "127.0.0.1", agent.Port);
        inspector.Start();
        try
        {
            using var http = CreateTokenClient(inspector);
            var response = await http.PostAsync($"http://127.0.0.1:{port}/api/diagnostics/layout",
                Json("{\"maxElements\":50}"));
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
            var report = body.RootElement.GetProperty("report");
            Assert.Equal(1, report.GetProperty("summary").GetProperty("violations").GetInt32());
            Assert.NotEmpty(report.GetProperty("coverage").GetProperty("limitations").EnumerateArray());
            Assert.Contains("maxElements=50", agent.LastLayoutQuery, StringComparison.Ordinal);
        }
        finally
        {
            await inspector.StopAsync();
        }
    }

    [Fact]
    public async Task PerformanceSnapshotRoute_ReturnsATriageSummaryWithTaintMetadata()
    {
        await using var agent = new FakeDiagnosticsAgent();
        var port = FreePort();
        var inspector = new InspectorServer(port, "127.0.0.1", agent.Port);
        inspector.Start();
        try
        {
            using var http = CreateTokenClient(inspector);
            var response = await http.PostAsync($"http://127.0.0.1:{port}/api/performance/snapshot", Json("{}"));
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var summary = body.RootElement.GetProperty("summary");
            Assert.Equal("debug", summary.GetProperty("capability").GetProperty("mode").GetString());
            Assert.False(summary.GetProperty("capability").GetProperty("lowPerturbation").GetBoolean());
            // Estimated frame timings must never surface as a frame rate.
            Assert.False(summary.GetProperty("frames").GetProperty("supported").GetBoolean());
            Assert.True(summary.GetProperty("loss").GetProperty("anyLoss").GetBoolean());
            Assert.NotEmpty(summary.GetProperty("warnings").EnumerateArray());
        }
        finally
        {
            await inspector.StopAsync();
        }
    }

    [Fact]
    public async Task StopAsync_StopsOnlyTheInspectorOwnedProfilerSession()
    {
        await using var agent = new FakeDiagnosticsAgent();
        var port = FreePort();
        var inspector = new InspectorServer(port, "127.0.0.1", agent.Port);
        var client = (AgentClient)typeof(InspectorServer)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(inspector)!;
        client.AutoAcquireMutationLease = false;
        typeof(InspectorServer)
            .GetField("_performanceSessionId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(inspector, "s1");
        typeof(InspectorServer)
            .GetField("_performanceLeaseId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(inspector, "browser-lease");
        inspector.Start();

        await inspector.StopAsync();
        inspector.Dispose();

        Assert.Contains("/api/v1/profiler/sessions/s1", agent.LastProfilerStopPath, StringComparison.Ordinal);
        Assert.Equal("browser-lease", agent.LastProfilerStopLease);
    }

    [Fact]
    public async Task TransientStopFailure_PreservesInspectorProfilerOwnership()
    {
        await using var agent = new FakeDiagnosticsAgent { FailProfilerStop = true };
        var port = FreePort();
        var inspector = new InspectorServer(port, "127.0.0.1", agent.Port);
        var client = (AgentClient)typeof(InspectorServer)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(inspector)!;
        client.AutoAcquireMutationLease = false;
        typeof(InspectorServer)
            .GetField("_performanceSessionId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(inspector, "s1");
        typeof(InspectorServer)
            .GetField("_performanceLeaseId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(inspector, "browser-lease");
        inspector.Start();
        try
        {
            using var http = CreateTokenClient(inspector);
            var response = await http.PostAsync(
                $"http://127.0.0.1:{port}/api/performance/stop",
                Json("{}"));
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.False(body.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(
                "s1",
                typeof(InspectorServer)
                    .GetField("_performanceSessionId", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(inspector));
        }
        finally
        {
            agent.FailProfilerStop = false;
            await inspector.StopAsync();
            inspector.Dispose();
        }
    }

    [Fact]
    public async Task TransientSnapshotFailure_PreservesInspectorProfilerOwnership()
    {
        await using var agent = new FakeDiagnosticsAgent { FailProfilerStatus = true };
        var port = FreePort();
        var inspector = new InspectorServer(port, "127.0.0.1", agent.Port);
        var client = (AgentClient)typeof(InspectorServer)
            .GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(inspector)!;
        client.AutoAcquireMutationLease = false;
        typeof(InspectorServer)
            .GetField("_performanceSessionId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(inspector, "s1");
        inspector.Start();
        try
        {
            using var http = CreateTokenClient(inspector);
            var response = await http.PostAsync(
                $"http://127.0.0.1:{port}/api/performance/snapshot",
                Json("{}"));
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.False(body.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(
                "s1",
                typeof(InspectorServer)
                    .GetField("_performanceSessionId", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(inspector));
        }
        finally
        {
            agent.FailProfilerStatus = false;
            await inspector.StopAsync();
            inspector.Dispose();
        }
    }

    private static HttpClient CreateTokenClient(InspectorServer inspector)
    {
        var token = (string)typeof(InspectorServer)
            .GetField("_readToken", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(inspector)!;
        var http = new HttpClient();
        http.DefaultRequestHeaders.Add("X-DevFlow-Inspector-Token", token);
        return http;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Loopback agent answering the layout and profiler reads the diagnostics tabs make.</summary>
    private sealed class FakeDiagnosticsAgent : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private string _lastLayoutQuery = "";
        private string _lastProfilerStopPath = "";
        private string _lastProfilerStopLease = "";

        public FakeDiagnosticsAgent()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _loop = Loop(_cts.Token);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public bool FailProfilerStop { get; set; }
        public bool FailProfilerStatus { get; set; }
        public string LastLayoutQuery => Volatile.Read(ref _lastLayoutQuery);
        public string LastProfilerStopPath => Volatile.Read(ref _lastProfilerStopPath);
        public string LastProfilerStopLease => Volatile.Read(ref _lastProfilerStopLease);

        private async Task Loop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(ct); }
                catch { break; }
                _ = Handle(client, ct);
            }
        }

        private async Task Handle(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var request = await ReadRequest(stream, ct);
                    if (request.Path is null) return;
                    var path = request.Path;

                    if (path.StartsWith("/api/v1/ui/diagnostics/layout", StringComparison.Ordinal))
                        Volatile.Write(ref _lastLayoutQuery, path);
                    if (path.StartsWith("/api/v1/profiler/sessions/", StringComparison.Ordinal) &&
                        !path.Contains("/samples", StringComparison.Ordinal))
                    {
                        Volatile.Write(ref _lastProfilerStopPath, path);
                        Volatile.Write(ref _lastProfilerStopLease, request.LeaseId ?? "");
                    }

                    var json = path switch
                    {
                        var p when p.StartsWith("/api/v1/ui/diagnostics/layout", StringComparison.Ordinal) => LayoutReport,
                        var p when p.StartsWith("/api/v1/profiler/capabilities", StringComparison.Ordinal) => ProfilerCapabilities,
                        var p when p.Contains("/samples", StringComparison.Ordinal) => ProfilerBatch,
                        var p when p.StartsWith("/api/v1/profiler/hotspots", StringComparison.Ordinal) => "[]",
                        var p when p.StartsWith("/api/v1/profiler/sessions/", StringComparison.Ordinal) =>
                            FailProfilerStop
                                ? "{}"
                                : """{"session":{"sessionId":"s1","startedAtUtc":"2026-07-29T10:00:00Z","sampleIntervalMs":250,"isActive":false}}""",
                        var p when p.StartsWith("/api/v1/agent/status", StringComparison.Ordinal) =>
                            FailProfilerStatus ? "not-json" : AgentStatus,
                        _ => "{}",
                    };

                    await WriteAsync(stream, Encoding.UTF8.GetBytes(json), ct);
                }
                catch
                {
                    // Transport failures surface as "agent unavailable" in the inspector.
                }
            }
        }

        private const string LayoutReport = """
            {"schemaVersion":"1.0","ruleSetVersion":"1.0","capturedUtc":"2026-07-29T10:00:00.000Z","platform":"Windows",
             "scope":{"maxElements":50,"elementsExamined":1,"truncated":false},
             "coverage":{"overall":"partial","rules":[{"ruleId":"layout.visible-zero-area","support":"full","confidence":"high","evaluated":1,"skipped":0,"limitations":[]}],
             "limitations":["Managed MAUI layout state only."],"neverCaptured":["Element Text/Value content"]},
             "summary":{"violations":1,"observations":0,"incomplete":0},
             "findings":[{"id":"layout.visible-zero-area:e1:area","ruleId":"layout.visible-zero-area","outcome":"violation","confidence":"high",
             "message":"Label was arranged with no area.","explanation":"A realized element with no area cannot draw.",
             "element":{"id":"e1","type":"Label"},"limitations":[]}]}
            """;

        private const string ProfilerCapabilities = """
            {"available":true,"featureEnabled":true,"platform":"Windows","managedMemorySupported":true,
             "gcSupported":true,"cpuPercentSupported":true,"threadCountSupported":true,
             "frameTimingsEstimated":true,"nativeFrameTimingsSupported":false}
            """;

        private const string ProfilerBatch = """
            {"sessionId":"s1","isActive":true,
             "samples":[{"tsUtc":"2026-07-29T10:00:00Z","managedBytes":100,"gc0":0,"gc1":0,"gc2":0,"frameSource":"estimated","frameQuality":"low"}],
             "markers":[],"spans":[],
             "sampleMetadata":{"oldestCursor":5,"latestCursor":6,"lostCount":5,"availableCount":1},
             "markerMetadata":{"lostCount":0},"spanMetadata":{"lostCount":0}}
            """;

        private const string AgentStatus = """
            {"agent":{"version":"0.1.0","mode":"debug","readOnly":false},"device":{"platform":"Windows"},"app":{"name":"Sample"}}
            """;

        private static async Task<(string? Path, string? LeaseId)> ReadRequest(
            NetworkStream stream,
            CancellationToken ct)
        {
            var buffer = new byte[8192];
            var read = await stream.ReadAsync(buffer, ct);
            if (read <= 0) return (null, null);
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            var firstLine = text.Split("\r\n")[0].Split(' ');
            var lease = text.Split("\r\n")
                .FirstOrDefault(line => line.StartsWith(
                    "X-DevFlow-Lease:",
                    StringComparison.OrdinalIgnoreCase))
                ?.Split(':', 2)[1]
                .Trim();
            return (firstLine.Length >= 2 ? firstLine[1] : null, lease);
        }

        private static async Task WriteAsync(NetworkStream stream, byte[] body, CancellationToken ct)
        {
            var header = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(header), ct);
            await stream.WriteAsync(body, ct);
            await stream.FlushAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try { await _loop; } catch { }
            _cts.Dispose();
        }
    }
}
