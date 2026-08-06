using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Maui.Cli.DevFlow.Broker;
using Microsoft.Maui.Cli.DevFlow.Mcp;
using Microsoft.Maui.DevFlow.Testing;
using ModelContextProtocol.Server;

namespace Microsoft.Maui.Cli.DevFlow.Mcp.Tools;

/// <summary>Static and bounded live validation for restricted test-agent drafts.</summary>
[McpServerToolType]
public sealed class TestAgentValidationTool
{
    [McpServerTool(Name = "maui_test_validate"),
     System.ComponentModel.Description("Validate the current broker-owned draft statically or with a read-only live structural check on an explicit target. Live validation never drives the app and labels UI-derived facts untrusted.")]
    public static async Task<string> Validate(
        [System.ComponentModel.Description("MCP session injected by the server and used for the local broker and exact target")] McpAgentSession session,
        [System.ComponentModel.Description("Typed validation request with envelope and mode static or live")] MauiTestAgentValidationRequest request)
    {
        if (request?.Envelope is null)
        {
            return TestAgentToolSupport.Failure(null, TestAgentToolSupport.Error(
                MauiTestAgentErrorCodes.InvalidRequest,
                MauiTestAgentErrorCategories.Validation,
                "A validation request with a protocol envelope is required.",
                retryable: false));
        }

        var snapshotResult = await TestAgentToolSupport.SessionAsync(session, request.Envelope).ConfigureAwait(false);
        if (snapshotResult.Value?.Ok != true || snapshotResult.Value.Snapshot is null)
            return TestAgentToolSupport.BrokerFailure(request.Envelope.RequestId, snapshotResult);

        var snapshot = snapshotResult.Value.Snapshot;
        var mode = request.Mode?.Trim().ToLowerInvariant();
        if (mode == "static")
        {
            var flow = MauiFlowValidator.Validate(snapshot.Flow!);
            var plan = MauiTestPlanValidator.Validate(snapshot.Plan);
            return TestAgentToolSupport.Success(request.Envelope.RequestId, new
            {
                mode,
                ok = flow.Ok && plan.IsValid,
                flow = new { errors = flow.Errors, warnings = flow.Warnings },
                plan = new { errors = plan.Errors, warnings = plan.Warnings },
                flowDigest = snapshot.FlowDigest,
                planDigest = snapshot.PlanDigest,
            });
        }

        if (mode != "live")
        {
            return TestAgentToolSupport.Failure(request.Envelope.RequestId, TestAgentToolSupport.Error(
                MauiTestAgentErrorCodes.UnsupportedOperation,
                MauiTestAgentErrorCategories.Unsupported,
                "Validation mode must be static or live.",
                retryable: false));
        }

        var target = await TestAgentToolSupport.ResolveTargetAsync(session, request.Envelope.Target).ConfigureAwait(false);
        if (target.Error is not null)
            return TestAgentToolSupport.Failure(request.Envelope.RequestId, target.Error);

        using var agent = await session.GetTestAgentClientAsync(request.Envelope.Target).ConfigureAwait(false);
        var tree = await agent.GetTreeAsync(maxDepth: 8).ConfigureAwait(false);
        var findings = new List<object>();
        foreach (var step in snapshot.Flow?.Steps ?? [])
        {
            var selector = step.Args?.Selector ?? step.Target;
            if (selector is null || selector.IsEmpty)
                continue;
            if (!string.IsNullOrWhiteSpace(selector.AutomationId))
            {
                var matches = await agent.QueryAsync(automationId: selector.AutomationId).ConfigureAwait(false);
                var resolved = selector.HasScopedStableItem
                    ? matches.Where(match =>
                        string.Equals(match.StableItemKey, selector.StableItemKey, StringComparison.Ordinal) &&
                        string.Equals(match.CollectionScope, selector.CollectionScope, StringComparison.Ordinal)).ToArray()
                    : matches.ToArray();
                findings.Add(new
                {
                    sequence = step.Seq,
                    selector = MauiTestAgentSelectorScopeKey.FromSelector(selector),
                    matchCount = resolved.Length,
                    types = resolved.Select(match => match.Type).Where(type => !string.IsNullOrWhiteSpace(type)).Distinct().Take(8).ToArray(),
                });
            }
            else
            {
                findings.Add(new
                {
                    sequence = step.Seq,
                    selector = "non-durable",
                    matchCount = (int?)null,
                    warning = "Only durable AutomationId selectors are queried during restricted live validation.",
                });
            }
        }

        return TestAgentToolSupport.Success(
            request.Envelope.RequestId,
            new
            {
                mode,
                target = target.State,
                rootCount = tree.Count,
                findings,
            },
            [TestAgentToolSupport.Untrusted("ui-tree")]);
    }
}

