using System.ComponentModel;
using System.Text.Json;
using Microsoft.Maui.Cli.DevFlow.Broker;
using Microsoft.Maui.Cli.DevFlow.Mcp;
using Microsoft.Maui.DevFlow.Driver;
using Microsoft.Maui.DevFlow.Testing;
using ModelContextProtocol.Server;

namespace Microsoft.Maui.Cli.DevFlow.Mcp.Tools;

/// <summary>Bounded, read-only trace projection for the restricted test-agent profile.</summary>
[McpServerToolType]
public sealed class TestAgentTraceTool
{
    [McpServerTool(Name = "maui_test_trace"),
     System.ComponentModel.Description("Read a bounded safe projection of a broker-owned local run report. It never returns raw screenshots, source, logs, network bodies, file content, or imported artifact content.")]
    public static async Task<string> Trace(
        [System.ComponentModel.Description("MCP session injected by the server and used for the local broker")] McpAgentSession session,
        [System.ComponentModel.Description("Typed trace request with envelope, runId, and target/run-bound capability token when needed")] MauiTestAgentTraceRequest request)
    {
        if (request?.Envelope is null)
        {
            return TestAgentToolSupport.Failure(null, TestAgentToolSupport.Error(
                MauiTestAgentErrorCodes.InvalidRequest,
                MauiTestAgentErrorCategories.Validation,
                "A trace request with a protocol envelope is required.",
                retryable: false));
        }

        var sessionResult = await TestAgentToolSupport.SessionAsync(session, request.Envelope).ConfigureAwait(false);
        if (sessionResult.Value?.Ok != true)
            return TestAgentToolSupport.BrokerFailure(request.Envelope.RequestId, sessionResult);
        if (!TryRunAccess(session, request, out var runId, out var token, out var error))
            return TestAgentToolSupport.Failure(request.Envelope.RequestId, error!);

        var brokerPort = await session.GetBrokerPortAsync().ConfigureAwait(false);
        var result = await TestAgentBrokerClient.PostWorkflowRunAsync(
            brokerPort,
            $"/api/workflow-runs/{Uri.EscapeDataString(runId!)}/status",
            new WorkflowRunAccessRequest { CapabilityToken = token },
            DevFlowCliJsonContext.Default.WorkflowRunAccessRequest).ConfigureAwait(false);
        if (!result.HasValue)
        {
            return TestAgentToolSupport.Failure(request.Envelope.RequestId, TestAgentToolSupport.Error(
                MauiTestAgentErrorCodes.TargetUnavailable,
                MauiTestAgentErrorCategories.Transport,
                result.TransportError ?? "The broker did not return the requested trace projection.",
                retryable: true));
        }

        return TestAgentToolSupport.Success(request.Envelope.RequestId, new
        {
            run = TestAgentRunTool.SafeRunProjection(result.Value),
            trust = new
            {
                state = "locally-produced",
                importedArtifactContentIncluded = false,
                executableAuthority = false,
                proposalAuthority = false,
            },
            omissions = new[]
            {
                "raw-screenshots", "raw-source", "raw-logs", "raw-network-bodies", "raw-files", "imported-artifact-content",
            },
        });
    }

    internal static bool TryRunAccess(
        McpAgentSession session,
        MauiTestAgentTraceRequest request,
        out string? runId,
        out string? capabilityToken,
        out MauiTestAgentError? error)
    {
        runId = request.RunId ?? request.Envelope?.Correlation?.RunId;
        capabilityToken = request.RunCapabilityToken;
        error = null;
        if (string.IsNullOrWhiteSpace(runId))
        {
            error = TestAgentToolSupport.Error(
                MauiTestAgentErrorCodes.InvalidRequest,
                MauiTestAgentErrorCategories.Validation,
                "trace requires a runId or envelope.correlation.runId.",
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
                "A target/run-bound capability token from run start is required.",
                retryable: false);
            return false;
        }
        return true;
    }
}

