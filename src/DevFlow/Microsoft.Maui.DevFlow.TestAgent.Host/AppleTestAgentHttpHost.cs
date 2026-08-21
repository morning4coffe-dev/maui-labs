using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Maui.DevFlow.Driver;
using Microsoft.Maui.DevFlow.TestAgent.Protocol;

namespace Microsoft.Maui.DevFlow.TestAgent.Host;

/// <summary>
/// Local macOS host endpoint for the native XCTest agent. Commands are pulled by the device agent;
/// the host never opens an unauthenticated listener on a non-loopback interface.
/// </summary>
public sealed class AppleTestAgentHttpHost : IAppleTestAgentTransport, IAsyncDisposable
{
    private const int MaximumRequestBytes = AppleTestAgentProtocolVersions.MaximumArtifactChunkBytes * 2;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<PendingCommand> _commands = Channel.CreateUnbounded<PendingCommand>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly ConcurrentDictionary<string, PendingCommand> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, AppleTestAgentCancellation> _cancellations = new(StringComparer.Ordinal);
    private readonly AppleTestAgentCommandLedger _ledger;
    private readonly AppleTestAgentReplayProtector _replay = new();
    private readonly AppleTestAgentArtifactChunkAssembler _artifacts = new();
    private readonly byte[] _secret;
    private readonly TimeProvider _clock;
    private readonly object _attachmentGate = new();
    private readonly TaskCompletionSource<AppleTestAgentHello> _agentAttached =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _acceptLoop;
    private bool _started;
    private bool _disposed;
    private long _nextSequence;
    private string? _attachedAgentInstanceId;
    private int _attachmentCount;
    private DateTimeOffset? _lastAgentActivity;

    public AppleTestAgentHttpHost(
        AppleTestAgentSession session,
        byte[] sharedSecret,
        int? port = null,
        TimeProvider? clock = null)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        if (sharedSecret is null || sharedSecret.Length < 32)
            throw new ArgumentException("A random 32-byte session secret is required.", nameof(sharedSecret));