/// <summary>Broker-owned start, status, and cancellation operations for restricted test runs.</summary>
[McpServerToolType]
public sealed class TestAgentRunTool
{
    [McpServerTool(Name = "maui_test_run"),
     System.ComponentModel.Description("Start, inspect, or cancel a broker-owned canonical test run. Start and cancel require a human-issued mutation grant. An approved start is the explicit one-shot authorization required by a non-replayable plan; it still never enables repeat replay or repair. Status is read-only. Pause and continue are explicitly unsupported and never simulated.")]
    public static async Task<string> Run(
        [System.ComponentModel.Description("MCP session injected by the server and used for the local broker and exact target")] McpAgentSession session,
        [System.ComponentModel.Description("Typed run request with operation start, status, cancel, pause, or continue and a complete protocol envelope")] MauiTestAgentRunRequest request)
    {
        if (request?.Envelope is null)
        {
            return TestAgentToolSupport.Failure(null, TestAgentToolSupport.Error(
                MauiTestAgentErrorCodes.InvalidRequest,
                MauiTestAgentErrorCategories.Validation,
                "A typed run request with a protocol envelope is required.",
                retryable: false));
        }

        var operation = request.Operation?.Trim().ToLowerInvariant();
        return operation switch
        {
            "start" => await StartAsync(session, request).ConfigureAwait(false),
            "status" => await StatusAsync(session, request).ConfigureAwait(false),
            "cancel" => await CancelAsync(session, request).ConfigureAwait(false),
            "pause" or "continue" => TestAgentToolSupport.Failure(request.Envelope.RequestId, TestAgentToolSupport.Error(
                MauiTestAgentErrorCodes.UnsupportedOperation,
                MauiTestAgentErrorCategories.Unsupported,
                "The broker does not support pause or continue for restricted test runs.",
                retryable: false)),
            _ => TestAgentToolSupport.Failure(request.Envelope.RequestId, TestAgentToolSupport.Error(
                MauiTestAgentErrorCodes.UnsupportedOperation,
                MauiTestAgentErrorCategories.Unsupported,
                "Supported run operations are start, status, and cancel.",
                retryable: false)),
        };
    }