/// <summary>Deterministic, value-free failure classification for a broker-owned run.</summary>
[McpServerToolType]
public sealed class TestAgentFailureTool
{
    [McpServerTool(Name = "maui_test_failure"),
     System.ComponentModel.Description("Return deterministic failure classification and bounded facts for a broker-owned run. It does not expose raw app logs, user text, network content, screenshots, source, or model prompts.")]
    public static async Task<string> Failure(
        [System.ComponentModel.Description("MCP session injected by the server and used for the local broker")] McpAgentSession session,
        [System.ComponentModel.Description("Typed trace-shaped request with envelope, runId, and target/run-bound capability token")] MauiTestAgentTraceRequest request)
    {
        if (request?.Envelope is null)
        {
            return TestAgentToolSupport.Failure(null, TestAgentToolSupport.Error(
                MauiTestAgentErrorCodes.InvalidRequest,
                MauiTestAgentErrorCategories.Validation,
                "A failure request with a protocol envelope is required.",
                retryable: false));
        }

        var sessionResult = await TestAgentToolSupport.SessionAsync(session, request.Envelope).ConfigureAwait(false);
        if (sessionResult.Value?.Ok != true)
            return TestAgentToolSupport.BrokerFailure(request.Envelope.RequestId, sessionResult);
        if (!TestAgentTraceTool.TryRunAccess(session, request, out var runId, out var token, out var error))
            return TestAgentToolSupport.Failure(request.Envelope.RequestId, error!);

        var brokerPort = await session.GetBrokerPortAsync().ConfigureAwait(false);
        var result = await TestAgentBrokerClient.PostWorkflowRunAsync(
            brokerPort,
            $"/api/workflow-runs/{Uri.EscapeDataString(runId!)}/status",
            new WorkflowRunAccessRequest { CapabilityToken = token },
            DevFlowCliJsonContext.Default.WorkflowRunAccessRequest).ConfigureAwait(false);
        if (!result.HasValue)
        {
            return TestAgentToolSupport.Failure(request.Envelope.RequestId, TestAgentToolSupport.Error(
                MauiTestAgentErrorCodes.TargetUnavailable,
                MauiTestAgentErrorCategories.Transport,
                result.TransportError ?? "The broker did not return failure facts.",
                retryable: true));
        }

        var facts = ReadFailureFacts(result.Value);
        var classification = MauiFlowFailureClassifier.Classify(facts);
        return TestAgentToolSupport.Success(request.Envelope.RequestId, new
        {
            runId,
            classification = new
            {
                @class = classification.FailureClass,
                code = classification.Code,
                category = classification.Category,
                phase = classification.Phase,
                retryable = classification.Retryable,
                repairEligible = classification.RepairEligible,
            },
            facts = new
            {
                terminalOutcome = facts.TerminalOutcome,
                failureClass = facts.FailureClass,
                completionCertain = facts.CompletionCertain,
                beforeDispatch = facts.BeforeDispatch,
                checkpointVerified = facts.CheckpointVerified,
                checkpointMatches = facts.CheckpointMatches,
                routeMatches = facts.RouteMatches,
            },
        });
    }

    private static MauiFlowFailureFacts ReadFailureFacts(JsonElement response)
    {
        var run = response.ValueKind == JsonValueKind.Object &&
                  response.TryGetProperty("run", out var nested) &&
                  nested.ValueKind == JsonValueKind.Object
            ? nested
            : default;
        var terminalState = String(run, "state");
        var failure = run.ValueKind == JsonValueKind.Object &&
                      run.TryGetProperty("report", out var report) &&
                      report.ValueKind == JsonValueKind.Object &&
                      report.TryGetProperty("failure", out var nestedFailure) &&
                      nestedFailure.ValueKind == JsonValueKind.Object
            ? nestedFailure
            : default;
        return new MauiFlowFailureFacts
        {
            TerminalOutcome = terminalState,
            FailureClass = String(failure, "class"),
            LegacyFailureKind = String(failure, "legacyKind"),
            CompletionCertain = terminalState == MauiFlowRunOutcomes.UnknownCompletion ? false : true,
            BeforeDispatch = String(failure, "phase") == "preflight",
            CheckpointVerified = Bool(failure, "repairEligible") == true,
            CheckpointMatches = Bool(failure, "repairEligible") == true,
            RouteMatches = Bool(failure, "repairEligible") == true,
        };
    }

