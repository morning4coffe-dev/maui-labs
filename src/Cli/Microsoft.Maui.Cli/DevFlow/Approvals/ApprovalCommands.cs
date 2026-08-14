using System.CommandLine;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.Cli.DevFlow.Broker;
using Microsoft.Maui.Cli.DevFlow.Mcp;

namespace Microsoft.Maui.Cli.DevFlow.Approvals;

/// <summary>
/// <c>maui devflow approve</c>: review, approve, or reject the mutating requests a restricted test
/// agent has queued against a connected app.
///
/// <para>
/// <b>This command is operator convenience, not an authorization boundary.</b> It presents the
/// owner-only native-host approval token from the broker state file and then performs the same
/// two-step confirmation ceremony an IDE extension performs. An MCP agent runs as the same OS user
/// with the same file permissions, so it can read the same token and call the same routes. Nothing
/// here proves a human is at the keyboard, and no flag, prompt, or code could make it prove that on
/// a shared user account. Treat it as a convenient place for the person who already controls the
/// machine to see and decide what the agent asked for.
/// </para>
/// </summary>
internal static class ApprovalCommands
{
    private const string ApproveConfirmationAction = "agent-request-approve";
    private const int DefaultGrantSeconds = 300;
    private const int MinimumGrantSeconds = 1;
    private const int MaximumGrantSeconds = 900;

    private const string NotABoundaryNotice =
        "This command is operator convenience, not an authorization boundary: an agent running as " +
        "the same OS user can read the same broker token and call the same routes.";