    private static async Task<string> StartAsync(McpAgentSession session, MauiTestAgentRunRequest request)
    {
        var envelope = request.Envelope!;
        var snapshot = await TestAgentToolSupport.SessionAsync(session, envelope).ConfigureAwait(false);
        if (snapshot.Value?.Ok != true || snapshot.Value.Snapshot is null)
            return TestAgentToolSupport.BrokerFailure(envelope.RequestId, snapshot);
        var target = await TestAgentToolSupport.ResolveTargetAsync(session, envelope.Target).ConfigureAwait(false);
        if (target.Error is not null)
            return TestAgentToolSupport.Failure(envelope.RequestId, target.Error);

        var startRequest = new WorkflowRunStartRequest
        {
            AgentId = envelope.Target!.AgentId,
            AgentInstanceId = envelope.Target.AgentInstanceId,
            IdempotencyKey = envelope.IdempotencyKey,
            Flow = snapshot.Value.Snapshot.Flow,
            Plan = snapshot.Value.Snapshot.Plan,
            TimeoutMs = request.TimeoutMs,
            Context = CreateRunContext(
                snapshot.Value.Snapshot.Plan,
                snapshot.Value.Snapshot.TargetState,
                target.State),
        };
        var startJson = TestAgentBrokerClient.SerializeWorkflowRunRequest(
            startRequest,
            DevFlowCliJsonContext.Default.WorkflowRunStartRequest);

        var authorization = await TestAgentToolSupport.AuthorizeAsync(
            session,
            envelope,
            MauiTestAgentActions.Run,
            sideEffectClass: "run").ConfigureAwait(false);
        if (authorization.Value?.Ok != true || authorization.Value.AuthorizationId is null)
            return TestAgentToolSupport.BrokerFailure(envelope.RequestId, authorization);

        var brokerPort = await session.GetBrokerPortAsync().ConfigureAwait(false);
        var started = await TestAgentBrokerClient.PostWorkflowRunJsonAsync(
            brokerPort,
            "/api/workflow-runs/start",
            startJson).ConfigureAwait(false);
        if (!started.HasValue)
        {
            await CompleteRunMutationAsync(
                session,
                authorization.Value.AuthorizationId,
                "unknown-completion",
                null,
                MauiTestAgentErrorCodes.UnknownCompletion).ConfigureAwait(false);
            return TestAgentToolSupport.Failure(envelope.RequestId, TestAgentToolSupport.Error(
                MauiTestAgentErrorCodes.UnknownCompletion,
                MauiTestAgentErrorCategories.UnknownCompletion,
                started.TransportError ?? "Run start completion is unknown and will not be retried.",
                retryable: false));
        }

        if (!TryReadStartedRun(started.Value, out var runId, out var capabilityToken))
        {
            var rejection = CreateRunStartRejection(started.Value);
            await CompleteRunMutationAsync(
                session,
                authorization.Value.AuthorizationId,
                "rejected",
                null,
                rejection.Code).ConfigureAwait(false);
            return TestAgentToolSupport.Failure(envelope.RequestId, rejection);
        }

        var sessionId = envelope.Correlation!.AuthoringSessionId!;
        session.RememberTestRunCapability(sessionId, runId!, capabilityToken!);
        await TestAgentBrokerClient.BindRunAsync(
            brokerPort,
            new MauiTestAgentRunBindingRequest
            {
                SessionId = sessionId,
                ReadCapabilityId = envelope.ReadCapabilityId,
                RunId = runId,
                RunCapabilityToken = capabilityToken,
            }).ConfigureAwait(false);
        await CompleteRunMutationAsync(session, authorization.Value.AuthorizationId, "queued", runId, null).ConfigureAwait(false);

        return TestAgentToolSupport.Success(envelope.RequestId, new
        {
            runId,
            state = "queued",
            // This is a target/run-bound capability, not ambient authorization. It is returned so
            // a caller may make read-only status requests after an MCP process restart.
            runCapabilityToken = capabilityToken,
        });
    }

    internal static MauiFlowRunContext CreateRunContext(
        MauiTestPlan? plan,
        MauiTestAgentTargetState? expectedState = null,
        MauiTestAgentTargetState? observedState = null) => new()
    {
        Intent = MauiFlowReplayIntents.OrdinaryReplay,
        ManualOneShotAuthorization =
            plan?.ParsedSideEffectPolicy == MauiFlowSideEffectPolicy.NonReplayable
                ? true
                : null,
        Preconditions = expectedState is null && observedState is null
            ? null
            : new MauiFlowReplayPreconditions
            {
                Expected = ToCheckpoint(expectedState),
                Observed = ToCheckpoint(observedState),
                CheckedAt = observedState?.ObservedAt ?? DateTimeOffset.UtcNow,
            },
    };

    internal static MauiTestAgentError CreateRunStartRejection(JsonElement result)
    {
        var messages = new List<string>();
        AddBoundedMessage(messages, String(result, "error"));
        if (result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Array)
        {
            foreach (var error in errors.EnumerateArray())
            {
                if (error.ValueKind == JsonValueKind.String)
                    AddBoundedMessage(messages, error.GetString());
                if (messages.Count == 4)
                    break;
            }
        }

        return TestAgentToolSupport.Error(
            MauiTestAgentErrorCodes.InvalidRequest,
            MauiTestAgentErrorCategories.State,
            messages.Count == 0
                ? "The broker rejected the run before dispatch."
                : string.Join(" ", messages),
            retryable: false);
    }