        _secret = sharedSecret.ToArray();
        _clock = clock ?? TimeProvider.System;
        _ledger = new AppleTestAgentCommandLedger(session, _clock);
        Port = port ?? FindAvailablePort();
        Endpoint = new Uri($"http://127.0.0.1:{Port}/", UriKind.Absolute);
        _listener.Prefixes.Add(Endpoint.AbsoluteUri);
    }

    public AppleTestAgentSession Session { get; }
    public int Port { get; }
    public Uri Endpoint { get; }
    public AppleTestAgentCommandReceipt? LastReceipt { get; private set; }
    public string? LastQueuedCommandId { get; private set; }
    public string? LastDeliveredCommandId { get; private set; }
    public AppleTestAgentHello? Hello { get; private set; }
    public IReadOnlyList<AppleTestAgentArtifactReference> CompletedArtifacts => _artifacts.CompletedReferences;
    public int AttachmentCount => Volatile.Read(ref _attachmentCount);
    public DateTimeOffset? LastAgentActivity => _lastAgentActivity;

    public Task StartAsync()
    {
        ThrowIfDisposed();
        if (_started)
            return Task.CompletedTask;

        _listener.Start();
        _started = true;
        _acceptLoop = Task.Run(AcceptLoopAsync);
        return Task.CompletedTask;
    }

    public async Task<AppleTestAgentHello> WaitForAgentAsync(CancellationToken cancellationToken = default)
        => await _agentAttached.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

    public async Task WaitForCommandDeliveryAsync(string commandId, CancellationToken cancellationToken = default)
    {
        if (!_pending.TryGetValue(commandId, out var pending))
            throw new InvalidOperationException("The requested Apple test-agent command is no longer pending.");
        await pending.Delivered.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AppleTestAgentOperationCompletion> SendAsync(
        string operation,
        IReadOnlyDictionary<string, string>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_started)
            throw new InvalidOperationException("The Apple test-agent host has not started.");
        EnsureSessionActive();
        if (!AppleTestAgentOperations.All.Contains(operation))
            throw new ArgumentOutOfRangeException(nameof(operation), "The operation is not part of the driver contract.");

        var sequence = Interlocked.Increment(ref _nextSequence);

        var command = CreateCommand(operation, arguments, sequence);
        var prepared = _ledger.Prepare(command);
        if (!prepared.Accepted || !prepared.ShouldDispatch)
            throw ToException(prepared.Error, prepared.Receipt);

        var dispatched = _ledger.MarkDispatched(command.CommandId);
        if (!dispatched.Accepted)
            throw ToException(dispatched.Error, dispatched.Receipt);

        LastReceipt = dispatched.Receipt;
        LastQueuedCommandId = command.CommandId;
        var pending = new PendingCommand(command);
        if (!_pending.TryAdd(command.CommandId, pending))
            throw new InvalidOperationException("The Apple test-agent command ID collided.");
        if (!_commands.Writer.TryWrite(pending))
            throw new InvalidOperationException("The Apple test-agent command queue is unavailable.");

        try
        {
            return await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var delivered = pending.Delivered.Task.IsCompleted;
            await CancelAsync(command.CommandId, "host-cancellation", CancellationToken.None).ConfigureAwait(false);
            if (delivered)
            {
                // The caller's deadline elapsed after delivery. The device may still complete,
                // but the host cannot safely infer whether the operation took effect.
                _ledger.MarkUnknownCompletion(command.CommandId);
            }
            throw;
        }
        finally
        {
            _pending.TryRemove(command.CommandId, out _);
        }
    }

    public Task<AppleTestAgentCommandReceipt?> CancelAsync(
        string commandId,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        cancellationToken.ThrowIfCancellationRequested();
        var cancellation = new AppleTestAgentCancellation
        {
            SessionId = Session.SessionId,
            CommandId = commandId,
            Reason = string.IsNullOrWhiteSpace(reason) ? "host-cancellation" : reason,
            RequestedAt = _clock.GetUtcNow(),
        };
        _cancellations.TryAdd(commandId, cancellation);
        if (_pending.TryGetValue(commandId, out var pending) && !pending.Delivered.Task.IsCompleted)
        {
            var result = _ledger.Cancel(commandId);
            LastReceipt = result.Receipt ?? LastReceipt;
            if (result.Receipt is null)
                return Task.FromResult<AppleTestAgentCommandReceipt?>(null);
            pending.Completion.TrySetResult(new AppleTestAgentOperationCompletion
            {
                Receipt = result.Receipt,
                Ok = false,
                CompletionCertainty = "certain",
                Error = new AppleTestAgentError
                {
                    Code = AppleTestAgentErrorCodes.Cancelled,
                    Category = "cancelled",
                    Message = "The host cancelled the operation.",
                },
            });
            return Task.FromResult<AppleTestAgentCommandReceipt?>(result.Receipt);
        }

        // Once delivery is observed, only the device agent can attest whether it observed the
        // cancellation before the operation took effect. Do not manufacture a certain receipt.
        // The cancellation query remains available to the agent until it responds or the host
        // session times out, at which point the runner reports unknown completion.
        return Task.FromResult<AppleTestAgentCommandReceipt?>(LastReceipt);
    }

    private AppleTestAgentOperationCommand CreateCommand(
        string operation,
        IReadOnlyDictionary<string, string>? arguments,
        long sequence)
    {
        var normalizedArguments = arguments is null
            ? null
            : new Dictionary<string, string>(arguments, StringComparer.Ordinal);
        var actionDigest = CreateActionDigest(operation, normalizedArguments);
        var command = new AppleTestAgentOperationCommand
        {
            SessionId = Session.SessionId,
            Target = Session.Target,
            AuthorityEpoch = Session.AuthorityEpoch,
            CommandId = $"apple-{Guid.NewGuid():N}",
            Sequence = sequence,
            ActionDigest = actionDigest,
            ApprovalDigest = Session.ApprovalDigest,
            Deadline = GetCommandDeadline(),
            Operation = operation,
            Arguments = normalizedArguments,
        };
        command.HostSignature = AppleTestAgentAuthenticator.CreateSignature(
            _secret,
            "COMMAND",
            $"/v1/session/{Session.SessionId}/next",
            command.SessionId,
            command.CommandId,
            command.Sequence,
            command.Deadline.ToUnixTimeSeconds(),
            command.ActionDigest,
            command.ActionDigest);
        return command;
    }

    private string CreateActionDigest(string operation, IReadOnlyDictionary<string, string>? arguments)
    {
        var material = new List<string>
        {
            operation,
            Session.Target.Platform,
            Session.Target.TargetBundleId,
            Session.AuthorityEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Session.ApprovalDigest ?? string.Empty,
        };
        if (arguments is not null)
        {
            material.AddRange(arguments
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair =>
                    $"{Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Key))}={Convert.ToBase64String(Encoding.UTF8.GetBytes(pair.Value))}"));
        }
        return AppleTestAgentAuthenticator.ComputeDigest(Encoding.UTF8.GetBytes(string.Join("\n", material)));
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(_stop.Token).ConfigureAwait(false);
                _ = Task.Run(() => HandleAsync(context), _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                context?.Response.Close();
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            if (Session.ExpiresAt <= _clock.GetUtcNow())
            {
                await WriteErrorAsync(context.Response, HttpStatusCode.Gone, "The Apple test-agent session has expired.").ConfigureAwait(false);
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (context.Request.HttpMethod == "POST" && path == $"/v1/session/{Session.SessionId}/hello")
            {
                await HandleHelloAsync(context).ConfigureAwait(false);
                return;
            }
            if (context.Request.HttpMethod == "GET" && path == $"/v1/session/{Session.SessionId}/next")
            {
                await HandleNextAsync(context).ConfigureAwait(false);
                return;
            }
            if (context.Request.HttpMethod == "POST" && path.StartsWith($"/v1/session/{Session.SessionId}/commands/", StringComparison.Ordinal) &&
                path.EndsWith("/complete", StringComparison.Ordinal))
            {
                await HandleCompletionAsync(context).ConfigureAwait(false);
                return;
            }
            if (context.Request.HttpMethod == "GET" && path.StartsWith($"/v1/session/{Session.SessionId}/commands/", StringComparison.Ordinal) &&
                path.EndsWith("/cancelled", StringComparison.Ordinal))
            {
                await HandleCancellationQueryAsync(context).ConfigureAwait(false);
                return;
            }
            if (context.Request.HttpMethod == "POST" && path == $"/v1/session/{Session.SessionId}/artifacts")
            {
                await HandleArtifactAsync(context).ConfigureAwait(false);
                return;
            }

            await WriteErrorAsync(context.Response, HttpStatusCode.NotFound, "The Apple test-agent endpoint is unknown.").ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (context.Response.OutputStream.CanWrite)
                await WriteErrorAsync(context.Response, HttpStatusCode.InternalServerError, "The Apple test-agent host could not process the request.").ConfigureAwait(false);
        }
    }

    private async Task HandleHelloAsync(HttpListenerContext context)
    {
        var body = await ReadBodyAsync(context.Request).ConfigureAwait(false);
        var hello = JsonSerializer.Deserialize<AppleTestAgentHello>(body, JsonOptions);
        if (hello is null || !Verify(context.Request, body, commandId: null, sequence: 0))
        {
            await WriteErrorAsync(context.Response, HttpStatusCode.Unauthorized, "The Apple test-agent hello is not authenticated.").ConfigureAwait(false);
            return;
        }

        if (!string.Equals(hello.SessionId, Session.SessionId, StringComparison.Ordinal) ||
            !string.Equals(hello.Target.TargetBundleId, Session.Target.TargetBundleId, StringComparison.Ordinal) ||
            !string.Equals(hello.Target.Platform, Session.Target.Platform, StringComparison.Ordinal))
        {
            await WriteErrorAsync(context.Response, HttpStatusCode.Conflict, "The Apple test-agent hello targets a different app.").ConfigureAwait(false);
            return;
        }

        if (!ValidateAttachment(hello, out var attachmentError))
        {
            await WriteErrorAsync(context.Response, HttpStatusCode.Conflict, attachmentError).ConfigureAwait(false);
            return;
        }

        _lastAgentActivity = _clock.GetUtcNow();
        _agentAttached.TrySetResult(hello);
        await WriteJsonAsync(context.Response, new
        {
            ok = true,
            attachmentCount = AttachmentCount,
            authorityEpoch = Session.AuthorityEpoch,
        }).ConfigureAwait(false);
    }

    private async Task HandleNextAsync(HttpListenerContext context)
    {
        if (!Verify(context.Request, Array.Empty<byte>(), commandId: null, sequence: 0))
        {
            await WriteErrorAsync(context.Response, HttpStatusCode.Unauthorized, "The Apple test-agent poll is not authenticated.").ConfigureAwait(false);
            return;
        }

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        wait.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            var pending = await _commands.Reader.ReadAsync(wait.Token).ConfigureAwait(false);
            LastDeliveredCommandId = pending.Command.CommandId;
            pending.Delivered.TrySetResult();
            _lastAgentActivity = _clock.GetUtcNow();
            await WriteJsonAsync(context.Response, pending.Command).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            context.Response.StatusCode = (int)HttpStatusCode.NoContent;
            context.Response.Close();
        }
    }

    private async Task HandleCompletionAsync(HttpListenerContext context)
    {
        var body = await ReadBodyAsync(context.Request).ConfigureAwait(false);
        var completion = JsonSerializer.Deserialize<AppleTestAgentOperationCompletion>(body, JsonOptions);
        if (completion is null ||
            !IsCompletionBounded(completion) ||
            !Verify(context.Request, body, completion.Receipt.CommandId, completion.Receipt.Sequence))
        {
            await WriteErrorAsync(context.Response, HttpStatusCode.Unauthorized, "The Apple test-agent completion is not authenticated.").ConfigureAwait(false);
            return;
        }

        var result = _ledger.Complete(completion);
        if (result.Receipt is not null)
            LastReceipt = result.Receipt;
        if (!result.Accepted && result.Error is not null)
        {
            await WriteJsonAsync(context.Response, new { ok = false, error = result.Error }, HttpStatusCode.Conflict).ConfigureAwait(false);
            return;
        }

        if (_pending.TryGetValue(completion.Receipt.CommandId, out var pending))
            pending.Completion.TrySetResult(completion);
        _lastAgentActivity = _clock.GetUtcNow();
        await WriteJsonAsync(context.Response, new { ok = true }).ConfigureAwait(false);
    }

    private async Task HandleCancellationQueryAsync(HttpListenerContext context)
    {
        var commandId = SegmentBeforeSuffix(context.Request.Url?.AbsolutePath, "/cancelled");
        if (string.IsNullOrWhiteSpace(commandId) || !Verify(context.Request, Array.Empty<byte>(), commandId, 0))
        {
            await WriteErrorAsync(context.Response, HttpStatusCode.Unauthorized, "The Apple test-agent cancellation query is not authenticated.").ConfigureAwait(false);
            return;
        }

        var cancelled = _cancellations.TryGetValue(commandId, out var cancellation);
        await WriteJsonAsync(context.Response, new
        {
            cancelled,
            reason = cancellation?.Reason,
        }).ConfigureAwait(false);
    }

    private async Task HandleArtifactAsync(HttpListenerContext context)
    {
        var body = await ReadBodyAsync(context.Request).ConfigureAwait(false);
        var chunk = JsonSerializer.Deserialize<AppleTestAgentArtifactChunk>(body, JsonOptions);
        if (chunk is null ||
            !string.Equals(chunk.SessionId, Session.SessionId, StringComparison.Ordinal) ||
            !Verify(context.Request, body, chunk.ArtifactId, chunk.ChunkIndex))
        {
            await WriteErrorAsync(context.Response, HttpStatusCode.Unauthorized, "The Apple test-agent artifact is not authenticated.").ConfigureAwait(false);
            return;
        }

        var error = _artifacts.Add(chunk);
        if (error is not null)
        {
            await WriteJsonAsync(context.Response, new { ok = false, error }, HttpStatusCode.BadRequest).ConfigureAwait(false);
            return;
        }

        _lastAgentActivity = _clock.GetUtcNow();
        await WriteJsonAsync(context.Response, new { ok = true }).ConfigureAwait(false);
    }

    private bool Verify(HttpListenerRequest request, byte[] body, string? commandId, long sequence)
    {
        var authentication = new AppleTestAgentAuthentication
        {
            SessionId = request.Headers["X-Maui-Apple-Session"] ?? "",
            Nonce = request.Headers["X-Maui-Apple-Nonce"] ?? "",
            Signature = request.Headers["X-Maui-Apple-Signature"] ?? "",
        };
        _ = long.TryParse(
            request.Headers["X-Maui-Apple-Timestamp"],
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var timestamp);
        authentication.TimestampUnixSeconds = timestamp;
        if (!string.Equals(authentication.SessionId, Session.SessionId, StringComparison.Ordinal))
            return false;

        return AppleTestAgentAuthenticator.Verify(
            _secret,
            authentication,
            request.HttpMethod,
            request.Url?.AbsolutePath ?? "/",
            commandId,
            sequence,
            AppleTestAgentAuthenticator.ComputeDigest(body),
            _clock.GetUtcNow(),
            TimeSpan.FromMinutes(2),
            _replay);
    }

    private static async Task<byte[]> ReadBodyAsync(HttpListenerRequest request)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[8 * 1024];
        int read;
        while ((read = await request.InputStream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > MaximumRequestBytes)
                throw new InvalidDataException("The Apple test-agent request exceeded its bounded payload.");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    private static Task WriteErrorAsync(HttpListenerResponse response, HttpStatusCode status, string message)
        => WriteJsonAsync(response, new { ok = false, error = message }, status);

    private static async Task WriteJsonAsync(HttpListenerResponse response, object value, HttpStatusCode status = HttpStatusCode.OK)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        response.StatusCode = (int)status;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

    private static string? SegmentBeforeSuffix(string? path, string suffix)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(suffix, StringComparison.Ordinal))
            return null;
        var withoutSuffix = path[..^suffix.Length].TrimEnd('/');
        var slash = withoutSuffix.LastIndexOf('/');
        return slash < 0 ? null : withoutSuffix[(slash + 1)..];
    }

    private static int FindAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static WorkflowCommandException ToException(AppleTestAgentError? error, AppleTestAgentCommandReceipt? receipt)
        => new(
            error?.Code ?? "apple-agent-command",
            error?.Message,
            receipt is null
                ? null
                : new Microsoft.Maui.DevFlow.Driver.WorkflowCommandReceipt
                {
                    RunId = receipt.SessionId,
                    Sequence = receipt.Sequence,
                    CommandId = receipt.CommandId,
                    ActionDigest = receipt.ActionDigest,
                    AuthorityEpoch = receipt.AuthorityEpoch,
                });

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AppleTestAgentHttpHost));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        _stop.Cancel();
        _commands.Writer.TryComplete();
        foreach (var pending in _pending.Values)
        {
            var unknown = _ledger.MarkUnknownCompletion(pending.Command.CommandId);
            pending.Completion.TrySetResult(new AppleTestAgentOperationCompletion
            {
                Receipt = unknown.Receipt ?? new AppleTestAgentCommandReceipt
                {
                    SessionId = Session.SessionId,
                    CommandId = pending.Command.CommandId,
                    Sequence = pending.Command.Sequence,
                    ActionDigest = pending.Command.ActionDigest,
                    AuthorityEpoch = pending.Command.AuthorityEpoch,
                    ApprovalDigest = pending.Command.ApprovalDigest,
                    AcknowledgementState = "unknown-completion",
                    CompletionCertainty = "unknown",
                    At = _clock.GetUtcNow(),
                },
                Ok = false,
                CompletionCertainty = "unknown",
                Error = new AppleTestAgentError
                {
                    Code = AppleTestAgentErrorCodes.AgentOrphaned,
                    Category = "transport",
                    Message = "The Apple test-agent session ended before a durable completion receipt arrived.",
                    Retryable = false,
                },
            });
        }
        if (_listener.IsListening)
            _listener.Stop();
        if (_acceptLoop is not null)
            await _acceptLoop.ConfigureAwait(false);
        _listener.Close();
        _stop.Dispose();
        CryptographicOperations.ZeroMemory(_secret);
    }

    private sealed class PendingCommand(AppleTestAgentOperationCommand command)
    {
        public AppleTestAgentOperationCommand Command { get; } = command;
        public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<AppleTestAgentOperationCompletion> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void EnsureSessionActive()
    {
        if (Session.ExpiresAt <= _clock.GetUtcNow())
            throw new WorkflowCommandException(AppleTestAgentErrorCodes.DeadlineExpired, "The Apple test-agent session has expired.");
    }

    private DateTimeOffset GetCommandDeadline()
    {
        var now = _clock.GetUtcNow();
        var deadline = now.AddSeconds(30);
        return deadline < Session.ExpiresAt ? deadline : Session.ExpiresAt;
    }

    private bool ValidateAttachment(AppleTestAgentHello hello, out string error)
    {
        error = string.Empty;
        if (hello.Capabilities.MaxArtifactChunkBytes is < 1 or > AppleTestAgentProtocolVersions.MaximumArtifactChunkBytes ||
            hello.Capabilities.Operations.Any(operation => !AppleTestAgentOperations.All.Contains(operation)) ||
            string.IsNullOrWhiteSpace(hello.AgentInstanceId))
        {
            error = "The Apple test-agent capability declaration is invalid.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(Session.Target.AppBuildDigest) &&
            !string.Equals(Session.Target.AppBuildDigest, hello.Target.AppBuildDigest, StringComparison.Ordinal))
        {
            error = "The Apple test-agent attachment reports a different app build.";
            return false;
        }
        if (!string.IsNullOrWhiteSpace(Session.Target.AppInstanceId) &&
            !string.Equals(Session.Target.AppInstanceId, hello.Target.AppInstanceId, StringComparison.Ordinal))
        {
            error = "The Apple test-agent attachment reports a different app instance.";
            return false;
        }

        lock (_attachmentGate)
        {
            if (_attachedAgentInstanceId is not null &&
                !string.Equals(_attachedAgentInstanceId, hello.AgentInstanceId, StringComparison.Ordinal))
            {
                error = "A different Apple test-agent instance attempted to attach to this session.";
                return false;
            }

            _attachedAgentInstanceId ??= hello.AgentInstanceId;
            if (string.IsNullOrWhiteSpace(Session.Target.AppInstanceId) &&
                !string.IsNullOrWhiteSpace(hello.Target.AppInstanceId))
            {
                Session.Target.AppInstanceId = hello.Target.AppInstanceId;
            }

            Hello = hello;
            Interlocked.Increment(ref _attachmentCount);
            return true;
        }
    }

    private static bool IsCompletionBounded(AppleTestAgentOperationCompletion completion)
    {
        if (completion.ResultBase64 is { Length: > 0 })
        {
            var maximumBase64Length = ((AppleTestAgentProtocolVersions.MaximumOperationResultBytes + 2) / 3 * 4) + 4;
            if (completion.ResultBase64.Length > maximumBase64Length)
                return false;
            try
            {
                if (Convert.FromBase64String(completion.ResultBase64).Length > AppleTestAgentProtocolVersions.MaximumOperationResultBytes)
                    return false;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        return completion.Artifacts.Count <= AppleTestAgentProtocolVersions.MaximumArtifactChunks;
    }
}
