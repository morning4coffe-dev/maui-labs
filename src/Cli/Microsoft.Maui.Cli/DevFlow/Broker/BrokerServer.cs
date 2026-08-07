using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Web;
using Microsoft.Maui.Cli.DevFlow.Inspector;
using Microsoft.Maui.DevFlow.Devices;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.Cli.DevFlow.Broker;

/// <summary>
/// Central broker daemon that manages agent registration and port assignment.
/// Agents connect via WebSocket; CLI queries via HTTP.
/// </summary>
public class BrokerServer : IDisposable
{
    public const int DefaultPort = 19223;
    public const int PortRangeStart = 10223;
    public const int PortRangeEnd = 10899;
    private const int MaxRecordingRequestChars = 512 * 1024;

    private readonly int _port;
    private readonly TimeSpan _idleTimeout;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<string, AgentConnection> _agents = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _agentRouteGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _agentStateGates = new(StringComparer.Ordinal);
    private readonly MutationLeaseRegistry _mutationLeases;
    private readonly DeviceRegistry _devices = new();
    private readonly BrokerFlowCoordinator _flows;
    private readonly RouteCheckpointCoordinator _checkpoints;
    private readonly WorkflowRunCoordinator _workflowRuns;
    private readonly TestAgentSessionService _testAgentSessions;
    private readonly ArtifactTrustImportService _artifactTrustImports;
    private readonly ArtifactTrustStore _artifactTrustStore;
    private readonly WorkflowRepairProposalStore _workflowRepairs;
    private readonly WorkflowXamlSourceProposalStore _workflowXamlSources;
    private readonly WorkflowCSharpSourceProposalStore _workflowCSharpSources;
    private readonly HashSet<int> _assignedPorts = new();
    private readonly object _portLock = new();
    private DateTime _lastActivity = DateTime.UtcNow;
    private Timer? _idleTimer;
    private bool _disposed;
    private Action<string>? _log;

    public int Port => _port;
    public int AgentCount => _agents.Count;
    public bool IsRunning => _listener?.IsListening ?? false;

    public BrokerServer(
        int port = DefaultPort,
        TimeSpan? idleTimeout = null,
        Action<string>? log = null)
        : this(
            port,
            idleTimeout,
            log,
            checkpointStore: null,
            recordingStorageRoot: null,
            clock: null)
    {
    }