    private static async Task<string> StatusAsync(McpAgentSession session, MauiTestAgentRunRequest request)
    {
        var envelope = request.Envelope!;
        var snapshot = await TestAgentToolSupport.SessionAsync(session, envelope).ConfigureAwait(false);
        if (snapshot.Value?.Ok != true)
            return TestAgentToolSupport.BrokerFailure(envelope.RequestId, snapshot);
        if (!TryGetRunAccess(session, request, out var runId, out var token, out var error))
            return TestAgentToolSupport.Failure(envelope.RequestId, error!);

        var brokerPort = await session.GetBrokerPortAsync().ConfigureAwait(false);
        var result = await TestAgentBrokerClient.PostWorkflowRunAsync(
            brokerPort,
            $"/api/workflow-runs/{Uri.EscapeDataString(runId!)}/status",
            new WorkflowRunAccessRequest { CapabilityToken = token },
            DevFlowCliJsonContext.Default.WorkflowRunAccessRequest).ConfigureAwait(false);
        return result.HasValue
            ? TestAgentToolSupport.Success(envelope.RequestId, SafeRunProjection(result.Value))
            : TestAgentToolSupport.Failure(envelope.RequestId, TestAgentToolSupport.Error(
                MauiTestAgentErrorCodes.TargetUnavailable,
                MauiTestAgentErrorCategories.Transport,
                result.TransportError ?? "The broker did not return run status.",
                retryable: true));
    }

    private static async Task<string> CancelAsync(McpAgentSession session, MauiTestAgentRunRequest request)
    {
        var envelope = request.Envelope!;
        if (!TryGetRunAccess(session, request, out var runId, out var token, out var accessError))
            return TestAgentToolSupport.Failure(envelope.RequestId, accessError!);

        var accessJson = TestAgentBrokerClient.SerializeWorkflowRunRequest(
            new WorkflowRunAccessRequest { CapabilityToken = token },
            DevFlowCliJsonContext.Default.WorkflowRunAccessRequest);

        var authorization = await TestAgentToolSupport.AuthorizeAsync(
            session,
            envelope,
            MauiTestAgentActions.Cancel,
            sideEffectClass: "run").ConfigureAwait(false);
        if (authorization.Value?.Ok != true || authorization.Value.AuthorizationId is null)
            return TestAgentToolSupport.BrokerFailure(envelope.RequestId, authorization);

        var brokerPort = await session.GetBrokerPortAsync().ConfigureAwait(false);
        var cancelled = await TestAgentBrokerClient.PostWorkflowRunJsonAsync(
            brokerPort,
            $"/api/workflow-runs/{Uri.EscapeDataString(runId!)}/cancel",
            accessJson).ConfigureAwait(false);
        if (!cancelled.HasValue)
        {
            await CompleteRunMutationAsync(
                session,
                authorization.Value.AuthorizationId,
                "unknown-completion",
                runId,
                MauiTestAgentErrorCodes.UnknownCompletion).ConfigureAwait(false);
            return TestAgentToolSupport.Failure(envelope.RequestId, TestAgentToolSupport.Error(
                MauiTestAgentErrorCodes.UnknownCompletion,
                MauiTestAgentErrorCategories.UnknownCompletion,
                cancelled.TransportError ?? "Run cancellation completion is unknown and will not be retried.",
                retryable: false));
        }

        await CompleteRunMutationAsync(session, authorization.Value.AuthorizationId, "completed", runId, null).ConfigureAwait(false);
        return TestAgentToolSupport.Success(envelope.RequestId, SafeRunProjection(cancelled.Value));
    }

    internal static bool TryGetRunAccess(
        McpAgentSession session,
        MauiTestAgentRunRequest request,
        out string? runId,
        out string? capabilityToken,
        out MauiTestAgentError? error)
    {
        runId = request.Envelope?.Correlation?.RunId;
        capabilityToken = request.RunCapabilityToken;
        error = null;
        if (string.IsNullOrWhiteSpace(runId))
        {
            error = TestAgentToolSupport.Error(
                MauiTestAgentErrorCodes.InvalidRequest,
                MauiTestAgentErrorCategories.Validation,
                "Run status and cancellation require envelope.correlation.runId.",
                retryable: false);
            return false;
        }
        if (string.IsNullOrWhiteSpace(capabilityToken) &&
            request.Envelope?.Correlation?.AuthoringSessionId is { Length: > 0 } sessionId)
        {
            session.TryGetTestRunCapability(sessionId, runId, out capabilityToken);
        }
        if (string.IsNullOrWhiteSpace(capabilityToken))
        {
            error = TestAgentToolSupport.Error(
                MauiTestAgentErrorCodes.ReadCapabilityRequired,
                MauiTestAgentErrorCategories.Authorization,
                "A target/run-bound capability token from start is required for run status or cancellation.",
                retryable: false);
            return false;
        }
        return true;
    }