    private static string? String(JsonElement source, string name)
        => source.ValueKind == JsonValueKind.Object &&
           source.TryGetProperty(name, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? Bool(JsonElement source, string name)
        => source.ValueKind == JsonValueKind.Object &&
           source.TryGetProperty(name, out var value) &&
           value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}

/// <summary>Inert patch proposal storage only; no agent path can apply or approve a patch.</summary>
[McpServerToolType]
public sealed class TestAgentPatchTool
{
    [McpServerTool(Name = "maui_test_patch"),
     System.ComponentModel.Description("Store, preview, or reject an inert structured flow patch proposal. Apply and approve are forbidden in this profile; this tool never edits source, flow files, or an app.")]
    public static async Task<string> Patch(
        [System.ComponentModel.Description("MCP session injected by the server and used only for the local broker")] McpAgentSession session,
        [System.ComponentModel.Description("Typed inert patch request with operation proposal, preview, reject, apply, approve, or rollback. Apply, approve, and rollback are rejected.")] MauiTestAgentPatchRequest request)
    {
        if (request?.Envelope is null)
        {
            return TestAgentToolSupport.Failure(null, TestAgentToolSupport.Error(
                MauiTestAgentErrorCodes.InvalidRequest,
                MauiTestAgentErrorCategories.Validation,
                "An inert patch request with a protocol envelope is required.",
                retryable: false));
        }

        var brokerPort = await session.GetBrokerPortAsync().ConfigureAwait(false);
        var result = await TestAgentBrokerClient.PatchAsync(brokerPort, request).ConfigureAwait(false);
        return result.Value?.Ok == true
            ? TestAgentToolSupport.Success(request.Envelope.RequestId, result.Value)
            : TestAgentToolSupport.BrokerFailure(request.Envelope.RequestId, result);
    }
}

/// <summary>Deterministic selector-health findings without candidate application or source proposals.</summary>
[McpServerToolType]
public sealed class TestAgentImprovementsTool
{
    [McpServerTool(Name = "maui_test_improvements"),
     System.ComponentModel.Description("Return deterministic selector-health findings and a bounded value-free live selector projection for the current broker draft. Read-only selector discovery needs no mutation grant; this tool never returns UI text/values or generates, applies, or approves changes.")]
    public static async Task<string> Improvements(
        [System.ComponentModel.Description("MCP session injected by the server and used for the local broker and exact target")] McpAgentSession session,
        [System.ComponentModel.Description("Complete protocol envelope with explicit target and read capability")] MauiTestAgentRequestEnvelope envelope)
    {
        var snapshotResult = await TestAgentToolSupport.SessionAsync(session, envelope).ConfigureAwait(false);
        if (snapshotResult.Value?.Ok != true || snapshotResult.Value.Snapshot is null)
            return TestAgentToolSupport.BrokerFailure(envelope?.RequestId, snapshotResult);

        var target = await TestAgentToolSupport.ResolveTargetAsync(session, envelope.Target).ConfigureAwait(false);
        if (target.Error is not null)
            return TestAgentToolSupport.Failure(envelope.RequestId, target.Error);

        using var agent = await session.GetTestAgentClientAsync(envelope.Target).ConfigureAwait(false);
        var tree = await agent.GetTreeAsync(maxDepth: 8).ConfigureAwait(false);
        var input = new MauiSelectorHealthAnalysisInput
        {
            Flow = snapshotResult.Value.Snapshot.Flow,
            Plan = snapshotResult.Value.Snapshot.Plan,
            LiveTreeComplete = tree.Count > 0,
            Context = new MauiSelectorObservationContext
            {
                AppBuild = target.State?.AppBuildFingerprint,
                Platform = target.Registration?.Platform,
                Route = target.State?.Route,
                Window = target.State?.Window,
            },
        };
        IReadOnlyList<MauiSelectorObservationElement> observedElements = [];
        if (tree.Count > 0)
        {
            // The factory intentionally drops ElementInfo.Text and Value before the analyzer sees
            // anything, so UI content cannot become a policy input or tool output.
            observedElements = MauiSelectorObservationFactory.Create(tree[0], tree, input.Context).Elements;
            input.LiveElements = observedElements.ToList();
        }

        var analysis = MauiSelectorHealthAnalyzer.Analyze(input);
        var selectorProjection = BuildSelectorProjection(observedElements);
        return TestAgentToolSupport.Success(
            envelope.RequestId,
            new
            {
                ruleVersion = analysis.RuleVersion,
                findings = analysis.Findings,
                coverage = analysis.Coverage,
                liveSelectors = new
                {
                    available = tree.Count > 0,
                    observedElementCount = observedElements.Count,
                    returned = selectorProjection.Count,
                    truncated = observedElements.Count > selectorProjection.Count,
                    identities = selectorProjection,
                    omissions = new[] { "text", "value", "runtime-id", "source", "bounds", "screenshots" },
                },
                repairApply = false,
                sourceProposal = false,
            },
            tree.Count > 0 ? [TestAgentToolSupport.Untrusted("ui-tree")] : []);
    }