    internal BrokerServer(
        int port,
        TimeSpan? idleTimeout,
        Action<string>? log,
        RouteCheckpointStore? checkpointStore,
        string? recordingStorageRoot,
        TimeProvider? clock)
    {
        _port = port;
        _idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(5);
        _log = log;
        _flows = new BrokerFlowCoordinator(
            new FlowRecordingStore(clock),
            new FlowRecordingSpoolStore(recordingStorageRoot, clock, warning => Log("Warning: " + warning)));
        _checkpoints = new RouteCheckpointCoordinator(checkpointStore);
        _mutationLeases = new MutationLeaseRegistry();
        _workflowRuns = new WorkflowRunCoordinator(
            _mutationLeases,
            ExecuteWorkflowRunAsync,
            new WorkflowRunCoordinatorOptions
            {
                ArtifactRoot = string.IsNullOrWhiteSpace(recordingStorageRoot)
                    ? null
                    : Path.Combine(recordingStorageRoot, "workflow-runs"),
            },
            clock: clock,
            controlLedger: ControlWorkflowRunLedgerAsync);
        _testAgentSessions = new TestAgentSessionService(clock: clock);
        _artifactTrustImports = new ArtifactTrustImportService(clock);
        _artifactTrustStore = new ArtifactTrustStore(clock: clock);
        _workflowRepairs = new WorkflowRepairProposalStore(clock: clock);
        _workflowXamlSources = new WorkflowXamlSourceProposalStore(clock: clock);
        _workflowCSharpSources = new WorkflowCSharpSourceProposalStore(clock: clock);
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{_port}/");
        // Also accept a literal 127.0.0.1 Host header: HTTP.sys routes by the request's Host,
        // and a bare "localhost" prefix rejects "127.0.0.1" with 400 "Invalid Hostname". Some
        // local clients connect with a 127.0.0.1 Host, so register the loopback IP too.
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException)
        {
            // The 127.0.0.1 prefix can require a URL ACL reservation on some machines, whereas the
            // "localhost" prefix is always permitted without elevation. Retry with localhost only
            // before resorting to the strong wildcard (which itself needs elevation), so adding the
            // loopback-IP prefix can never regress startup on a stock machine.
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{_port}/");
                _listener.Start();
            }
            catch (HttpListenerException)
            {
                // Last-resort fallback for platforms where the loopback prefixes don't bind.
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://+:{_port}/");
                _listener.Start();
            }
        }

        Log($"Broker started on port {_port} (PID {Environment.ProcessId})");

        // Write state file
        WriteBrokerState();

        // Start idle timer
        _idleTimer = new Timer(_ => CheckIdle(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().WaitAsync(_cts.Token);
                _ = HandleRequestAsync(context);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            Shutdown();
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        TouchActivity();

        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            var method = context.Request.HttpMethod;

            // Defense-in-depth: the broker is designed to be reachable only on
            // loopback, but HttpListener falls back to binding on all interfaces
            // (http://+:port/) when localhost reservation fails — see line 56-60
            // below. In that fallback, non-browser HTTP clients on the LAN (curl,
            // scripts, attacker) can reach this port without sending an Origin
            // header, so the Origin check alone (further down) doesn't help.
            // Reject any caller whose RemoteEndPoint isn't a loopback address.
            // Legitimate uses (CLI tool, inspector UI in a local browser, MAUI
            // agent running on the same machine, Android emulator port-forwarded
            // back to host loopback) all use 127.0.0.1 or ::1.
            var remoteIp = context.Request.RemoteEndPoint?.Address;
            if (remoteIp == null || !IPAddress.IsLoopback(remoteIp))
            {
                context.Response.StatusCode = 403;
                context.Response.ContentType = "text/plain";
                var msg = Encoding.UTF8.GetBytes("Forbidden: loopback required");
                context.Response.ContentLength64 = msg.Length;
                await context.Response.OutputStream.WriteAsync(msg);
                context.Response.Close();
                return;
            }

            // WebSocket upgrade for agents
            if (context.Request.IsWebSocketRequest && path == "/ws/agent")
            {
                await HandleAgentWebSocket(context);
                return;
            }

            // WebSocket upgrade for inspector event relay
            if (context.Request.IsWebSocketRequest && path.StartsWith("/inspector", StringComparison.OrdinalIgnoreCase))
            {
                await HandleInspectorRoute(context, path);
                return;
            }

            // HTTP endpoints for CLI
            // Block state-mutating endpoints from non-loopback origins BEFORE dispatching
            // the handler — otherwise a cross-origin POST to /api/shutdown would still
            // tear down the broker even though we return 403.
            var origin = context.Request.Headers["Origin"];
            if (method == "POST" && !LocalOriginValidator.IsAllowed(origin, _port))
            {
                context.Response.StatusCode = 403;
                context.Response.ContentType = "application/json";
                var forbidden = Encoding.UTF8.GetBytes(CliJson.SerializeUntyped(new JsonObject { ["error"] = "Forbidden origin" }, indented: false));
                context.Response.ContentLength64 = forbidden.Length;
                await context.Response.OutputStream.WriteAsync(forbidden);
                context.Response.Close();
                return;
            }

            if (path.StartsWith("/api/leases/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleMutationLeaseRoute(context, method, path);
                return;
            }
            if (path.StartsWith("/api/recordings/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleRecordingRoute(context, method, path);
                return;
            }
            if (path.StartsWith("/api/checkpoints/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleCheckpointRoute(context, method, path);
                return;
            }
            if (path.StartsWith("/api/workflow-runs", StringComparison.OrdinalIgnoreCase))
            {
                await HandleWorkflowRunRoute(context, method, path);
                return;
            }
            if (path.StartsWith("/api/test-agent", StringComparison.OrdinalIgnoreCase))
            {
                await HandleTestAgentRoute(context, method, path);
                return;
            }
            if (path.StartsWith("/api/devices", StringComparison.OrdinalIgnoreCase))
            {
                await HandleDeviceRoute(context, method, path);
                return;
            }
            if (path.StartsWith("/api/artifact-trust", StringComparison.OrdinalIgnoreCase))
            {
                await HandleArtifactTrustRoute(context, method, path);
                return;
            }

            var (statusCode, body) = (method, path) switch
            {
                ("GET", "/api/health") => (200, CliJson.SerializeUntyped(new JsonObject
                {
                    ["status"] = "ok",
                    ["agents"] = _agents.Count
                }, indented: false)),
                ("GET", "/api/agents") => (200, HandleListAgents()),
                ("POST", "/api/shutdown") => HandleShutdown(),
                // Browsers auto-request /favicon.ico; answer 204 so the inspector page doesn't log a 404.
                ("GET", "/favicon.ico") => (204, ""),
                _ => (0, "") // handled below for inspector routes
            };

            // Inspector routes — serve the web inspector for connected agents
            if (statusCode == 0)
            {
                if (path.StartsWith("/inspector", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleInspectorRoute(context, path);
                    return;
                }

                statusCode = 404;
                body = CliJson.SerializeUntyped(new JsonObject { ["error"] = "Not found" }, indented: false);
            }

            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";

            // Mirror Origin only for loopback callers; the previous wildcard let any web
            // page read /api/agents (leaking IDs) and POST /api/shutdown.
            if (LocalOriginValidator.IsAllowed(origin, _port) && !string.IsNullOrEmpty(origin) && origin != "null")
            {
                context.Response.Headers.Add("Access-Control-Allow-Origin", origin);
                context.Response.Headers.Add("Vary", "Origin");
            }

            var responseBytes = Encoding.UTF8.GetBytes(body);
            context.Response.ContentLength64 = responseBytes.Length;
            await context.Response.OutputStream.WriteAsync(responseBytes);
            context.Response.Close();
        }
        catch (Exception ex)
        {
            Log($"Error handling request: {ex.Message}");
            try { context.Response.Close(); } catch { }
        }
    }

    private async Task HandleAgentWebSocket(HttpListenerContext context)
    {
        // Reject cross-origin WebSocket connections; only the local agent process
        // or CLI tools (no Origin header) may register.
        var origin = context.Request.Headers["Origin"];
        if (!LocalOriginValidator.IsAllowed(origin, _port))
        {
            context.Response.StatusCode = 403;
            context.Response.Close();
            return;
        }

        WebSocketContext wsContext;
        try
        {
            wsContext = await context.AcceptWebSocketAsync(null);
        }
        catch (Exception ex)
        {
            Log($"WebSocket accept failed: {ex.Message}");
            context.Response.StatusCode = 500;
            context.Response.Close();
            return;
        }

        var ws = wsContext.WebSocket;
        var buffer = new byte[4096];
        AgentConnection? publishedConnection = null;

        try
        {
            // Read registration message
            var result = await ws.ReceiveAsync(buffer, _cts?.Token ?? CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close) return;

            var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var registration = CliJson.Deserialize<RegistrationMessage>(message);
            if (registration == null || registration.Type != "register")
            {
                await ws.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "Expected register message", CancellationToken.None);
                return;
            }

            var id = AgentRegistration.ComputeId(
                registration.PackageId ?? registration.Project,
                registration.Tfm,
                registration.SessionId,
                registration.ProcessId);

            // If the agent already has an HTTP listener (late reconnection), use its current port
            int assignedPort;
            if (registration.CurrentPort is > 0)
            {
                assignedPort = registration.CurrentPort.Value;
            }
            else
            {
                var newPort = AssignPort();
                if (newPort == null)
                {
                    var errorMsg = CliJson.SerializeUntyped(new JsonObject
                    {
                        ["type"] = "error",
                        ["message"] = "No ports available"
                    }, indented: false);
                    await ws.SendAsync(Encoding.UTF8.GetBytes(errorMsg), WebSocketMessageType.Text, true, CancellationToken.None);
                    await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, "No ports available", CancellationToken.None);
                    return;
                }
                assignedPort = newPort.Value;
            }

            var agent = new AgentRegistration
            {
                Id = id,
                Project = registration.Project,
                Tfm = registration.Tfm,
                Platform = registration.Platform,
                AppName = registration.AppName,
                PackageId = registration.PackageId,
                DeviceId = registration.DeviceId,
                Port = assignedPort,
                Version = registration.Version,
                SessionId = registration.SessionId,
                ProcessId = registration.ProcessId,
                InstanceId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
                ConnectedAt = DateTime.UtcNow
            };

            var connection = new AgentConnection(agent, ws);
            var routeGate = AgentRouteGate(id);
            await routeGate.WaitAsync(_cts?.Token ?? CancellationToken.None);
            try
            {
                var replaced = ReplaceConnection(_agents, id, connection);
                if (_inspectors.TryRemove(id, out var staleInspector))
                    staleInspector.Dispose();
                if (replaced is not null)
                {
                    _workflowRuns.MarkAgentInstanceUnavailable(
                        replaced.Registration.Id,
                        replaced.Registration.InstanceId,
                        "The agent reconnected with a new instance.");
                    if (replaced.Registration.Port != assignedPort)
                        ReleasePort(replaced.Registration.Port);
                    try { replaced.WebSocket.Dispose(); } catch { }
                    Log($"Agent replaced: {agent.AppName}|{agent.Tfm} (was port {replaced.Registration.Port})");
                }
            }
            finally
            {
                routeGate.Release();
            }
            publishedConnection = connection;

            Log($"Agent connected: {agent.AppName}|{agent.Tfm} → port {assignedPort} (id: {id})");

            // Send registration response
            var response = CliJson.SerializeUntyped(new JsonObject
            {
                ["type"] = "registered",
                ["id"] = id,
                ["port"] = assignedPort,
                ["instanceId"] = agent.InstanceId
            }, indented: false);
            await ws.SendAsync(Encoding.UTF8.GetBytes(response), WebSocketMessageType.Text, true, CancellationToken.None);

            // Keep connection alive — wait for disconnect
            await MonitorAgentConnection(connection);
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
        finally
        {
            if (publishedConnection is not null)
                await CleanupAgentConnectionAsync(publishedConnection);
            ws.Dispose();
        }
    }

    internal static TConnection? ReplaceConnection<TConnection>(
        ConcurrentDictionary<string, TConnection> connections,
        string id,
        TConnection connection)
        where TConnection : class
    {
        while (true)
        {
            if (connections.TryGetValue(id, out var existing))
            {
                if (connections.TryUpdate(id, connection, existing))
                    return existing;
                continue;
            }

            if (connections.TryAdd(id, connection))
                return null;
        }
    }

    private async Task MonitorAgentConnection(AgentConnection connection)
    {
        var buffer = new byte[256];
        try
        {
            while (connection.WebSocket.State == WebSocketState.Open && !(_cts?.Token.IsCancellationRequested ?? true))
            {
                var result = await connection.WebSocket.ReceiveAsync(buffer, _cts?.Token ?? CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) break;
                TouchActivity();
            }
        }
        catch { }
    }

    private async Task CleanupAgentConnectionAsync(AgentConnection connection)
    {
        // Use the KeyValuePair overload so a reconnecting agent that re-registered with the same ID
        // cannot be evicted by stale cleanup from the superseded socket.
        var routeGate = AgentRouteGate(connection.Registration.Id);
        await routeGate.WaitAsync();
        try
        {
            if (_agents.TryRemove(new KeyValuePair<string, AgentConnection>(connection.Registration.Id, connection)))
            {
                var stateGate = AgentStateGate(connection.Registration.Id);
                await stateGate.WaitAsync();
                try
                {
                    ReleasePort(connection.Registration.Port);
                    _mutationLeases.Remove(connection.Registration.Id);
                    _flows.RemoveAgent(connection.Registration.Id);
                    _workflowRuns.MarkAgentInstanceUnavailable(
                        connection.Registration.Id,
                        connection.Registration.InstanceId,
                        "The agent disconnected.");
                    if (_inspectors.TryRemove(connection.Registration.Id, out var inspector))
                        inspector.Dispose();
                    Log($"Agent disconnected: {connection.Registration.AppName}|{connection.Registration.Tfm}");
                }
                finally
                {
                    stateGate.Release();
                }
            }
        }
        finally
        {
            routeGate.Release();
        }
    }

    private SemaphoreSlim AgentRouteGate(string agentId)
        => _agentRouteGates.GetOrAdd(agentId, static _ => new SemaphoreSlim(1, 1));

    private SemaphoreSlim AgentStateGate(string agentId)
        => _agentStateGates.GetOrAdd(agentId, static _ => new SemaphoreSlim(1, 1));

    private string HandleListAgents()
    {
        var agents = _agents.Values.Select(c => c.Registration).ToArray();
        return CliJson.SerializeUntyped(agents, indented: true);
    }

    /// <summary>
    /// The device layer surface. Kept behind the broker so hosts have a single front door: a
    /// second front door is what produces duplicate device pickers and two competing ideas of
    /// which device is selected.
    /// </summary>
    private async Task HandleDeviceRoute(HttpListenerContext context, string method, string path)
    {
        var (statusCode, body) = await BuildDeviceResponse(context, method, path);

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private async Task<(int, string)> BuildDeviceResponse(HttpListenerContext context, string method, string path)
    {
        var trimmed = path.TrimEnd('/');

        if (trimmed.StartsWith("/api/devices/", StringComparison.OrdinalIgnoreCase))
            return await BuildDeviceControlResponse(context, method, trimmed);

        if (method == "GET" && trimmed.Equals("/api/devices", StringComparison.OrdinalIgnoreCase))
        {
            var health = await _devices.GetHealthAsync();
            var agents = _agents.Values.Select(c => c.Registration).ToArray();
            var paired = await _devices.ListPairedAsync(agents);

            var list = new JsonArray();
            foreach (var entry in paired)
            {
                list.Add(new JsonObject
                {
                    ["id"] = entry.Device.Id,
                    ["platform"] = entry.Device.Platform,
                    ["name"] = entry.Device.Name,
                    ["nativeId"] = entry.Device.NativeId,
                    ["state"] = entry.Device.State,
                    ["isBooted"] = entry.Device.IsBooted,
                    ["osVersion"] = entry.Device.OsVersion,
                    ["agentId"] = entry.AgentId,
                    ["agentPort"] = entry.AgentPort,
                    ["pairing"] = entry.MatchConfidence.ToString().ToLowerInvariant(),
                    ["capabilities"] = new JsonObject
                    {
                        ["tap"] = entry.Device.Capabilities.Tap,
                        ["screenshot"] = entry.Device.Capabilities.Screenshot,
                        ["liveStream"] = entry.Device.Capabilities.LiveStream,
                        ["recording"] = entry.Device.Capabilities.Recording,
                        ["boot"] = entry.Device.Capabilities.Boot,
                        ["rotate"] = entry.Device.Capabilities.Rotate,
                    },
                });
            }

            // The host's availability travels with the list so a caller can tell "no devices"
            // apart from "no device layer" — they need very different messages in the UI.
            return (200, CliJson.SerializeUntyped(new JsonObject
            {
                ["available"] = health.Available,
                ["reason"] = health.Reason,
                ["devices"] = list,
            }, indented: true));
        }

        return (404, CliJson.SerializeUntyped(new JsonObject { ["error"] = "Not found" }, indented: false));
    }

    /// <summary>
    /// Device control: <c>POST /api/devices/{id}/{action}</c>.
    /// <para>
    /// A refusal is a 200 with <c>success:false</c> and a reason rather than an HTTP error,
    /// because "this platform cannot do that" is an expected answer that callers must be able to
    /// show a human, not an exceptional condition.
    /// </para>
    /// </summary>
    private async Task<(int, string)> BuildDeviceControlResponse(HttpListenerContext context, string method, string trimmed)
    {
        if (method != "POST")
            return (405, CliJson.SerializeUntyped(new JsonObject { ["error"] = "Method not allowed" }, indented: false));

        var segments = trimmed["/api/devices/".Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2)
            return (404, CliJson.SerializeUntyped(new JsonObject { ["error"] = "Not found" }, indented: false));

        var deviceId = Uri.UnescapeDataString(segments[0]);
        var action = segments[1].ToLowerInvariant();

        DeviceOperationResult result;
        switch (action)
        {
            case "boot":
                result = await _devices.BootAsync(deviceId);
                break;
            case "shutdown":
                result = await _devices.ShutdownAsync(deviceId);
                break;
            case "tap":
                var query = context.Request.QueryString;
                var x = ParseDouble(query["x"]);
                var y = ParseDouble(query["y"]);
                if (x is null || y is null)
                {
                    result = DeviceOperationResult.Failed("A tap requires x and y coordinates.");
                    break;
                }

                // "validate" carries the caller's own lease id, which is what makes this an
                // authorization rather than a presence check: probing without one reports "held by
                // other" even to the session that holds it, refusing exactly the caller that
                // should be allowed.
                //
                // The gate is !HeldByOther rather than Allowed, because Allowed means strictly
                // "you hold it" — and an unclaimed device, which is the normal state before an app
                // launches, has no holder at all. Nobody holding it must not mean nobody may drive.
                var agents = _agents.Values.Select(c => c.Registration).ToArray();
                var leaseKey = await _devices.ResolveLeaseKeyAsync(deviceId, agents);
                var lease = _mutationLeases.Control(
                    leaseKey,
                    "validate",
                    context.Request.QueryString["leaseId"],
                    context.Request.QueryString["holderKind"],
                    context.Request.QueryString["label"],
                    force: false);

                if (lease.HeldByOther)
                {
                    result = DeviceOperationResult.Failed(
                        $"Another session ({lease.Label ?? lease.HolderKind ?? "unknown"}) is driving this device. "
                        + "Take control before sending input.");
                    break;
                }

                result = await _devices.TapAsync(deviceId, new DevicePoint(x.Value, y.Value));
                break;
            default:
                return (404, CliJson.SerializeUntyped(new JsonObject { ["error"] = $"Unknown device action '{action}'" }, indented: false));
        }

        return (200, CliJson.SerializeUntyped(new JsonObject
        {
            ["success"] = result.Success,
            ["reason"] = result.Reason,
        }, indented: false));
    }

    private static double? ParseDouble(string? value) =>
        double.TryParse(
            value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;

    private (int, string) HandleShutdown()
    {
        Log("Shutdown requested via API");
        _ = Task.Run(async () =>
        {
            await Task.Delay(100); // Let response send first
            _cts?.Cancel();
        });
        return (200, CliJson.SerializeUntyped(new JsonObject
        {
            ["status"] = "shutting_down"
        }, indented: false));
    }

    private int? AssignPort()
    {
        lock (_portLock)
        {
            for (int port = PortRangeStart; port <= PortRangeEnd; port++)
            {
                if (_assignedPorts.Contains(port)) continue;
                if (IsPortInUse(port)) continue;
                _assignedPorts.Add(port);
                return port;
            }
        }
        return null;
    }

    private void ReleasePort(int port)
    {
        lock (_portLock)
        {
            _assignedPorts.Remove(port);
        }
    }

    private static bool IsPortInUse(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return false;
        }
        catch
        {
            return true;
        }
    }

    private void TouchActivity() => _lastActivity = DateTime.UtcNow;

    private void CheckIdle()
    {
        if (_agents.Count > 0) return;
        if (DateTime.UtcNow - _lastActivity < _idleTimeout) return;

        Log("Idle timeout reached, shutting down");
        _cts?.Cancel();
    }

    private void Shutdown()
    {
        _idleTimer?.Dispose();

        // Close all agent WebSockets
        foreach (var agent in _agents.Values)
        {
            try
            {
                agent.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Broker shutting down", CancellationToken.None)
                    .Wait(TimeSpan.FromSeconds(2));
            }
            catch { }
            agent.WebSocket.Dispose();
        }
        _agents.Clear();
        _mutationLeases.Clear();
        _flows.Clear();
        _workflowRuns.Dispose();

        // Dispose inspector instances. Without this, a Shutdown() that doesn't
        // go through Dispose() (e.g. /api/shutdown handler or idle timeout)
        // leaks every InspectorServer's AgentClient (HttpClient) and CTS.
        foreach (var insp in _inspectors.Values)
        {
            try { insp.Dispose(); } catch { }
        }
        _inspectors.Clear();

        // Delete state file
        DeleteBrokerState();

        try { _listener?.Close(); } catch { }

        Log("Broker stopped");
    }

    private void WriteBrokerState()
    {
        var tmpPath = BrokerPaths.StateFile + ".tmp";
        try
        {
            var dir = BrokerPaths.ConfigDir;
            Directory.CreateDirectory(dir);
            BrokerPaths.RestrictConfigDirectoryPermissions(dir);

            var state = new BrokerState
            {
                Pid = Environment.ProcessId,
                Port = _port,
                StartedAt = DateTime.UtcNow,
                EmbedToken = _embedToken,
                HostApprovalToken = _hostApprovalToken,
            };

            var json = CliJson.SerializeUntyped(state, indented: true);
            File.WriteAllText(tmpPath, json);
            BrokerPaths.RestrictStateFilePermissions(tmpPath);
            File.Move(tmpPath, BrokerPaths.StateFile, overwrite: true);
        }
        catch (Exception ex)
        {
            try { File.Delete(tmpPath); } catch { }
            Log($"Warning: failed to write broker state: {ex.Message}");
        }
    }

    private async Task HandleMutationLeaseRoute(HttpListenerContext context, string method, string path)
    {
        if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonResponseAsync(context, 405, new JsonObject { ["error"] = "Method not allowed" });
            return;
        }

        var segments = path.Trim('/').Split('/');
        if (segments.Length != 3 ||
            !segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) ||
            !segments[1].Equals("leases", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonResponseAsync(context, 404, new JsonObject { ["error"] = "Not found" });
            return;
        }

        var agentId = Uri.UnescapeDataString(segments[2]);

        if (context.Request.ContentLength64 > 64 * 1024)
        {
            await WriteJsonResponseAsync(context, 413, new JsonObject { ["error"] = "Request body too large" });
            return;
        }

        JsonObject body;
        try
        {
            var text = await ReadBoundedBodyAsync(
                context.Request.InputStream,
                context.Request.ContentEncoding ?? Encoding.UTF8,
                64 * 1024);
            body = string.IsNullOrWhiteSpace(text)
                ? new JsonObject()
                : JsonNode.Parse(text)?.AsObject() ?? new JsonObject();
        }
        catch (RequestBodyTooLargeException)
        {
            await WriteJsonResponseAsync(context, 413, new JsonObject { ["error"] = "Request body too large" });
            return;
        }
        catch
        {
            await WriteJsonResponseAsync(context, 400, new JsonObject { ["error"] = "Invalid JSON body" });
            return;
        }

        var action = body["action"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "status";
        var leaseId = body["leaseId"]?.GetValue<string>();
        var holderKind = body["holderKind"]?.GetValue<string>();
        var label = body["label"]?.GetValue<string>();
        var force = body["force"]?.GetValue<bool>() ?? false;
        var transactionId = body["transactionId"]?.GetValue<string>();

        MutationLeaseSnapshot status;
        var stateGate = AgentStateGate(agentId);
        await stateGate.WaitAsync(_cts?.Token ?? CancellationToken.None);
        try
        {
            if (!_agents.ContainsKey(agentId))
            {
                await WriteJsonResponseAsync(context, 404, new JsonObject { ["error"] = $"Agent '{agentId}' not found" });
                return;
            }
            status = _mutationLeases.Control(agentId, action, leaseId, holderKind, label, force, transactionId);
        }
        catch (ArgumentException ex)
        {
            await WriteJsonResponseAsync(context, 400, new JsonObject { ["error"] = ex.Message });
            return;
        }
        finally
        {
            stateGate.Release();
        }

        await WriteJsonResponseAsync(context, 200, new JsonObject
        {
            ["ok"] = true,
            ["allowed"] = status.Allowed,
            ["youHold"] = status.YouHold,
            ["heldByOther"] = status.HeldByOther,
            ["leaseId"] = status.LeaseId,
            ["transactionId"] = status.TransactionId,
            ["holderKind"] = status.HolderKind,
            ["label"] = status.Label,
            ["expiresInMs"] = status.ExpiresInMs,
            ["authorityEpoch"] = status.AuthorityEpoch,
            ["authority"] = "broker"
        });
    }

    private static async Task WriteJsonResponseAsync(HttpListenerContext context, int statusCode, JsonObject body)
    {
        var bytes = Encoding.UTF8.GetBytes(CliJson.SerializeUntyped(body, indented: false));
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private async Task HandleRecordingRoute(HttpListenerContext context, string method, string path)
    {
        if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonResponseAsync(context, 405, new JsonObject { ["error"] = "Method not allowed" });
            return;
        }

        var segments = path.Trim('/').Split('/');
        if (segments.Length != 3 ||
            !segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) ||
            !segments[1].Equals("recordings", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonResponseAsync(context, 404, new JsonObject { ["error"] = "Not found" });
            return;
        }

        var agentId = Uri.UnescapeDataString(segments[2]);
        if (context.Request.ContentLength64 > MaxRecordingRequestChars)
        {
            await WriteJsonResponseAsync(context, 413, new JsonObject { ["error"] = "Request body too large" });
            return;
        }

        JsonObject body;
        try
        {
            var text = await ReadBoundedBodyAsync(
                context.Request.InputStream,
                context.Request.ContentEncoding ?? Encoding.UTF8,
                MaxRecordingRequestChars);
            body = string.IsNullOrWhiteSpace(text)
                ? new JsonObject()
                : JsonNode.Parse(text)?.AsObject() ?? new JsonObject();
        }
        catch (RequestBodyTooLargeException)
        {
            await WriteJsonResponseAsync(context, 413, new JsonObject { ["error"] = "Request body too large" });
            return;
        }
        catch
        {
            await WriteJsonResponseAsync(context, 400, new JsonObject { ["error"] = "Invalid JSON body" });
            return;
        }

        var action = body["action"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "status";
        var leaseId = body["leaseId"]?.GetValue<string>();
        var recordingId = body["recordingId"]?.GetValue<string>();
        if (!action.Equals("status", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(leaseId))
        {
            await WriteJsonResponseAsync(context, 400, new JsonObject { ["error"] = "leaseId is required" });
            return;
        }
        if (!action.Equals("status", StringComparison.Ordinal))
        {
            var lease = _mutationLeases.Control(agentId, "validate", leaseId, null, null, force: false);
            if (!lease.Allowed)
            {
                await WriteJsonResponseAsync(context, 409, new JsonObject
                {
                    ["ok"] = false,
                    ["reason"] = "lease",
                    ["error"] = "Another session is driving this app.",
                    ["holderKind"] = lease.HolderKind,
                    ["label"] = lease.Label
                });
                return;
            }
        }

        BrokerFlowResult result;
        var stateGate = AgentStateGate(agentId);
        await stateGate.WaitAsync(_cts?.Token ?? CancellationToken.None);
        try
        {
            if (!_agents.TryGetValue(agentId, out var connection))
            {
                await WriteJsonResponseAsync(context, 404, new JsonObject { ["error"] = $"Agent '{agentId}' not found" });
                return;
            }
            var flowAgentId = connection.Registration.Id;
            var stableFlowAgentId = RouteCheckpointCoordinator.StableAgentId(connection.Registration);
            _flows.ConnectAgent(
                flowAgentId,
                stableFlowAgentId,
                connection.Registration.SessionId,
                recordingId);

            switch (action)
            {
                case "start":
                    result = _flows.Start(
                        flowAgentId,
                        body["name"]?.GetValue<string>() ?? "scenario",
                        body["app"]?.GetValue<string>() ?? connection.Registration.AppName,
                        body["platform"]?.GetValue<string>() ?? connection.Registration.Platform,
                        body["preconditions"]?.GetValue<string>(),
                        connection.Registration.SessionId,
                        stableFlowAgentId);
                    break;
                case "status":
                    result = _flows.Status(flowAgentId, recordingId);
                    break;
                case "observe":
                    var observationNode = body["observation"];
                    var observation = observationNode?.Deserialize<FlowObservation>(
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    result = observation is null
                        ? BrokerFlowResult.Failure("observation is required")
                        : _flows.Observe(flowAgentId, observation, recordingId);
                    break;
                case "stop":
                    result = _flows.Stop(flowAgentId, recordingId);
                    break;
                case "cancel":
                    result = _flows.Cancel(flowAgentId, recordingId);
                    break;
                case "cancel-if-empty":
                    result = _flows.CancelIfEmpty(flowAgentId, recordingId);
                    break;
                default:
                    result = BrokerFlowResult.Failure($"Unknown recording action '{action}'.");
                    break;
            }
        }
        finally
        {
            stateGate.Release();
        }

        var resultNode = JsonSerializer.SerializeToNode(result, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        })?.AsObject() ?? new JsonObject();
        await WriteJsonResponseAsync(context, result.Ok ? 200 : 400, resultNode);
    }

    /// <summary>
    /// Broker-owned local route checkpoint API. Unlike workflow recording this deliberately does
    /// not require a mutation lease: save/restore/clear are explicit user commands, and restore is
    /// never performed by connection lifecycle code.
    /// </summary>
    private async Task HandleCheckpointRoute(HttpListenerContext context, string method, string path)
    {
        var segments = path.Trim('/').Split('/');
        if (segments.Length != 3 ||
            !segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) ||
            !segments[1].Equals("checkpoints", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonResponseAsync(context, 404, new JsonObject { ["error"] = "Not found" });
            return;
        }

        var agentId = Uri.UnescapeDataString(segments[2]);
        var action = method.Equals("GET", StringComparison.OrdinalIgnoreCase) ? "status" : null;
        if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            if (context.Request.ContentLength64 > 16 * 1024)
            {
                await WriteJsonResponseAsync(context, 413, new JsonObject { ["error"] = "Request body too large" });
                return;
            }
            try
            {
                var text = await ReadBoundedBodyAsync(
                    context.Request.InputStream,
                    context.Request.ContentEncoding ?? Encoding.UTF8,
                    16 * 1024);
                action = string.IsNullOrWhiteSpace(text)
                    ? "status"
                    : JsonNode.Parse(text)?["action"]?.GetValue<string>()?.Trim().ToLowerInvariant();
            }
            catch
            {
                await WriteJsonResponseAsync(context, 400, new JsonObject { ["error"] = "Invalid JSON body" });
                return;
            }
        }
        else if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            await WriteJsonResponseAsync(context, 405, new JsonObject { ["error"] = "Method not allowed" });
            return;
        }

        if (!_agents.TryGetValue(agentId, out var connection))
        {
            await WriteJsonResponseAsync(context, 404, new JsonObject
            {
                ["ok"] = false,
                ["error"] = $"Agent '{agentId}' is not connected. Checkpoints are retained until explicitly cleared or expired."
            });
            return;
        }

        RouteCheckpointStatus status;
        try
        {
            status = action switch
            {
                "status" => _checkpoints.Status(connection.Registration),
                "save" => await _checkpoints.SaveAsync(connection.Registration, _cts?.Token ?? CancellationToken.None),
                "restore" => await _checkpoints.RestoreAsync(connection.Registration, _cts?.Token ?? CancellationToken.None),
                "clear" => ClearCheckpoint(connection.Registration),
                _ => new RouteCheckpointStatus
                {
                    Ok = false,
                    Connected = true,
                    Warning = $"Unknown checkpoint action '{action}'."
                }
            };
        }
        catch (Exception ex)
        {
            Log($"Warning: checkpoint {action} failed for {agentId}: {ex.Message}");
            status = new RouteCheckpointStatus
            {
                Ok = false,
                Connected = true,
                Warning = "Checkpoint operation failed."
            };
        }

        var node = JsonSerializer.SerializeToNode(status, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        })?.AsObject() ?? new JsonObject();
        await WriteJsonResponseAsync(context, status.Ok ? 200 : 400, node);
    }

    private RouteCheckpointStatus ClearCheckpoint(AgentRegistration registration)
    {
        _checkpoints.Clear(registration);
        return new RouteCheckpointStatus { Connected = true, HasCheckpoint = false };
    }

    /// <summary>
    /// Imports foreign diagnostic artifacts into a memory-only, capability-gated quarantine.
    /// Import is deliberately read-only: no flow is executed, no workspace path is used, and no
    /// repair history is appended. Attestation facts are not accepted on this browser-facing route;
    /// imports remain untrusted unless a trusted host calls the provider-neutral policy directly.
    /// </summary>
    private async Task HandleArtifactTrustRoute(HttpListenerContext context, string method, string path)
    {
        const string capabilityHeader = "X-Maui-Artifact-Capability";
        var normalizedPath = path.TrimEnd('/');

        if (string.Equals(normalizedPath, "/api/artifact-trust/import", StringComparison.OrdinalIgnoreCase))
        {
            if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                await WriteTypedJsonResponseAsync(context, 405, ArtifactTrustRouteResponse.Failure("Method not allowed."));
                return;
            }

            var kind = HttpUtility.ParseQueryString(context.Request.Url?.Query ?? string.Empty)["kind"];
            if (!ArtifactTrustImportKinds.IsKnown(kind))
            {
                await WriteTypedJsonResponseAsync(context, 400, ArtifactTrustRouteResponse.Failure(
                    "An explicit supported artifact kind is required."));
                return;
            }

            var maximum = string.Equals(kind, ArtifactTrustImportKinds.FlowRun, StringComparison.Ordinal)
                ? ArtifactTrustImportService.MaxFlowRunBytes
                : (int)Evidence.EvidenceFormat.MaxBundleFileBytes;
            var bytes = await ReadArtifactTrustBytesAsync(context, maximum);
            if (bytes is null)
                return;

            var imported = _artifactTrustImports.Import(
                bytes,
                kind!,
                policy: null,
                verifiedProvenance: null,
                _cts?.Token ?? CancellationToken.None);
            if (!imported.Ok || imported.Artifact is null)
            {
                await WriteTypedJsonResponseAsync(
                    context,
                    400,
                    ArtifactTrustRouteResponse.Failure(imported.Error ?? "The artifact could not be imported."));
                return;
            }

            var stored = _artifactTrustStore.Add(imported.Artifact);
            if (!stored.Ok)
            {
                await WriteTypedJsonResponseAsync(
                    context,
                    409,
                    ArtifactTrustRouteResponse.Failure(stored.Error ?? "The artifact could not be retained."));
                return;
            }

            await WriteTypedJsonResponseAsync(context, 201, new ArtifactTrustRouteResponse
            {
                Ok = true,
                CapabilityToken = stored.CapabilityToken,
                Status = stored.Status,
            });
            return;
        }

        var segments = normalizedPath.Trim('/').Split('/');
        if (segments.Length != 4 ||
            !segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) ||
            !segments[1].Equals("artifact-trust", StringComparison.OrdinalIgnoreCase))
        {
            await WriteTypedJsonResponseAsync(context, 404, ArtifactTrustRouteResponse.Failure("Not found."));
            return;
        }

        var artifactId = Uri.UnescapeDataString(segments[2]);
        var action = segments[3];
        var capabilityToken = context.Request.Headers[capabilityHeader];
        if (string.Equals(action, "status", StringComparison.OrdinalIgnoreCase))
        {
            if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                await WriteTypedJsonResponseAsync(context, 405, ArtifactTrustRouteResponse.Failure("Method not allowed."));
                return;
            }

            var result = _artifactTrustStore.GetStatus(artifactId, capabilityToken);
            await WriteTypedJsonResponseAsync(
                context,
                result.StatusCode,
                result.StatusCode == 200
                    ? new ArtifactTrustRouteResponse { Ok = true, Status = result.Status }
                    : ArtifactTrustRouteResponse.Failure(result.Error ?? "Imported artifact was not found."));
            return;
        }

        if (string.Equals(action, "projection", StringComparison.OrdinalIgnoreCase))
        {
            if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                await WriteTypedJsonResponseAsync(context, 405, ArtifactTrustRouteResponse.Failure("Method not allowed."));
                return;
            }

            var result = _artifactTrustStore.GetSafeProjection(artifactId, capabilityToken);
            await WriteTypedJsonResponseAsync(
                context,
                result.StatusCode,
                result.StatusCode == 200
                    ? new ArtifactTrustRouteResponse { Ok = true, Projection = result.Projection }
                    : ArtifactTrustRouteResponse.Failure(result.Error ?? "Imported artifact was not found."));
            return;
        }

        if (string.Equals(action, "bind-local-reproduction", StringComparison.OrdinalIgnoreCase))
        {
            if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                await WriteTypedJsonResponseAsync(context, 405, ArtifactTrustRouteResponse.Failure("Method not allowed."));
                return;
            }

            // Validate imported-artifact authority before revealing whether a local run exists.
            var access = _artifactTrustStore.GetStatus(artifactId, capabilityToken);
            if (access.StatusCode != 200)
            {
                await WriteTypedJsonResponseAsync(
                    context,
                    access.StatusCode,
                    ArtifactTrustRouteResponse.Failure(access.Error ?? "Imported artifact was not found."));
                return;
            }

            var request = await ReadArtifactTrustJsonBodyAsync<ArtifactTrustLocalReproductionRequest>(context, 32 * 1024);
            if (request is null)
                return;
            if (string.IsNullOrWhiteSpace(request.LocalRunId) || request.LocalRunId.Length > 128 || request.Current is null)
            {
                await WriteTypedJsonResponseAsync(
                    context,
                    400,
                    ArtifactTrustRouteResponse.Failure("localRunId and current reproduction facts are required."));
                return;
            }

            var local = _workflowRuns.GetLocalReproductionFacts(request.LocalRunId);
            if (!local.Ok || local.Facts is null)
            {
                await WriteTypedJsonResponseAsync(
                    context,
                    409,
                    ArtifactTrustRouteResponse.Failure(local.Error ?? "The local run cannot establish reproduction facts."));
                return;
            }

            var bound = _artifactTrustStore.BindLocalReproduction(
                artifactId,
                capabilityToken,
                local.Facts,
                request.Current);
            await WriteTypedJsonResponseAsync(
                context,
                bound.StatusCode,
                bound.StatusCode == 200
                    ? new ArtifactTrustRouteResponse
                    {
                        Ok = true,
                        Status = bound.Status,
                        Reproduction = bound.Evaluation,
                    }
                    : ArtifactTrustRouteResponse.Failure(bound.Error ?? "The reproduction binding was rejected."));
            return;
        }

        await WriteTypedJsonResponseAsync(context, 404, ArtifactTrustRouteResponse.Failure("Not found."));
    }

    private async Task HandleTestAgentRoute(HttpListenerContext context, string method, string path)
    {
        const int maxBodyChars = 1_048_576;
        var normalizedPath = path.TrimEnd('/');
        if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            await WriteTypedJsonResponseAsync(
                context,
                405,
                new Microsoft.Maui.DevFlow.Testing.MauiTestAgentToolResult
                {
                    Error = TestAgentRouteError(
                        Microsoft.Maui.DevFlow.Testing.MauiTestAgentErrorCodes.UnsupportedOperation,
                        Microsoft.Maui.DevFlow.Testing.MauiTestAgentErrorCategories.Unsupported,
                        "Only POST is supported by the restricted test-agent broker protocol.",
                        retryable: false),
                });
            return;
        }

        if (string.Equals(normalizedPath, "/api/test-agent/sessions/begin", StringComparison.OrdinalIgnoreCase))
        {
            var request = await ReadWorkflowRunBodyAsync<Microsoft.Maui.DevFlow.Testing.MauiTestAgentSessionBeginRequest>(context, maxBodyChars);
            if (request is null)
                return;
            request.TargetState = await GetLiveTestAgentTargetStateAsync(request.Envelope?.Target, request.TargetState);
            if (request.TargetState is null)
            {
                await WriteTypedJsonResponseAsync(context, 409, new Microsoft.Maui.DevFlow.Testing.MauiTestAgentSessionResult
                {
                    Error = TestAgentRouteError(
                        Microsoft.Maui.DevFlow.Testing.MauiTestAgentErrorCodes.TargetStale,
                        Microsoft.Maui.DevFlow.Testing.MauiTestAgentErrorCategories.Target,
                        "The explicit target agent and instance are not currently connected.",
                        retryable: false),
                });
                return;
            }
            CanonicalizeTestAgentTarget(request.Envelope?.Target, request.TargetState);

            var result = _testAgentSessions.Begin(request);
            await WriteTypedJsonResponseAsync(context, TestAgentStatusCode(result.Error), result);
            return;
        }

        if (string.Equals(normalizedPath, "/api/test-agent/sessions/status", StringComparison.OrdinalIgnoreCase))
        {
            var request = await ReadWorkflowRunBodyAsync<Microsoft.Maui.DevFlow.Testing.MauiTestAgentSessionAccessRequest>(context, maxBodyChars);
            if (request is null)
                return;
            var result = _testAgentSessions.Status(request);
            await WriteTypedJsonResponseAsync(context, TestAgentStatusCode(result.Error), result);
            return;
        }

        if (string.Equals(normalizedPath, "/api/test-agent/sessions/abandon", StringComparison.OrdinalIgnoreCase))
        {
            var request = await ReadWorkflowRunBodyAsync<Microsoft.Maui.DevFlow.Testing.MauiTestAgentSessionAccessRequest>(context, maxBodyChars);
            if (request is null)
                return;
            var result = _testAgentSessions.Abandon(request);
            await WriteTypedJsonResponseAsync(context, TestAgentStatusCode(result.Error), result);
            return;
        }

        if (string.Equals(normalizedPath, "/api/test-agent/approvals/request", StringComparison.OrdinalIgnoreCase))
        {
            var request = await ReadWorkflowRunBodyAsync<Microsoft.Maui.DevFlow.Testing.MauiTestAgentApprovalSubmitRequest>(context, maxBodyChars);
            if (request is null)
                return;
            var result = _testAgentSessions.SubmitApprovalRequest(request);
            await WriteTypedJsonResponseAsync(context, TestAgentStatusCode(result.Error), result);
            return;
        }

        if (string.Equals(normalizedPath, "/api/test-agent/grants/issue", StringComparison.OrdinalIgnoreCase))
        {
            // A loopback caller or a model-controlled request body is not evidence of a human
            // approval. Only a native local host that retained the host-only broker-state bearer
            // may submit an already-human-reviewed approval. The iframe embed token is different.
            var hostApprovalToken = context.Request.Headers["X-DevFlow-Host-Approval-Token"];
            if (!FixedTimeEquals(_hostApprovalToken, hostApprovalToken))
            {
                await WriteTypedJsonResponseAsync(context, 403, new Microsoft.Maui.DevFlow.Testing.MauiTestAgentGrantIssueResult
                {
                    Error = TestAgentRouteError(
                        Microsoft.Maui.DevFlow.Testing.MauiTestAgentErrorCodes.HumanApprovalRequired,
                        Microsoft.Maui.DevFlow.Testing.MauiTestAgentErrorCategories.Authorization,
                        "A trusted human host approval is required to issue a mutation grant.",
                        retryable: false),
                });
                return;
            }

            var request = await ReadWorkflowRunBodyAsync<Microsoft.Maui.DevFlow.Testing.MauiTestAgentGrantIssueRequest>(context, maxBodyChars);
            if (request is null)
                return;
            request.TargetState = await GetLiveTestAgentTargetStateAsync(request.TargetState);
            if (request.TargetState is null)
            {
                await WriteTypedJsonResponseAsync(context, 409, new Microsoft.Maui.DevFlow.Testing.MauiTestAgentGrantIssueResult
                {
                    Error = TestAgentRouteError(
                        Microsoft.Maui.DevFlow.Testing.MauiTestAgentErrorCodes.TargetStale,
                        Microsoft.Maui.DevFlow.Testing.MauiTestAgentErrorCategories.Target,
                        "The human approval must name a currently connected explicit target.",
                        retryable: false),
                });
                return;
            }

            var result = _testAgentSessions.IssueGrant(request);
            await WriteTypedJsonResponseAsync(context, TestAgentStatusCode(result.Error), result);
            return;
        }

        if (string.Equals(normalizedPath, "/api/test-agent/mutations/authorize", StringComparison.OrdinalIgnoreCase))
        {
            var request = await ReadWorkflowRunBodyAsync<Microsoft.Maui.DevFlow.Testing.MauiTestAgentMutationAuthorizationRequest>(context, maxBodyChars);
            if (request is null)
                return;
            request.CurrentTargetState = await GetLiveTestAgentTargetStateAsync(
                request.Envelope?.Target,
                request.CurrentTargetState);
            if (request.CurrentTargetState is null)
            {
                await WriteTypedJsonResponseAsync(context, 409, new Microsoft.Maui.DevFlow.Testing.MauiTestAgentMutationAuthorizationResult
                {
                    Error = TestAgentRouteError(
                        Microsoft.Maui.DevFlow.Testing.MauiTestAgentErrorCodes.TargetStale,
                        Microsoft.Maui.DevFlow.Testing.MauiTestAgentErrorCategories.Target,
                        "The explicit target agent and instance are not currently connected.",
                        retryable: false),
                });
                return;
            }

            var result = _testAgentSessions.AuthorizeMutation(request);
            await WriteTypedJsonResponseAsync(context, TestAgentStatusCode(result.Error), result);
            return;
        }

        if (string.Equals(normalizedPath, "/api/test-agent/mutations/complete", StringComparison.OrdinalIgnoreCase))
        {
            var request = await ReadWorkflowRunBodyAsync<Microsoft.Maui.DevFlow.Testing.MauiTestAgentMutationCompletion>(context, maxBodyChars);
            if (request is null)
                return;
            var result = _testAgentSessions.CompleteMutation(request);
            await WriteTypedJsonResponseAsync(context, TestAgentStatusCode(result.Error), result);
            return;
        }

        if (string.Equals(normalizedPath, "/api/test-agent/draft/action", StringComparison.OrdinalIgnoreCase))
        {
            var request = await ReadWorkflowRunBodyAsync<Microsoft.Maui.DevFlow.Testing.MauiTestAgentActionRequest>(context, maxBodyChars);
            if (request is null)
                return;
            var result = _testAgentSessions.AppendAction(
                request.Envelope?.Correlation?.AuthoringSessionId,
                request.AuthorizationId,
                request);
            await WriteTypedJsonResponseAsync(context, TestAgentStatusCode(result.Error), result);
            return;
        }

        if (string.Equals(normalizedPath, "/api/test-agent/draft/assertion", StringComparison.OrdinalIgnoreCase))
        {
            var request = await ReadWorkflowRunBodyAsync<Microsoft.Maui.DevFlow.Testing.MauiTestAgentAssertionRequest>(context, maxBodyChars);
            if (request is null)
                return;
            var result = _testAgentSessions.AddAssertion(
                request.Envelope?.Correlation?.AuthoringSessionId,
                request.AuthorizationId,
                request);
            await WriteTypedJsonResponseAsync(context, TestAgentStatusCode(result.Error), result);
            return;
        }

        if (string.Equals(normalizedPath, "/api/test-agent/draft/commit", StringComparison.OrdinalIgnoreCase))
        {
            var request = await ReadWorkflowRunBodyAsync<Microsoft.Maui.DevFlow.Testing.MauiTestAgentSessionAccessRequest>(context, maxBodyChars);
            if (request is null)
                return;
            var result = _testAgentSessions.Commit(request, request.AuthorizationId);
            await WriteTypedJsonResponseAsync(context, TestAgentStatusCode(result.Error), result);
            return;
        }

        if (string.Equals(normalizedPath, "/api/test-agent/draft/migrate-preview", StringComparison.OrdinalIgnoreCase))
        {
            var request = await ReadWorkflowRunBodyAsync<Microsoft.Maui.DevFlow.Testing.MauiTestAgentSessionAccessRequest>(context, maxBodyChars);
            if (request is null)
                return;
            var result = _testAgentSessions.MigratePreview(request);
            await WriteTypedJsonResponseAsync(context, TestAgentStatusCode(result.Error), result);
            return;
        }

        if (string.Equals(normalizedPath, "/api/test-agent/patch", StringComparison.OrdinalIgnoreCase))
        {
            var request = await ReadWorkflowRunBodyAsync<Microsoft.Maui.DevFlow.Testing.MauiTestAgentPatchRequest>(context, maxBodyChars);
            if (request is null)
                return;
            var result = _testAgentSessions.Patch(request);
            await WriteTypedJsonResponseAsync(context, TestAgentStatusCode(result.Error), result);
            return;
        }

        if (string.Equals(normalizedPath, "/api/test-agent/audit", StringComparison.OrdinalIgnoreCase))
        {
            var request = await ReadWorkflowRunBodyAsync<Microsoft.Maui.DevFlow.Testing.MauiTestAgentSessionAccessRequest>(context, maxBodyChars);
            if (request is null)
                return;
            var result = _testAgentSessions.Audit(request);
            await WriteTypedJsonResponseAsync(context, TestAgentStatusCode(result.Error), result);
            return;
        }

        if (string.Equals(normalizedPath, "/api/test-agent/runs/bind", StringComparison.OrdinalIgnoreCase))
        {
            var request = await ReadWorkflowRunBodyAsync<Microsoft.Maui.DevFlow.Testing.MauiTestAgentRunBindingRequest>(context, maxBodyChars);
            if (request is null)
                return;
            var result = _testAgentSessions.BindRun(request);
            await WriteTypedJsonResponseAsync(context, TestAgentStatusCode(result.Error), result);
            return;
        }

        if (string.Equals(normalizedPath, "/api/test-agent/runs/validate", StringComparison.OrdinalIgnoreCase))
        {
            var request = await ReadWorkflowRunBodyAsync<Microsoft.Maui.DevFlow.Testing.MauiTestAgentRunBindingRequest>(context, maxBodyChars);
            if (request is null)
                return;
            var result = _testAgentSessions.ValidateRunBinding(request);
            await WriteTypedJsonResponseAsync(context, TestAgentStatusCode(result.Error), result);
            return;
        }

        await WriteTypedJsonResponseAsync(context, 404, new Microsoft.Maui.DevFlow.Testing.MauiTestAgentToolResult
        {
            Error = TestAgentRouteError(
                Microsoft.Maui.DevFlow.Testing.MauiTestAgentErrorCodes.UnsupportedOperation,
                Microsoft.Maui.DevFlow.Testing.MauiTestAgentErrorCategories.Unsupported,
                "The requested restricted test-agent broker operation is not supported.",
                retryable: false),
        });
    }

    private async Task<Microsoft.Maui.DevFlow.Testing.MauiTestAgentTargetState?> GetLiveTestAgentTargetStateAsync(
        Microsoft.Maui.DevFlow.Testing.MauiTestAgentTarget? target,
        Microsoft.Maui.DevFlow.Testing.MauiTestAgentTargetState? supplied = null)
    {
        if (target is null ||
            string.IsNullOrWhiteSpace(target.AgentId) ||
            string.IsNullOrWhiteSpace(target.AgentInstanceId) ||
            !_agents.TryGetValue(target.AgentId, out var connection) ||
            !string.Equals(connection.Registration.InstanceId, target.AgentInstanceId, StringComparison.Ordinal))
        {
            return null;
        }

        return await ReadLiveTestAgentTargetStateAsync(connection.Registration, supplied).ConfigureAwait(false);
    }

    private async Task<Microsoft.Maui.DevFlow.Testing.MauiTestAgentTargetState?> GetLiveTestAgentTargetStateAsync(
        Microsoft.Maui.DevFlow.Testing.MauiTestAgentTargetState? supplied)
    {
        if (supplied is null ||
            string.IsNullOrWhiteSpace(supplied.AgentId) ||
            string.IsNullOrWhiteSpace(supplied.AgentInstanceId) ||
            !_agents.TryGetValue(supplied.AgentId, out var connection) ||
            !string.Equals(connection.Registration.InstanceId, supplied.AgentInstanceId, StringComparison.Ordinal))
        {
            return null;
        }

        return await ReadLiveTestAgentTargetStateAsync(connection.Registration, supplied).ConfigureAwait(false);
    }

    private static async Task<Microsoft.Maui.DevFlow.Testing.MauiTestAgentTargetState?> ReadLiveTestAgentTargetStateAsync(
        AgentRegistration registration,
        Microsoft.Maui.DevFlow.Testing.MauiTestAgentTargetState? supplied)
    {
        try
        {
            using var client = new Microsoft.Maui.DevFlow.Driver.AgentClient("localhost", registration.Port)
            {
                AutoAcquireMutationLease = false,
            };
            var status = await client.GetStatusAsync().ConfigureAwait(false);
            if (status is null)
                return null;

            return new Microsoft.Maui.DevFlow.Testing.MauiTestAgentTargetState
            {
                AgentId = registration.Id,
                AgentInstanceId = registration.InstanceId,
                AppBuildFingerprint = status.App?.Build,
                // These values remain unavailable until the running agent or a trusted reset host
                // attests them. Never turn caller-echoed values into broker-observed facts.
                SeedFingerprint = null,
                BackendStateFingerprint = null,
                Route = status.Route,
                Window = status.Window,
                ObservedAt = DateTimeOffset.UtcNow,
            };
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return null;
        }
    }

    private static bool RequiresWorkflowRunCapabilities(MauiTestPlan? plan)
        => plan?.Requirements?.RequiredCapabilities.Count > 0 ||
           plan?.Requirements?.RequiredSemantics.Count > 0;

    private static async Task<MauiFlowCapabilitySet?> ReadWorkflowRunCapabilitiesAsync(AgentRegistration registration)
    {
        try
        {
            using var client = new Microsoft.Maui.DevFlow.Driver.AgentClient("localhost", registration.Port)
            {
                AutoAcquireMutationLease = false,
            };
            var status = await client.GetStatusAsync().ConfigureAwait(false);
            return WorkflowRunCoordinator.BuildAvailableCapabilities(status);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return null;
        }
    }

    private static void CanonicalizeTestAgentTarget(
        Microsoft.Maui.DevFlow.Testing.MauiTestAgentTarget? target,
        Microsoft.Maui.DevFlow.Testing.MauiTestAgentTargetState targetState)
    {
        if (target is null)
            return;
        target.AppBuildFingerprint = targetState.AppBuildFingerprint;
        target.SeedFingerprint = targetState.SeedFingerprint;
        target.BackendStateFingerprint = targetState.BackendStateFingerprint;
    }

    private static int TestAgentStatusCode(Microsoft.Maui.DevFlow.Testing.MauiTestAgentError? error)
    {
        if (error is null)
            return 200;
        return error.Category switch
        {
            Microsoft.Maui.DevFlow.Testing.MauiTestAgentErrorCategories.Authorization => 403,
            Microsoft.Maui.DevFlow.Testing.MauiTestAgentErrorCategories.Target or
            Microsoft.Maui.DevFlow.Testing.MauiTestAgentErrorCategories.State or
            Microsoft.Maui.DevFlow.Testing.MauiTestAgentErrorCategories.Conflict => 409,
            Microsoft.Maui.DevFlow.Testing.MauiTestAgentErrorCategories.Capability => 429,
            Microsoft.Maui.DevFlow.Testing.MauiTestAgentErrorCategories.Unsupported => 400,
            _ => 400,
        };
    }

    private static Microsoft.Maui.DevFlow.Testing.MauiTestAgentError TestAgentRouteError(
        string code,
        string category,
        string message,
        bool retryable)
        => new()
        {
            Code = code,
            Category = category,
            Message = message,
            Retryable = retryable,
        };

    private static bool FixedTimeEquals(string expected, string? supplied)
        => !string.IsNullOrEmpty(supplied) &&
           CryptographicOperations.FixedTimeEquals(
               Encoding.UTF8.GetBytes(expected),
               Encoding.UTF8.GetBytes(supplied));

    private async Task HandleWorkflowRunRoute(HttpListenerContext context, string method, string path)
    {
        const int maxBodyChars = 1_048_576;
        var normalizedPath = path.TrimEnd('/');

        if (string.Equals(normalizedPath, "/api/workflow-runs/capabilities", StringComparison.OrdinalIgnoreCase))
        {
            if (!method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                await WriteTypedJsonResponseAsync(
                    context,
                    405,
                    WorkflowRunStatusResponse.Failure("Method not allowed."));
                return;
            }

            await WriteTypedJsonResponseAsync(context, 200, _workflowRuns.GetCapabilities());
            return;
        }

        if (string.Equals(normalizedPath, "/api/workflow-runs/start", StringComparison.OrdinalIgnoreCase))
        {
            if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                await WriteTypedJsonResponseAsync(
                    context,
                    405,
                    WorkflowRunStatusResponse.Failure("Method not allowed."));
                return;
            }

            var request = await ReadWorkflowRunBodyAsync<WorkflowRunStartRequest>(context, maxBodyChars);
            if (request is null)
                return;

            if (string.IsNullOrWhiteSpace(request.AgentId) || string.IsNullOrWhiteSpace(request.AgentInstanceId))
            {
                await WriteTypedJsonResponseAsync(
                    context,
                    400,
                    WorkflowRunStartResult.Rejected(400, "agentId and agentInstanceId are required.", null, null));
                return;
            }

            var routeGate = AgentRouteGate(request.AgentId);
            await routeGate.WaitAsync(_cts?.Token ?? CancellationToken.None);
            try
            {
                if (!_agents.TryGetValue(request.AgentId, out var connection) ||
                    !string.Equals(
                        connection.Registration.InstanceId,
                        request.AgentInstanceId,
                        StringComparison.Ordinal))
                {
                    await WriteTypedJsonResponseAsync(
                        context,
                        409,
                        WorkflowRunStartResult.Conflict(
                            "The requested agent instance is stale or no longer connected."));
                    return;
                }

                if (RequiresWorkflowRunCapabilities(request.Plan) &&
                    request.AvailableCapabilities is null)
                {
                    request.AvailableCapabilities = await ReadWorkflowRunCapabilitiesAsync(connection.Registration)
                        .ConfigureAwait(false);
                }

                var target = CreateWorkflowRunTarget(connection.Registration);
                var result = _workflowRuns.Start(
                    request,
                    target,
                    () => IsCurrentAgentConnection(connection),
                    new WorkflowRunExecutionOptions
                    {
                        ReproductionExpectation = request.ReproductionExpectation,
                    });
                await WriteTypedJsonResponseAsync(context, result.StatusCode, result);
            }
            finally
            {
                routeGate.Release();
            }
            return;
        }

        var segments = normalizedPath.Trim('/').Split('/');
        if (segments.Length != 4 ||
            !segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) ||
            !segments[1].Equals("workflow-runs", StringComparison.OrdinalIgnoreCase))
        {
            await WriteTypedJsonResponseAsync(context, 404, WorkflowRunStatusResponse.Failure("Not found."));
            return;
        }

        var runId = Uri.UnescapeDataString(segments[2]);
        var action = segments[3];
        if (string.Equals(action, "status", StringComparison.OrdinalIgnoreCase))
        {
            if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                await WriteTypedJsonResponseAsync(
                    context,
                    405,
                    WorkflowRunStatusResponse.Failure("Method not allowed."));
                return;
            }

            var request = await ReadWorkflowRunBodyAsync<WorkflowRunAccessRequest>(context, maxBodyChars);
            if (request is null)
                return;

            var result = _workflowRuns.GetStatus(runId, request.CapabilityToken);
            await WriteTypedJsonResponseAsync(
                context,
                result.StatusCode,
                result.Run is null
                    ? WorkflowRunStatusResponse.Failure(result.Error ?? "Workflow run was not found.")
                    : WorkflowRunStatusResponse.Success(result.Run));
            return;
        }

        if (string.Equals(action, "cancel", StringComparison.OrdinalIgnoreCase))
        {
            if (!method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                await WriteTypedJsonResponseAsync(
                    context,
                    405,
                    WorkflowRunStatusResponse.Failure("Method not allowed."));
                return;
            }

            var request = await ReadWorkflowRunBodyAsync<WorkflowRunAccessRequest>(context, maxBodyChars);
            if (request is null)
                return;

            var result = _workflowRuns.Cancel(runId, request.CapabilityToken);
            await WriteTypedJsonResponseAsync(context, result.StatusCode, result);
            return;
        }

        await WriteTypedJsonResponseAsync(context, 404, WorkflowRunStatusResponse.Failure("Not found."));
    }

    private async Task<byte[]?> ReadArtifactTrustBytesAsync(HttpListenerContext context, int maximumBytes)
    {
        if (context.Request.ContentLength64 > maximumBytes)
        {
            await WriteTypedJsonResponseAsync(
                context,
                413,
                ArtifactTrustRouteResponse.Failure("Request body too large."));
            return null;
        }

        try
        {
            using var output = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
            var buffer = new byte[16 * 1024];
            var total = 0;
            while (true)
            {
                var read = await context.Request.InputStream.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    _cts?.Token ?? CancellationToken.None);
                if (read == 0)
                    break;

                total += read;
                if (total > maximumBytes)
                {
                    await WriteTypedJsonResponseAsync(
                        context,
                        413,
                        ArtifactTrustRouteResponse.Failure("Request body too large."));
                    return null;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), _cts?.Token ?? CancellationToken.None);
            }

            return output.ToArray();
        }
        catch (OperationCanceledException)
        {
            await WriteTypedJsonResponseAsync(
                context,
                408,
                ArtifactTrustRouteResponse.Failure("Artifact import was cancelled."));
            return null;
        }
        catch (IOException)
        {
            await WriteTypedJsonResponseAsync(
                context,
                400,
                ArtifactTrustRouteResponse.Failure("The artifact request could not be read."));
            return null;
        }
    }

    private async Task<T?> ReadArtifactTrustJsonBodyAsync<T>(HttpListenerContext context, int maximumChars)
        where T : class
    {
        if (context.Request.ContentLength64 > maximumChars)
        {
            await WriteTypedJsonResponseAsync(
                context,
                413,
                ArtifactTrustRouteResponse.Failure("Request body too large."));
            return null;
        }

        try
        {
            var text = await ReadBoundedBodyAsync(
                context.Request.InputStream,
                context.Request.ContentEncoding ?? Encoding.UTF8,
                maximumChars,
                _cts?.Token ?? CancellationToken.None);
            if (string.IsNullOrWhiteSpace(text))
            {
                await WriteTypedJsonResponseAsync(
                    context,
                    400,
                    ArtifactTrustRouteResponse.Failure("A JSON request body is required."));
                return null;
            }

            var value = JsonSerializer.Deserialize<T>(text, WorkflowRunJsonOptions);
            if (value is null)
            {
                await WriteTypedJsonResponseAsync(
                    context,
                    400,
                    ArtifactTrustRouteResponse.Failure("Invalid JSON request body."));
            }
            return value;
        }
        catch (RequestBodyTooLargeException)
        {
            await WriteTypedJsonResponseAsync(
                context,
                413,
                ArtifactTrustRouteResponse.Failure("Request body too large."));
            return null;
        }
        catch (JsonException)
        {
            await WriteTypedJsonResponseAsync(
                context,
                400,
                ArtifactTrustRouteResponse.Failure("Invalid JSON request body."));
            return null;
        }
    }

    private async Task<T?> ReadWorkflowRunBodyAsync<T>(HttpListenerContext context, int maxChars)
        where T : class
    {
        if (context.Request.ContentLength64 > maxChars)
        {
            await WriteTypedJsonResponseAsync(
                context,
                413,
                WorkflowRunStatusResponse.Failure("Request body too large."));
            return null;
        }

        try
        {
            var text = await ReadBoundedBodyAsync(
                context.Request.InputStream,
                context.Request.ContentEncoding ?? Encoding.UTF8,
                maxChars,
                _cts?.Token ?? CancellationToken.None);
            if (string.IsNullOrWhiteSpace(text))
            {
                await WriteTypedJsonResponseAsync(
                    context,
                    400,
                    WorkflowRunStatusResponse.Failure("A JSON request body is required."));
                return null;
            }

            var value = JsonSerializer.Deserialize<T>(text, WorkflowRunJsonOptions);
            if (value is null)
            {
                await WriteTypedJsonResponseAsync(
                    context,
                    400,
                    WorkflowRunStatusResponse.Failure("Invalid JSON request body."));
            }
            return value;
        }
        catch (RequestBodyTooLargeException)
        {
            await WriteTypedJsonResponseAsync(
                context,
                413,
                WorkflowRunStatusResponse.Failure("Request body too large."));
            return null;
        }
        catch (JsonException)
        {
            await WriteTypedJsonResponseAsync(
                context,
                400,
                WorkflowRunStatusResponse.Failure("Invalid JSON request body."));
            return null;
        }
    }

    private static readonly JsonSerializerOptions WorkflowRunJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static async Task WriteTypedJsonResponseAsync<T>(
        HttpListenerContext context,
        int statusCode,
        T body)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, WorkflowRunJsonOptions);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static WorkflowRunTarget CreateWorkflowRunTarget(AgentRegistration registration)
        => new(
            registration.Id,
            registration.InstanceId,
            registration.Port,
            registration.Platform,
            registration.AppName);

    private bool IsCurrentAgentConnection(AgentConnection expected)
        => _agents.TryGetValue(expected.Registration.Id, out var current) &&
           ReferenceEquals(current, expected) &&
           string.Equals(
               current.Registration.InstanceId,
               expected.Registration.InstanceId,
               StringComparison.Ordinal);

    private async Task<FlowReplayReport> ExecuteWorkflowRunAsync(
        WorkflowRunExecution execution,
        CancellationToken cancellationToken)
    {
        using var client = new Microsoft.Maui.DevFlow.Driver.AgentClient("localhost", execution.Target.AgentPort)
        {
            AutoAcquireMutationLease = false,
            RetryMutatingRequests = false,
            TransientFailureRetryCount = 0,
            MutationLeaseHolderKind = "workflow-run",
            MutationLeaseLabel = execution.RunId
        };
        using var leaseScope = client.UseMutationLease(
            execution.LeaseId,
            "workflow-run",
            execution.RunId);
        using var workflowScope = client.UseWorkflowRun(new Microsoft.Maui.DevFlow.Driver.WorkflowRunContext
        {
            RunId = execution.RunId,
            AgentInstanceId = execution.Target.AgentInstanceId,
            AuthorityEpoch = execution.AuthorityEpoch
        });
        Microsoft.Maui.DevFlow.Driver.AgentStatus? liveStatus = null;
        try
        {
            // Target facts are observed before replay and are report metadata only. They do not
            // grant reset or source authority, and unavailable facts remain absent rather than
            // being inferred from a package name or host path.
            liveStatus = await client.GetStatusAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            // The canonical runner will report the actual transport failure. Keep target facts
            // capability-honest when the app cannot answer this optional read.
        }
        var evidenceCapture = execution.Options.EvidenceCaptureFactory?.Invoke(client);
        var runner = new MauiFlowRunner(
            client,
            new MauiFlowRunnerOptions
            {
                RunId = execution.RunId,
                Target = new MauiFlowRunTarget
                {
                    TargetId = execution.Target.AgentId,
                    AgentId = execution.Target.AgentId,
                    AgentInstanceId = execution.Target.AgentInstanceId,
                    Platform = liveStatus?.Device?.Platform ?? execution.Target.Platform,
                    AppId = liveStatus?.App?.PackageId ?? execution.Target.AppName,
                    AppBuildFingerprint = liveStatus?.App?.Build,
                    AppSourceFingerprint = execution.Options.ReproductionExpectation?.AppSourceFingerprint,
                    PackageDigest = execution.Options.ReproductionExpectation?.PackageDigest,
                    DeviceProfile = execution.Options.ReproductionExpectation?.DeviceProfile ??
                        string.Join(
                            "|",
                            liveStatus?.Device?.DeviceType ?? string.Empty,
                            liveStatus?.Device?.Idiom ?? string.Empty),
                },
                Plan = execution.SafetyRequest.Plan,
                RunContext = execution.SafetyRequest.Context,
                ThrowOnCancellation = false,
                Progress = execution.Options.Progress,
            },
            evidenceCapture);
        return (await runner.RunWithLegacyAsync(execution.Flow, file: null, cancellationToken)
            .ConfigureAwait(false)).LegacyReport;
    }

    private async Task<WorkflowRunLedgerControlResult> ControlWorkflowRunLedgerAsync(
        WorkflowRunLedgerControl control,
        CancellationToken cancellationToken)
    {
        using var client = new Microsoft.Maui.DevFlow.Driver.AgentClient("localhost", control.Target.AgentPort)
        {
            AutoAcquireMutationLease = false,
            RetryMutatingRequests = false,
            TransientFailureRetryCount = 0,
            MutationLeaseHolderKind = "workflow-run",
            MutationLeaseLabel = control.RunId
        };
        using var leaseScope = client.UseMutationLease(
            control.LeaseId,
            "workflow-run",
            control.RunId);
        var result = await client.ControlWorkflowRunAsync(
            control.Action,
            new Microsoft.Maui.DevFlow.Driver.WorkflowRunContext
            {
                RunId = control.RunId,
                AgentInstanceId = control.Target.AgentInstanceId,
                AuthorityEpoch = control.AuthorityEpoch,
                ApprovalDigest = control.ApprovalDigest
            },
            control.Reason).WaitAsync(cancellationToken).ConfigureAwait(false);

        if (result.Ok && !string.Equals(result.State, "unknown-completion", StringComparison.Ordinal))
            return WorkflowRunLedgerControlResult.Success();

        return WorkflowRunLedgerControlResult.Failure(
            result.Reason ?? (result.State == "unknown-completion" ? "workflow-unknown-completion" : null),
            result.Error ?? (result.State == "unknown-completion"
                ? "The agent reported a workflow command with unknown completion."
                : null));
    }

    internal static async Task<string> ReadBoundedBodyAsync(
        Stream stream,
        Encoding encoding,
        int maxChars,
        CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(
            stream,
            encoding,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);
        var buffer = new char[Math.Min(4096, maxChars + 1)];
        var builder = new StringBuilder(Math.Min(maxChars, 4096));
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) return builder.ToString();
            if (builder.Length + read > maxChars) throw new RequestBodyTooLargeException();
            builder.Append(buffer, 0, read);
        }
    }

    private sealed class RequestBodyTooLargeException : Exception;

    private static void DeleteBrokerState()
    {
        try { File.Delete(BrokerPaths.StateFile); } catch { }
    }

    private void Log(string message)
    {
        var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] {message}";
        try { _log?.Invoke(line); } catch { }

        try
        {
            var logFile = BrokerPaths.LogFile;
            Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);

            // Truncate if > 1MB
            if (File.Exists(logFile) && new FileInfo(logFile).Length > 1_000_000)
                File.WriteAllText(logFile, "");

            File.AppendAllText(logFile, line + Environment.NewLine);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _idleTimer?.Dispose();
        try { _listener?.Close(); } catch { }
        foreach (var inspector in _inspectors.Values)
        {
            try { inspector.Dispose(); } catch { }
        }
        _inspectors.Clear();
        _mutationLeases.Clear();
        _flows.Clear();
        _workflowRuns.Dispose();
        _cts?.Dispose();
    }
    private record AgentConnection(AgentRegistration Registration, WebSocket WebSocket);

    // ── Inspector integration ──

    private readonly ConcurrentDictionary<string, InspectorServer> _inspectors = new();

    // Unguessable per-broker token that lets local host shells (canvas, VS Code) embed the inspector
    // in an iframe. Written to broker.json (local-only) and honored by the inspector via ?embed=.
    private readonly string _embedToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    // Independent native-host approval bearer. Unlike _embedToken, this value is never passed to
    // InspectorServer or placed in a browser-visible URL.
    private readonly string _hostApprovalToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private async Task HandleInspectorRoute(HttpListenerContext context, string path)
    {
        // Routes:
        //   /inspector          → list agents with inspector links
        //   /inspector/{id}     → 301 redirect to /inspector/{id}/ so that
        //                         relative asset URLs in inspector.html
        //                         (devflow.css, devflow.js) resolve against
        //                         the per-agent base rather than the broker root.
        //   /inspector/{id}/    → serve inspector HTML for that agent
        //   /inspector/{id}/... → proxy sub-routes to the per-agent InspectorServer

        var segments = path.TrimStart('/').Split('/', 3);

        if (segments.Length == 1 || (segments.Length == 2 && string.IsNullOrEmpty(segments[1])))
        {
            // List agents with inspector links
            await ServeAgentListPage(context);
            return;
        }

        // /inspector/{id} (no trailing slash, no sub-path) → redirect to
        // /inspector/{id}/ so that <link href="devflow.css"> on the inspector
        // page resolves to /inspector/{id}/devflow.css instead of
        // /inspector/devflow.css (which the broker would otherwise try to
        // route as agent id "devflow.css"). Without the trailing slash, the
        // browser treats {id} as a filename and resolves relatives against
        // /inspector/ instead of /inspector/{id}/.
        if (segments.Length == 2 && !string.IsNullOrEmpty(segments[1]) && !path.EndsWith('/'))
        {
            // Preserve the query string on the redirect and URL-encode the
            // agent id so the Location header is a valid URI-reference even
            // when the agent id contains characters that need percent-encoding
            // (spaces, unicode, etc.).
            var query = context.Request.Url?.Query ?? string.Empty;
            context.Response.StatusCode = 301;
            context.Response.RedirectLocation = $"/inspector/{Uri.EscapeDataString(segments[1])}/{query}";
            context.Response.Close();
            return;
        }

        var agentId = segments[1];

        var subPath = segments.Length > 2 ? "/" + segments[2] : "/";

        // Inspector URLs are agent-scoped. Only the explicit "default" convenience route may use
        // the sole connected agent; a stale or mistyped real ID must never drive another app.
        var connection = ResolveInspectorAgent(_agents, agentId);

        if (connection == null)
        {
            context.Response.StatusCode = 404;
            context.Response.ContentType = "text/plain";
            var msg = Encoding.UTF8.GetBytes($"Agent '{agentId}' not found. Connected agents: {_agents.Count}");
            await context.Response.OutputStream.WriteAsync(msg);
            context.Response.Close();
            return;
        }

        // Ordinary HTTP requests hold the generation gate through dispatch, so a same-ID reconnect
        // cannot replace the agent after validation but before a mutation reaches it. Event sockets
        // are long-lived and read-only; replacement disposes their InspectorServer instead.
        var routeGate = context.Request.IsWebSocketRequest ? null : AgentRouteGate(connection.Registration.Id);
        if (routeGate is not null)
            await routeGate.WaitAsync(_cts?.Token ?? CancellationToken.None);
        try
        {

        // Get or create inspector server for this agent.
        // Three lifecycle hazards we have to defend against:
        //   1. Race with disconnect — the agent may disconnect between our
        //      TryGetValue above and creating the inspector. MonitorAgentConnection
        //      runs its own _inspectors.TryRemove() but only sees inspectors that
        //      already exist; one created after its cleanup would be orphaned.
        //   2. Stale port on reconnect — if the agent restarts on a different port,
        //      the cached inspector still holds an AgentClient pointing at the old
        //      (dead) port. Replace it.
        //   3. GetOrAdd factory race — the ConcurrentDictionary factory overload
        //      may invoke the factory concurrently and silently discard the loser,
        //      leaking its AgentClient (HttpClient) and CTS. Construct first, then
        //      GetOrAdd(value), and dispose the loser.
        var agentPort = connection.Registration.Port;
        if (_inspectors.TryGetValue(connection.Registration.Id, out var inspector))
        {
            // Stale-port detection: the agent reconnected on a different port.
            if (inspector.AgentPort != agentPort ||
                !string.Equals(inspector.AgentInstanceId, connection.Registration.InstanceId, StringComparison.Ordinal))
            {
                if (_inspectors.TryRemove(new KeyValuePair<string, InspectorServer>(connection.Registration.Id, inspector)))
                {
                    try { inspector.Dispose(); } catch { }
                }
                inspector = null;
            }
        }

        if (inspector == null)
        {
            var registration = connection.Registration;
            var created = new InspectorServer(
                0,
                "localhost",
                agentPort,
                _embedToken,
                registration.Id,
                registration.AppName,
                registration.Platform,
                registration.Project,
                registration.SessionId,
                checkpoints: _checkpoints,
                checkpointRegistration: registration,
                workflowReplay: (flow, evidenceFactory, leaseHandoff, cancellationToken) =>
                    ReplayInspectorWorkflowAsync(connection, flow, evidenceFactory, leaseHandoff, cancellationToken),
                agentInstanceId: registration.InstanceId,
                workflowServices: new InspectorWorkflowServices(
                    _workflowRuns,
                    _artifactTrustImports,
                    _artifactTrustStore,
                    _workflowRepairs,
                    _workflowXamlSources,
                    _workflowCSharpSources,
                    CreateWorkflowRunTarget(registration),
                    () => IsCurrentAgentConnection(connection),
                    _cts?.Token ?? CancellationToken.None),
                testAgentSessions: _testAgentSessions,
                testAgentTargetStateRefresh: supplied => GetLiveTestAgentTargetStateAsync(supplied));
            inspector = _inspectors.GetOrAdd(connection.Registration.Id, created);
            if (!ReferenceEquals(inspector, created))
            {
                created.Dispose();
            }
            else
            {
                Log($"Inspector created for agent: {connection.Registration.AppName} (port {agentPort})");
            }

            // Disconnect-race fix: MonitorAgentConnection may have already removed
            // this agent ID from _agents (and tried to remove the not-yet-existing
            // inspector). If so, our newly-stored inspector would leak — clean up
            // and return 503 instead of routing into a dead AgentClient.
            if (!_agents.TryGetValue(connection.Registration.Id, out var currentConnection) ||
                !ReferenceEquals(currentConnection, connection))
            {
                if (_inspectors.TryRemove(new KeyValuePair<string, InspectorServer>(connection.Registration.Id, inspector)))
                {
                    try { inspector.Dispose(); } catch { }
                }
                context.Response.StatusCode = 503;
                context.Response.ContentType = "text/plain";
                var msg = Encoding.UTF8.GetBytes("Agent disconnected");
                await context.Response.OutputStream.WriteAsync(msg);
                context.Response.Close();
                return;
            }
        }

        if (!_agents.TryGetValue(connection.Registration.Id, out var routeConnection) ||
            !ReferenceEquals(routeConnection, connection))
        {
            context.Response.StatusCode = 503;
            context.Response.ContentType = "text/plain";
            var msg = Encoding.UTF8.GetBytes("Agent reconnected; reload Inspector state");
            await context.Response.OutputStream.WriteAsync(msg);
            context.Response.Close();
            return;
        }

        // Proxy the request through the inspector's route handler
        await inspector.HandleBrokerRequestAsync(context, subPath);
        }
        finally
        {
            routeGate?.Release();
        }
    }

    internal static TConnection? ResolveInspectorAgent<TConnection>(
        IReadOnlyDictionary<string, TConnection> agents,
        string agentId)
        where TConnection : class
    {
        if (agents.TryGetValue(agentId, out var connection))
            return connection;
        if (!string.Equals(agentId, "default", StringComparison.OrdinalIgnoreCase))
            return null;

        var candidates = agents.Values.Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private async Task<FlowReplayReport> ReplayInspectorWorkflowAsync(
        AgentConnection connection,
        MauiFlow flow,
        Func<Microsoft.Maui.DevFlow.Driver.AgentClient, IFlowReplayEvidenceCapture?> evidenceCaptureFactory,
        WorkflowRunLeaseHandoff leaseHandoff,
        CancellationToken _)
    {
        if (!IsCurrentAgentConnection(connection))
        {
            throw new WorkflowRunRejectedException(
                409,
                "The Inspector target was replaced by a newer agent instance. Reload the Inspector.");
        }

        var registration = connection.Registration;
        var started = _workflowRuns.Start(
            new WorkflowRunStartRequest
            {
                AgentId = registration.Id,
                AgentInstanceId = registration.InstanceId,
                IdempotencyKey = $"inspector-{Guid.NewGuid():N}",
                Flow = flow,
                TimeoutMs = 120_000
            },
            CreateWorkflowRunTarget(registration),
            () => IsCurrentAgentConnection(connection),
            new WorkflowRunExecutionOptions
            {
                EvidenceCaptureFactory = evidenceCaptureFactory
            },
            leaseHandoff);
        if (!started.Ok || started.Run is null || string.IsNullOrWhiteSpace(started.CapabilityToken))
        {
            throw new WorkflowRunRejectedException(
                started.StatusCode == 0 ? 500 : started.StatusCode,
                started.Error ?? "The broker could not start the workflow run.");
        }

        var snapshot = await _workflowRuns.WaitForTerminalAsync(
            started.Run.RunId,
            started.CapabilityToken,
            CancellationToken.None).ConfigureAwait(false);
        if (snapshot.CompatibilityReport is not null)
            return snapshot.CompatibilityReport;
        if (snapshot.Report is not null)
        {
            var compatibility = FlowReplayReportAdapter.ToLegacy(snapshot.Report, flow.Name);
            compatibility.Total = Math.Max(compatibility.Total, flow.Steps.Count);
            return compatibility;
        }

        return new FlowReplayReport
        {
            Ok = false,
            Name = flow.Name,
            Total = flow.Steps.Count,
            Failed = flow.Steps.Count,
            DivergencePoint = snapshot.FirstDivergence,
            StoppedEarly = true,
            Results =
            [
                new FlowStepResult
                {
                    Seq = snapshot.FirstDivergence ?? 0,
                    Action = "run",
                    Label = "Prepare run",
                    Ok = false,
                    FailureKind = FlowFailureKinds.Drive,
                    Error = snapshot.Message ?? "The workflow run failed before the first step."
                }
            ]
        };
    }

    private async Task ServeAgentListPage(HttpListenerContext context)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'><title>DevFlow Inspector</title>");
        sb.AppendLine("<style>body{font-family:system-ui;background:#1e1e1e;color:#fff;padding:20px}");
        sb.AppendLine("a{color:#4ec9b0;text-decoration:none}a:hover{text-decoration:underline}");
        sb.AppendLine(".agent{padding:12px;margin:8px 0;background:#2d2d2d;border-radius:6px}</style></head><body>");
        sb.AppendLine("<h1>DevFlow Inspector</h1>");

        if (_agents.IsEmpty)
        {
            sb.AppendLine("<p>No agents connected. Start a MAUI app with DevFlow enabled.</p>");
        }
        else
        {
            foreach (var agent in _agents.Values)
            {
                var reg = agent.Registration;
                sb.AppendLine($"<div class='agent'>");
                sb.AppendLine($"<a href='/inspector/{HttpUtility.UrlEncode(reg.Id)}/'><strong>{HttpUtility.HtmlEncode(reg.AppName)}</strong></a>");
                sb.AppendLine($" — {HttpUtility.HtmlEncode(reg.Platform)} ({HttpUtility.HtmlEncode(reg.Tfm)}) on port {reg.Port}");
                sb.AppendLine($"</div>");
            }
        }

        sb.AppendLine("</body></html>");
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }
}