    internal static object SafeRunProjection(JsonElement response)
    {
        var run = response.ValueKind == JsonValueKind.Object &&
                  response.TryGetProperty("run", out var nested) &&
                  nested.ValueKind == JsonValueKind.Object
            ? nested
            : default;
        if (run.ValueKind != JsonValueKind.Object)
            return new { ok = false, state = "unavailable" };

        return new
        {
            runId = String(run, "runId"),
            state = String(run, "state"),
            terminal = Bool(run, "terminal"),
            flowDigest = String(run, "flowDigest"),
            currentStepId = String(run, "currentStepId"),
            completedSteps = Number(run, "completedSteps"),
            totalSteps = Number(run, "totalSteps"),
            firstDivergence = Number(run, "firstDivergence"),
            cancellationRequested = Bool(run, "cancellationRequested"),
            failure = SafeFailure(run),
        };
    }

    private static object? SafeFailure(JsonElement run)
    {
        if (!run.TryGetProperty("report", out var report) ||
            report.ValueKind != JsonValueKind.Object ||
            !report.TryGetProperty("failure", out var failure) ||
            failure.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return new
        {
            failureId = String(failure, "failureId"),
            @class = String(failure, "class"),
            code = String(failure, "code"),
            category = String(failure, "category"),
            phase = String(failure, "phase"),
            retryable = Bool(failure, "retryable"),
            repairEligible = Bool(failure, "repairEligible"),
            stepId = String(failure, "stepId"),
        };
    }

    private static bool TryReadStartedRun(JsonElement result, out string? runId, out string? capabilityToken)
    {
        runId = null;
        capabilityToken = null;
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("ok", out var ok) ||
            ok.ValueKind != JsonValueKind.True ||
            !result.TryGetProperty("run", out var run) ||
            run.ValueKind != JsonValueKind.Object ||
            !run.TryGetProperty("runId", out var runIdElement) ||
            runIdElement.ValueKind != JsonValueKind.String ||
            !result.TryGetProperty("capabilityToken", out var token) ||
            token.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        runId = runIdElement.GetString();
        capabilityToken = token.GetString();
        return !string.IsNullOrWhiteSpace(runId) && !string.IsNullOrWhiteSpace(capabilityToken);
    }

    private static void AddBoundedMessage(List<string> messages, string? message)
    {
        var normalized = message?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) ||
            messages.Contains(normalized, StringComparer.Ordinal))
        {
            return;
        }

        messages.Add(normalized.Length > 512 ? normalized[..512] : normalized);
    }

    private static MauiFlowCheckpoint? ToCheckpoint(MauiTestAgentTargetState? state)
        => state is null
            ? null
            : new MauiFlowCheckpoint
            {
                AppBuildFingerprint = NormalizeFingerprint(state.AppBuildFingerprint),
                AgentInstanceId = state.AgentInstanceId,
                SeedFingerprint = state.SeedFingerprint,
                BackendStateFingerprint = state.BackendStateFingerprint,
                Route = state.Route,
                Window = state.Window,
            };

    private static string? NormalizeFingerprint(string? value)
        => value is null ||
           string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "unknown:unknown", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;

    private static async Task CompleteRunMutationAsync(
        McpAgentSession session,
        string authorizationId,
        string outcome,
        string? runId,
        string? errorCode)
    {
        var brokerPort = await session.GetBrokerPortAsync().ConfigureAwait(false);
        await TestAgentBrokerClient.CompleteAsync(
            brokerPort,
            new MauiTestAgentMutationCompletion
            {
                AuthorizationId = authorizationId,
                Outcome = outcome,
                ActionDigest = TestAgentToolSupport.Digest("run"),
                ResultDigest = runId is null ? null : TestAgentToolSupport.Digest(runId),
                RunId = runId,
                ErrorCode = errorCode,
            }).ConfigureAwait(false);
    }

    private static string? String(JsonElement source, string name)
        => source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Number(JsonElement source, string name)
        => source.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

    private static bool? Bool(JsonElement source, string name)
        => source.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}