    internal static IReadOnlyList<TestAgentSelectorIdentityProjection> BuildSelectorProjection(
        IReadOnlyList<MauiSelectorObservationElement> elements,
        int maximum = 128)
    {
        if (elements.Count == 0 || maximum <= 0)
            return [];

        var automationIdCounts = elements
            .Where(static element => !string.IsNullOrWhiteSpace(element.AutomationId))
            .GroupBy(static element => element.AutomationId!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
        var typeIndices = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<TestAgentSelectorIdentityProjection>(Math.Min(maximum, elements.Count));

        foreach (var element in elements.Where(static element => element.IsVisible != false))
        {
            var type = string.IsNullOrWhiteSpace(element.Type) ? element.FullType : element.Type;
            if (string.IsNullOrWhiteSpace(type))
                continue;
            typeIndices.TryGetValue(type, out var typeIndex);
            typeIndices[type] = typeIndex + 1;

            var automationId = string.IsNullOrWhiteSpace(element.AutomationId) ? null : element.AutomationId;
            int? matchCount = automationId is null ? null : automationIdCounts[automationId];
            result.Add(new TestAgentSelectorIdentityProjection
            {
                Type = type,
                Role = element.Role,
                AutomationId = automationId,
                NativeAutomationIdentity = element.NativeAutomationIdentity,
                NativeAutomationIdentityKind = element.NativeAutomationIdentityKind,
                Selector = automationId is not null
                    ? $"automationId:{automationId}"
                    : $"typeIndex:{type}:{typeIndex}",
                SelectorKind = automationId is not null ? "automationId" : "typeIndex",
                MatchCount = matchCount ?? 1,
                Quality = automationId is null
                    ? "fragile"
                    : matchCount == 1
                        ? "durable"
                        : "ambiguous",
                Enabled = element.IsEnabled,
                Focused = element.IsFocused,
                CollectionScope = element.CollectionScope,
                Virtualized = element.IsVirtualized,
            });
            if (result.Count >= maximum)
                break;
        }

        return result;
    }
}

internal sealed class TestAgentSelectorIdentityProjection
{
    public string? Type { get; init; }
    public string? Role { get; init; }
    public string? AutomationId { get; init; }
    public string? NativeAutomationIdentity { get; init; }
    public string? NativeAutomationIdentityKind { get; init; }
    public string? Selector { get; init; }
    public string? SelectorKind { get; init; }
    public int MatchCount { get; init; }
    public string? Quality { get; init; }
    public bool? Enabled { get; init; }
    public bool? Focused { get; init; }
    public string? CollectionScope { get; init; }
    public bool? Virtualized { get; init; }
}