    public static Command Create(
        Option<bool> jsonOption,
        Option<bool> noJsonOption,
        Option<int> agentPortOption,
        IDevFlowOutputWriter output,
        Action onError)
    {
        var idArgument = new Argument<string?>("approval-request-id")
        {
            Description = "The approval request to decide. Omit it to list what is pending.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var listOption = new Option<bool>("--list")
        {
            Description = "List the agent's pending approval requests without deciding anything.",
        };
        var rejectOption = new Option<bool>("--reject")
        {
            Description = "Reject the request instead of approving it. No grant is issued and no app or test state changes.",
        };
        var reasonOption = new Option<string?>("--reason")
        {
            Description = "Short reason code recorded with a rejection, for example 'wrong-target' or 'scope-too-broad'.",
        };
        var grantSecondsOption = new Option<int>("--grant-seconds")
        {
            Description = $"Lifetime of the issued grant in seconds ({MinimumGrantSeconds}-{MaximumGrantSeconds}, default {DefaultGrantSeconds}).",
            DefaultValueFactory = static _ => DefaultGrantSeconds,
        };
        var yesOption = new Option<bool>("--yes", "-y")
        {
            Description = "Skip the interactive confirmation prompt. This is an ergonomic shortcut only; it removes no protection, because the prompt was never one.",
        };

        var command = new Command(
            "approve",
            "Review, approve, or reject a restricted test agent's pending mutating request")
        {
            idArgument,
            listOption,
            rejectOption,
            reasonOption,
            grantSecondsOption,
            yesOption,
        };
        command.Description =
            "Review, approve, or reject a restricted test agent's pending mutating request.\n\n" +
            "NOT AN AUTHORIZATION BOUNDARY. This command reads the owner-only native-host approval " +
            "token from the local broker state file and performs the same confirmation ceremony an " +
            "IDE extension performs. A DevFlow MCP agent runs as the same OS user with the same file " +
            "permissions, so it can read that same token and call those same routes. This command " +
            "makes approval reachable for a human who has no IDE extension installed; it does not, " +
            "and cannot, prove that a human rather than a local agent process made the decision.";

        command.SetAction(async (ctx, ct) =>
        {
            var json = output.ResolveJsonMode(ctx.GetValue(jsonOption), ctx.GetValue(noJsonOption));
            var approvalRequestId = ctx.GetValue(idArgument);
            var list = ctx.GetValue(listOption) || string.IsNullOrWhiteSpace(approvalRequestId);
            var reject = ctx.GetValue(rejectOption);
            var reason = ctx.GetValue(reasonOption);
            var grantSeconds = ctx.GetValue(grantSecondsOption);
            var assumeYes = ctx.GetValue(yesOption);
            var requestedAgentPort = ctx.GetValue(agentPortOption);

            if (!list && grantSeconds is < MinimumGrantSeconds or > MaximumGrantSeconds)
            {
                output.WriteError(
                    $"--grant-seconds must be between {MinimumGrantSeconds} and {MaximumGrantSeconds}.",
                    json,
                    "InvalidRequest");
                onError();
                return;
            }

            var context = await ResolveContextAsync(output, json, requestedAgentPort, onError, ct).ConfigureAwait(false);
            if (context is null)
                return;

            var listing = await TestAgentBrokerClient.GetInspectorWorkbenchAsync(
                context.BrokerPort,
                context.AgentId,
                "/api/workbench/agent-requests",
                context.HostToken,
                ct).ConfigureAwait(false);
            if (!TryUnwrap(listing, output, json, onError, "read the agent's approval requests"))
                return;

            if (list)
            {
                WriteListing(listing.Value, output, json);
                return;
            }

            if (!TryFindRequest(listing.Value, approvalRequestId!, out var record))
            {
                output.WriteError(
                    $"No approval request '{approvalRequestId}' is queued against the selected app. Run 'maui devflow approve --list' to see current request IDs; requests expire and disappear.",
                    json,
                    "ApprovalRequestNotFound",
                    suggestions: ["maui devflow approve --list"]);
                onError();
                return;
            }

            if (!string.Equals(record.State, "pending", StringComparison.Ordinal))
            {
                output.WriteError(
                    $"Approval request '{approvalRequestId}' is already '{record.State}'. Only a pending request can be decided.",
                    json,
                    "ApprovalRequestNotPending");
                onError();
                return;
            }

            if (reject)
            {
                await RejectAsync(context, record, reason, assumeYes, output, json, onError, ct).ConfigureAwait(false);
                return;
            }

            await ApproveAsync(context, record, grantSeconds, assumeYes, output, json, onError, ct).ConfigureAwait(false);
        });

        return command;
    }

    private static async Task ApproveAsync(
        ApprovalContext context,
        PendingApproval record,
        int grantSeconds,
        bool assumeYes,
        IDevFlowOutputWriter output,
        bool json,
        Action onError,
        CancellationToken cancellationToken)
    {
        if (!Confirm(record, $"approve (grant lifetime {grantSeconds}s)", assumeYes, json))
        {
            output.WriteError("Approval cancelled. No grant was issued.", json, "ApprovalCancelled");
            onError();
            return;
        }

        // The confirmation capability expires two minutes after it is issued, so it is minted only
        // after every human-facing prompt has already returned.
        var issueBody = new StringBuilder()
            .Append("{\"action\":\"").Append(ApproveConfirmationAction).Append("\",\"subjectId\":")
            .Append(JsonSerializer.Serialize(record.ApprovalRequestId))
            .Append(",\"approvedScope\":").Append(record.RequestedScopeJson)
            .Append(",\"grantDurationSeconds\":").Append(grantSeconds)
            .Append('}')
            .ToString();
        var issued = await TestAgentBrokerClient.PostInspectorWorkbenchAsync(
            context.BrokerPort,
            context.AgentId,
            "/api/workbench/approval-confirmations/issue",
            issueBody,
            context.HostToken,
            cancellationToken).ConfigureAwait(false);
        if (!TryUnwrap(issued, output, json, onError, "issue an approval confirmation"))
            return;
        if (!issued.Value.TryGetProperty("confirmationCapability", out var capability) ||
            capability.ValueKind != JsonValueKind.String)
        {
            output.WriteError(
                "The Inspector issued no confirmation capability, so no approval was sent.",
                json,
                "ApprovalConfirmationUnavailable");
            onError();
            return;
        }

        // The confirmation digest covers the approved scope and grant duration, so both calls must
        // carry identical values. The scope is forwarded exactly as the broker reported it and is
        // never re-normalized here.
        var approveBody = new StringBuilder()
            .Append("{\"humanConfirmed\":true,\"confirmationCapability\":")
            .Append(JsonSerializer.Serialize(capability.GetString()))
            .Append(",\"approvedScope\":").Append(record.RequestedScopeJson)
            .Append(",\"grantDurationSeconds\":").Append(grantSeconds)
            .Append(",\"decidedBy\":").Append(DecidedByJson())
            .Append('}')
            .ToString();
        var approved = await TestAgentBrokerClient.PostInspectorWorkbenchAsync(
            context.BrokerPort,
            context.AgentId,
            $"/api/workbench/agent-requests/{Uri.EscapeDataString(record.ApprovalRequestId)}/approve",
            approveBody,
            context.HostToken,
            cancellationToken).ConfigureAwait(false);
        if (!TryUnwrap(approved, output, json, onError, "approve the request"))
            return;

        WriteDecision(approved.Value, record, "Approved", output, json);
    }

    private static async Task RejectAsync(
        ApprovalContext context,
        PendingApproval record,
        string? reason,
        bool assumeYes,
        IDevFlowOutputWriter output,
        bool json,
        Action onError,
        CancellationToken cancellationToken)
    {
        if (!Confirm(record, "reject", assumeYes, json))
        {
            output.WriteError("Rejection cancelled. The request is still pending.", json, "ApprovalCancelled");
            onError();
            return;
        }

        var body = new StringBuilder()
            .Append("{\"humanConfirmed\":true,\"reasonCode\":")
            .Append(JsonSerializer.Serialize(string.IsNullOrWhiteSpace(reason) ? "host-rejected" : reason))
            .Append(",\"decidedBy\":").Append(DecidedByJson())
            .Append('}')
            .ToString();
        var rejected = await TestAgentBrokerClient.PostInspectorWorkbenchAsync(
            context.BrokerPort,
            context.AgentId,
            $"/api/workbench/agent-requests/{Uri.EscapeDataString(record.ApprovalRequestId)}/reject",
            body,
            context.HostToken,
            cancellationToken).ConfigureAwait(false);
        if (!TryUnwrap(rejected, output, json, onError, "reject the request"))
            return;

        WriteDecision(rejected.Value, record, "Rejected", output, json);
    }

    /// <summary>
    /// Bounded provenance labels recorded on the decision so an operator can later tell a CLI
    /// decision from a Workbench one. Deliberately excludes the OS user name: the audit journal and
    /// session status are readable by the agent, and the issuing surface is the useful fact.
    /// </summary>
    private static string DecidedByJson()
        => "{\"actorId\":\"maui-cli-operator\",\"channel\":\"cli\",\"provider\":\"maui-cli\"}";

    private static bool Confirm(PendingApproval record, string verb, bool assumeYes, bool json)
    {
        if (assumeYes || json)
            return true;

        Console.WriteLine();
        Console.WriteLine($"Approval request : {record.ApprovalRequestId}");
        Console.WriteLine($"Kind             : {record.Kind}");
        Console.WriteLine($"Intent           : {record.Intent}");
        Console.WriteLine($"Requested scope  : {record.ScopeSummary}");
        if (!string.IsNullOrEmpty(record.CorrelationSummary))
            Console.WriteLine($"Bound to         : {record.CorrelationSummary}");
        Console.WriteLine();
        Console.WriteLine(NotABoundaryNotice);
        Console.WriteLine();
        Console.Write($"Type 'yes' to {verb}: ");
        var answer = Console.ReadLine();
        return string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ApprovalContext?> ResolveContextAsync(
        IDevFlowOutputWriter output,
        bool json,
        int requestedAgentPort,
        Action onError,
        CancellationToken cancellationToken)
    {
        var brokerPort = await DevFlowCommands.ResolveRunningBrokerPortAsync().ConfigureAwait(false);
        if (brokerPort is null)
        {
            output.WriteError(
                "No running DevFlow broker was found. Start one with 'maui devflow broker start'.",
                json,
                "BrokerUnavailable",
                suggestions: ["maui devflow broker start"]);
            onError();
            return null;
        }

        if (!BrokerClient.TryReadNativeApprovalToken(out var hostToken))
        {
            output.WriteError(
                "The broker published no native-host approval token, so approvals cannot be issued. Restart it with 'maui devflow broker start'.",
                json,
                "NativeHostApprovalUnavailable",
                suggestions: ["maui devflow broker start"]);
            onError();
            return null;
        }

        var agents = await DevFlowCommands.ListBrokerAgentsAsync(brokerPort.Value).ConfigureAwait(false);
        var agent = agents?.FirstOrDefault(candidate => candidate.Port == requestedAgentPort)
            ?? (agents is null ? null : BrokerClient.ResolveAgent(agents));
        if (agent?.Id is not { Length: > 0 } agentId)
        {
            output.WriteError(
                "Select exactly one connected app with --agent-port. Approvals are always bound to one exact target.",
                json,
                "AgentAmbiguous",
                suggestions: ["maui devflow list"]);
            onError();
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new ApprovalContext(brokerPort.Value, agentId, hostToken);
    }

    private static bool TryUnwrap(
        TestAgentBrokerRawResponse response,
        IDevFlowOutputWriter output,
        bool json,
        Action onError,
        string operation)
    {
        if (response.TransportError is { Length: > 0 } transportError)
        {
            output.WriteError(transportError, json, "BrokerUnavailable", retryable: true);
            onError();
            return false;
        }
        if (response.StatusCode is >= 200 and < 300 && response.HasValue)
            return true;

        var (code, message) = DescribeFailure(response, operation);
        output.WriteError(message, json, code);
        onError();
        return false;
    }

    /// <summary>
    /// Turns the Inspector's deliberately terse approval failures into distinct, actionable CLI
    /// messages. Several unrelated causes share HTTP 404 with near-identical text, so the mapping
    /// keys off the response body as well as the status code.
    /// </summary>
    private static (string Code, string Message) DescribeFailure(
        TestAgentBrokerRawResponse response,
        string operation)
    {
        var error = response.HasValue &&
                    response.Value.ValueKind == JsonValueKind.Object &&
                    response.Value.TryGetProperty("error", out var errorText) &&
                    errorText.ValueKind == JsonValueKind.String
            ? errorText.GetString() ?? string.Empty
            : string.Empty;
        var code = response.HasValue &&
                   response.Value.ValueKind == JsonValueKind.Object &&
                   response.Value.TryGetProperty("code", out var codeText) &&
                   codeText.ValueKind == JsonValueKind.String
            ? codeText.GetString() ?? string.Empty
            : string.Empty;

        return response.StatusCode switch
        {
            403 when code == "trusted-host-required" => (
                "TrustedHostRequired",
                "The broker rejected this CLI's native-host approval token. The broker was most likely restarted after this shell started; retry the command."),
            403 when code == "approval-confirmation-invalid" => (
                "ApprovalConfirmationInvalid",
                "The approval confirmation was rejected. It is single-use, expires two minutes after it is issued, and is bound to the exact scope and grant lifetime it was minted for. Retry the command."),
            403 => ("Forbidden", $"The Inspector refused to {operation}: {Describe(error, "forbidden")}"),
            404 when error.Contains("does not target this Inspector", StringComparison.OrdinalIgnoreCase) => (
                "ApprovalTargetMismatch",
                "The approval request targets a different app instance than the one selected. Choose the right app with --agent-port."),
            404 when error.Contains("expired", StringComparison.OrdinalIgnoreCase) => (
                "ApprovalRequestExpired",
                "The approval request expired before it was decided. Ask the agent to request approval again."),
            404 when error.Contains("preview capability", StringComparison.OrdinalIgnoreCase) => (
                "PreviewCapabilityDisabled",
                "Agent authoring is disabled on this broker, so there is nothing to approve."),
            404 => ("ApprovalRequestNotFound", $"Could not {operation}: {Describe(error, "the approval request was not found")}"),
            409 => ("TargetChanged", $"Could not {operation}: {Describe(error, "the exact target app instance changed")}"),
            400 => ("InvalidRequest", $"Could not {operation}: {Describe(error, "the request was rejected as invalid")}"),
            501 or 503 => ("ApprovalUnavailable", $"Could not {operation}: {Describe(error, "broker-owned approvals are unavailable")}"),
            _ => ("RuntimeError", $"Could not {operation}: {Describe(error, $"the Inspector returned HTTP {response.StatusCode}")}"),
        };
    }

    private static string Describe(string error, string fallback)
        => string.IsNullOrWhiteSpace(error) ? fallback : error;

    private static bool TryFindRequest(JsonElement listing, string approvalRequestId, out PendingApproval record)
    {
        record = default!;
        if (listing.ValueKind != JsonValueKind.Object ||
            !listing.TryGetProperty("requests", out var requests) ||
            requests.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var candidate in requests.EnumerateArray())
        {
            if (candidate.ValueKind != JsonValueKind.Object ||
                !candidate.TryGetProperty("approvalRequestId", out var id) ||
                id.ValueKind != JsonValueKind.String ||
                !string.Equals(id.GetString(), approvalRequestId, StringComparison.Ordinal))
            {
                continue;
            }
            if (!candidate.TryGetProperty("requestedScope", out var scope) || scope.ValueKind != JsonValueKind.Object)
                return false;

            record = new PendingApproval(
                approvalRequestId,
                ReadString(candidate, "kind") ?? "unknown",
                ReadString(candidate, "state") ?? "unknown",
                ReadString(candidate, "intent") ?? "(no intent recorded)",
                scope.GetRawText(),
                SummarizeScope(scope),
                SummarizeCorrelation(candidate));
            return true;
        }
        return false;
    }

    private static void WriteListing(JsonElement listing, IDevFlowOutputWriter output, bool json)
    {
        if (json)
        {
            output.WriteJsonElement(listing, json: true);
            return;
        }

        var pending = new List<JsonElement>();
        if (listing.ValueKind == JsonValueKind.Object &&
            listing.TryGetProperty("requests", out var requests) &&
            requests.ValueKind == JsonValueKind.Array)
        {
            foreach (var request in requests.EnumerateArray())
            {
                if (request.ValueKind == JsonValueKind.Object &&
                    string.Equals(ReadString(request, "state"), "pending", StringComparison.Ordinal))
                {
                    pending.Add(request);
                }
            }
        }

        if (pending.Count == 0)
        {
            Console.WriteLine("No pending approval requests.");
            Console.WriteLine(NotABoundaryNotice);
            return;
        }

        Console.WriteLine($"{pending.Count} pending approval request(s):");
        foreach (var request in pending)
        {
            Console.WriteLine();
            Console.WriteLine($"  id     : {ReadString(request, "approvalRequestId")}");
            Console.WriteLine($"  kind   : {ReadString(request, "kind")}");
            Console.WriteLine($"  intent : {ReadString(request, "intent")}");
            if (request.TryGetProperty("requestedScope", out var scope) && scope.ValueKind == JsonValueKind.Object)
                Console.WriteLine($"  scope  : {SummarizeScope(scope)}");
            var correlation = SummarizeCorrelation(request);
            if (!string.IsNullOrEmpty(correlation))
                Console.WriteLine($"  bound  : {correlation}");
            if (ReadString(request, "expiresAt") is { Length: > 0 } expiresAt)
                Console.WriteLine($"  expires: {expiresAt}");
        }
        Console.WriteLine();
        Console.WriteLine("Approve with: maui devflow approve <id>");
        Console.WriteLine(NotABoundaryNotice);
    }

    private static void WriteDecision(
        JsonElement response,
        PendingApproval record,
        string outcome,
        IDevFlowOutputWriter output,
        bool json)
    {
        if (json)
        {
            output.WriteJsonElement(response, json: true);
            return;
        }

        Console.WriteLine($"{outcome}: {record.ApprovalRequestId} ({record.Kind})");
        if (response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.String)
        {
            Console.WriteLine(message.GetString());
        }
        if (response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("request", out var decided) &&
            decided.ValueKind == JsonValueKind.Object &&
            ReadString(decided, "grantExpiresAt") is { Length: > 0 } grantExpiresAt)
        {
            Console.WriteLine($"Grant expires: {grantExpiresAt}");
        }
    }

    private static string SummarizeScope(JsonElement scope)
    {
        var parts = new List<string>();
        if (JoinArray(scope, "allowedActions") is { Length: > 0 } actions)
            parts.Add($"actions=[{actions}]");
        if (JoinArray(scope, "allowedSideEffectClasses") is { Length: > 0 } classes)
            parts.Add($"sideEffects=[{classes}]");
        if (JoinArray(scope, "allowedSelectors") is { Length: > 0 } selectors)
            parts.Add($"selectors=[{selectors}]");
        if (JoinArray(scope, "allowedRoutes") is { Length: > 0 } routes)
            parts.Add($"routes=[{routes}]");
        if (scope.TryGetProperty("maxActionCount", out var maxActions) &&
            maxActions.ValueKind == JsonValueKind.Number)
        {
            parts.Add($"maxActions={maxActions.GetInt32()}");
        }
        return parts.Count == 0 ? "(empty)" : string.Join(", ", parts);
    }

    private static string SummarizeCorrelation(JsonElement request)
    {
        if (!request.TryGetProperty("correlation", out var correlation) ||
            correlation.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (ReadString(correlation, "flowId") is { Length: > 0 } flowId)
            parts.Add($"flow={flowId}");
        if (correlation.TryGetProperty("flowRevision", out var flowRevision) &&
            flowRevision.ValueKind == JsonValueKind.Number)
        {
            parts.Add($"flowRevision={flowRevision.GetInt32()}");
        }
        if (correlation.TryGetProperty("planRevision", out var planRevision) &&
            planRevision.ValueKind == JsonValueKind.Number)
        {
            parts.Add($"planRevision={planRevision.GetInt32()}");
        }
        return string.Join(", ", parts);
    }

    private static string JoinArray(JsonElement owner, string property)
    {
        if (!owner.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            return string.Empty;
        return string.Join(
            ", ",
            array.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString()));
    }

    private static string? ReadString(JsonElement owner, string property)
        => owner.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record ApprovalContext(int BrokerPort, string AgentId, string HostToken);

    private sealed record PendingApproval(
        string ApprovalRequestId,
        string Kind,
        string State,
        string Intent,
        string RequestedScopeJson,
        string ScopeSummary,
        string CorrelationSummary);
}
