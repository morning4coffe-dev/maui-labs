using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.Maui.DevFlow.Testing;

/// <summary>Version constants for the provider-neutral restricted test-agent protocol.</summary>
public static class MauiTestAgentProtocolVersions
{
    public const int Schema = 1;
    public const string PolicyVersion = "test-agent-policy-v1";
}

/// <summary>Known semantic actions that the restricted protocol can represent.</summary>
public static class MauiTestAgentActions
{
    public const string Tap = "tap";
    public const string Fill = "fill";
    public const string Scroll = "scroll";
    public const string Navigate = "navigate";
    public const string Back = "back";
    public const string Assert = "assert";
    public const string Run = "run";
    public const string Cancel = "cancel";
    public const string AuthorCommit = "author-commit";
    public const string DraftAppend = "draft-append";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Tap, Fill, Scroll, Navigate, Back, Assert, Run, Cancel, AuthorCommit, DraftAppend,
    };
}

/// <summary>Known human-review purposes for a restricted test-agent mutation request.</summary>
public static class MauiTestAgentApprovalKinds
{
    public const string Exploration = "exploration";
    public const string DraftChange = "draft-change";
    public const string Assertion = "assertion";
    public const string Commit = "commit";
    public const string Run = "run";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Exploration, DraftChange, Assertion, Commit, Run,
    };
}

/// <summary>Stable states for a broker-owned human approval request.</summary>
public static class MauiTestAgentApprovalStates
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Expired = "expired";
    public const string Stale = "stale";
    public const string Consumed = "consumed";
}

/// <summary>Stable error categories returned by the restricted test-agent protocol.</summary>
public static class MauiTestAgentErrorCategories
{
    public const string Validation = "validation";
    public const string Authorization = "authorization";
    public const string Target = "target";
    public const string State = "state";
    public const string Conflict = "conflict";
    public const string Capability = "capability";
    public const string Transport = "transport";
    public const string UnknownCompletion = "unknown-completion";
    public const string Unsupported = "unsupported";
    public const string Internal = "internal";
}

/// <summary>Stable machine-readable error codes returned by the restricted protocol.</summary>
public static class MauiTestAgentErrorCodes
{
    public const string InvalidRequest = "invalid-request";
    public const string ExplicitTargetRequired = "explicit-target-required";
    public const string TargetStale = "target-stale";
    public const string TargetUnavailable = "target-unavailable";
    public const string SessionNotFound = "authoring-session-not-found";
    public const string SessionExpired = "authoring-session-expired";
    public const string SessionAbandoned = "authoring-session-abandoned";
    public const string ReadCapabilityRequired = "read-capability-required";
    public const string MutationGrantRequired = "mutation-grant-required";
    public const string MutationGrantExpired = "mutation-grant-expired";
    public const string MutationGrantReused = "mutation-grant-reused";
    public const string MutationGrantScopeDenied = "mutation-grant-scope-denied";
    public const string MutationGrantStale = "mutation-grant-stale";
    public const string HumanApprovalRequired = "human-approval-required";
    public const string ApprovalRequestNotFound = "approval-request-not-found";
    public const string ApprovalRequestExpired = "approval-request-expired";
    public const string ApprovalRequestDecided = "approval-request-decided";
    public const string ApprovalRequestScopeDenied = "approval-request-scope-denied";
    public const string IdempotencyReused = "idempotency-reused";
    public const string DeadlineExpired = "deadline-expired";
    public const string ValueLimitExceeded = "value-limit-exceeded";
    public const string UntrustedPolicyInput = "untrusted-policy-input";
    public const string UnknownCompletion = "unknown-completion";
    public const string UnsupportedOperation = "unsupported-operation";
    public const string PatchApplyForbidden = "patch-apply-forbidden";
}

/// <summary>Identity of the exact connected process that a request may target.</summary>
public sealed class MauiTestAgentTarget
{
    [JsonPropertyName("agentId")] public string? AgentId { get; set; }
    [JsonPropertyName("agentInstanceId")] public string? AgentInstanceId { get; set; }
    [JsonPropertyName("appBuildFingerprint")] public string? AppBuildFingerprint { get; set; }
    [JsonPropertyName("seedFingerprint")] public string? SeedFingerprint { get; set; }
    [JsonPropertyName("backendStateFingerprint")] public string? BackendStateFingerprint { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Revision and run facts which bind an authoring or execution request to its subject.</summary>
public sealed class MauiTestAgentCorrelation
{
    [JsonPropertyName("authoringSessionId")] public string? AuthoringSessionId { get; set; }
    [JsonPropertyName("planId")] public string? PlanId { get; set; }
    [JsonPropertyName("planRevision")] public int? PlanRevision { get; set; }
    [JsonPropertyName("planDigest")] public string? PlanDigest { get; set; }
    [JsonPropertyName("flowId")] public string? FlowId { get; set; }
    [JsonPropertyName("flowRevision")] public int? FlowRevision { get; set; }
    [JsonPropertyName("flowDigest")] public string? FlowDigest { get; set; }
    [JsonPropertyName("runId")] public string? RunId { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Envelope carried by every restricted test-agent request. The intent is user-visible audit
/// metadata, never hidden model reasoning or a policy instruction.
/// </summary>
public sealed class MauiTestAgentRequestEnvelope
{
    [JsonPropertyName("schema")] public int Schema { get; set; } = MauiTestAgentProtocolVersions.Schema;
    [JsonPropertyName("requestId")] public string? RequestId { get; set; }
    [JsonPropertyName("idempotencyKey")] public string? IdempotencyKey { get; set; }
    [JsonPropertyName("target")] public MauiTestAgentTarget? Target { get; set; }
    [JsonPropertyName("correlation")] public MauiTestAgentCorrelation? Correlation { get; set; }
    [JsonPropertyName("provenance")] public MauiActorProvenance? Provenance { get; set; }
    [JsonPropertyName("intent")] public string? Intent { get; set; }
    [JsonPropertyName("approvalGrantId")] public string? ApprovalGrantId { get; set; }
    [JsonPropertyName("readCapabilityId")] public string? ReadCapabilityId { get; set; }
    [JsonPropertyName("deadlineMs")] public int? DeadlineMs { get; set; }
    [JsonPropertyName("policyVersion")] public string? PolicyVersion { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Broker-observed target state compared against a session or grant before dispatch.</summary>
public sealed class MauiTestAgentTargetState
{
    [JsonPropertyName("agentId")] public string? AgentId { get; set; }
    [JsonPropertyName("agentInstanceId")] public string? AgentInstanceId { get; set; }
    [JsonPropertyName("appBuildFingerprint")] public string? AppBuildFingerprint { get; set; }
    [JsonPropertyName("seedFingerprint")] public string? SeedFingerprint { get; set; }
    [JsonPropertyName("backendStateFingerprint")] public string? BackendStateFingerprint { get; set; }
    [JsonPropertyName("route")] public string? Route { get; set; }
    [JsonPropertyName("window")] public string? Window { get; set; }
    [JsonPropertyName("observedAt")] public DateTimeOffset? ObservedAt { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Allowed scope bound into an opaque, human-issued mutation grant.</summary>
public sealed class MauiTestAgentMutationScope
{
    [JsonPropertyName("allowedActions")] public List<string> AllowedActions { get; set; } = [];
    [JsonConverter(typeof(MauiTestAgentSelectorScopeListConverter))]
    [JsonPropertyName("allowedSelectors")] public List<string> AllowedSelectors { get; set; } = [];
    [JsonPropertyName("allowedRoutes")] public List<string> AllowedRoutes { get; set; } = [];
    [JsonPropertyName("allowedSideEffectClasses")] public List<string> AllowedSideEffectClasses { get; set; } = [];
    [JsonPropertyName("maxActionCount")] public int? MaxActionCount { get; set; }
    [JsonPropertyName("maxValueBytes")] public int? MaxValueBytes { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Evidence that a host or Workbench performed an explicit human approval.</summary>
public sealed class MauiTestAgentHumanApproval
{
    [JsonPropertyName("approved")] public bool Approved { get; set; }
    [JsonPropertyName("actor")] public MauiActorProvenance? Actor { get; set; }
    [JsonPropertyName("approvalChannel")] public string? ApprovalChannel { get; set; }
    [JsonPropertyName("approvedAt")] public DateTimeOffset? ApprovedAt { get; set; }
    [JsonPropertyName("approvalDigest")] public string? ApprovalDigest { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Request made by a human-owned host to issue an opaque mutation grant.</summary>
public sealed class MauiTestAgentGrantIssueRequest
{
    [JsonPropertyName("sessionId")] public string? SessionId { get; set; }
    [JsonPropertyName("readCapabilityId")] public string? ReadCapabilityId { get; set; }
    [JsonPropertyName("targetState")] public MauiTestAgentTargetState? TargetState { get; set; }
    [JsonPropertyName("correlation")] public MauiTestAgentCorrelation? Correlation { get; set; }
    [JsonPropertyName("scope")] public MauiTestAgentMutationScope? Scope { get; set; }
    [JsonPropertyName("approval")] public MauiTestAgentHumanApproval? Approval { get; set; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset? ExpiresAt { get; set; }
    [JsonPropertyName("policyVersion")] public string? PolicyVersion { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Safe result of a grant issue request. GrantId is opaque and never appears in audit data.</summary>
public sealed class MauiTestAgentGrantIssueResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("grantId")] public string? GrantId { get; set; }
    [JsonPropertyName("grantDigest")] public string? GrantDigest { get; set; }
    [JsonPropertyName("remainingActions")] public int? RemainingActions { get; set; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset? ExpiresAt { get; set; }
    [JsonPropertyName("error")] public MauiTestAgentError? Error { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Agent submission for a broker-owned, human-reviewable mutation request. The request can ask
/// only for an explicit bounded scope; it cannot contain or fabricate human approval.
/// </summary>
public sealed class MauiTestAgentApprovalSubmitRequest
{
    [JsonPropertyName("envelope")] public MauiTestAgentRequestEnvelope? Envelope { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("scope")] public MauiTestAgentMutationScope? Scope { get; set; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset? ExpiresAt { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Safe broker projection of a human approval request. GrantId is returned only through the
/// authoring session's read capability after approval; Workbench list responses omit it.
/// </summary>
public sealed class MauiTestAgentApprovalRecord
{
    [JsonPropertyName("schema")] public int Schema { get; set; } = MauiTestAgentProtocolVersions.Schema;
    [JsonPropertyName("approvalRequestId")] public string? ApprovalRequestId { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("sessionId")] public string? SessionId { get; set; }
    [JsonPropertyName("intent")] public string? Intent { get; set; }
    [JsonPropertyName("provenance")] public MauiActorProvenance? Provenance { get; set; }
    [JsonPropertyName("target")] public MauiTestAgentTarget? Target { get; set; }
    [JsonPropertyName("targetState")] public MauiTestAgentTargetState? TargetState { get; set; }
    [JsonPropertyName("correlation")] public MauiTestAgentCorrelation? Correlation { get; set; }
    [JsonPropertyName("requestedScope")] public MauiTestAgentMutationScope? RequestedScope { get; set; }
    [JsonPropertyName("approvedScope")] public MauiTestAgentMutationScope? ApprovedScope { get; set; }
    [JsonPropertyName("createdAt")] public DateTimeOffset? CreatedAt { get; set; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset? ExpiresAt { get; set; }
    [JsonPropertyName("decidedAt")] public DateTimeOffset? DecidedAt { get; set; }
    [JsonPropertyName("reasonCode")] public string? ReasonCode { get; set; }
    [JsonPropertyName("grantId")] public string? GrantId { get; set; }
    [JsonPropertyName("grantExpiresAt")] public DateTimeOffset? GrantExpiresAt { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Structured result for creating or reading a broker-owned approval request.</summary>
public sealed class MauiTestAgentApprovalResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("request")] public MauiTestAgentApprovalRecord? Request { get; set; }
    [JsonPropertyName("error")] public MauiTestAgentError? Error { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Starts a bounded broker-owned authoring session.</summary>
public sealed class MauiTestAgentSessionBeginRequest
{
    [JsonPropertyName("envelope")] public MauiTestAgentRequestEnvelope? Envelope { get; set; }
    [JsonPropertyName("targetState")] public MauiTestAgentTargetState? TargetState { get; set; }
    [JsonPropertyName("plan")] public MauiTestPlan? Plan { get; set; }
    [JsonPropertyName("flow")] public MauiFlow? Flow { get; set; }
    [JsonPropertyName("durationSeconds")] public int? DurationSeconds { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Unified typed request for begin, status, commit, abandon, and migration-preview authoring operations.</summary>
public sealed class MauiTestAgentAuthorRequest
{
    [JsonPropertyName("envelope")] public MauiTestAgentRequestEnvelope? Envelope { get; set; }
    [JsonPropertyName("plan")] public MauiTestPlan? Plan { get; set; }
    [JsonPropertyName("flow")] public MauiFlow? Flow { get; set; }
    [JsonPropertyName("targetState")] public MauiTestAgentTargetState? TargetState { get; set; }
    [JsonPropertyName("durationSeconds")] public int? DurationSeconds { get; set; }
    [JsonPropertyName("explorationScope")] public MauiTestAgentMutationScope? ExplorationScope { get; set; }
    [JsonPropertyName("approvalKind")] public string? ApprovalKind { get; set; }
    [JsonPropertyName("approvalScope")] public MauiTestAgentMutationScope? ApprovalScope { get; set; }
    [JsonPropertyName("approvalExpiresAt")] public DateTimeOffset? ApprovalExpiresAt { get; set; }
    [JsonPropertyName("approvalRequestId")] public string? ApprovalRequestId { get; set; }
    [JsonPropertyName("waitTimeoutSeconds")] public int? WaitTimeoutSeconds { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Read/abandon request for a bounded authoring session.</summary>
public sealed class MauiTestAgentSessionAccessRequest
{
    [JsonPropertyName("sessionId")] public string? SessionId { get; set; }
    [JsonPropertyName("readCapabilityId")] public string? ReadCapabilityId { get; set; }
    [JsonPropertyName("authorizationId")] public string? AuthorizationId { get; set; }
    [JsonPropertyName("envelope")] public MauiTestAgentRequestEnvelope? Envelope { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Mutable draft state that remains broker-owned until a human-approved commit.</summary>
public sealed class MauiTestAgentAuthoringSnapshot
{
    [JsonPropertyName("schema")] public int Schema { get; set; } = MauiTestAgentProtocolVersions.Schema;
    [JsonPropertyName("sessionId")] public string? SessionId { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset? ExpiresAt { get; set; }
    [JsonPropertyName("target")] public MauiTestAgentTarget? Target { get; set; }
    [JsonPropertyName("targetState")] public MauiTestAgentTargetState? TargetState { get; set; }
    [JsonPropertyName("plan")] public MauiTestPlan? Plan { get; set; }
    [JsonPropertyName("planDigest")] public string? PlanDigest { get; set; }
    [JsonPropertyName("flow")] public MauiFlow? Flow { get; set; }
    [JsonPropertyName("flowDigest")] public string? FlowDigest { get; set; }
    [JsonPropertyName("flowRevision")] public int? FlowRevision { get; set; }
    [JsonPropertyName("committedAt")] public DateTimeOffset? CommittedAt { get; set; }
    [JsonPropertyName("approvalRequests")] public List<MauiTestAgentApprovalRecord> ApprovalRequests { get; set; } = [];
    [JsonPropertyName("readCapabilityId")] public string? ReadCapabilityId { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Structured begin/status/abandon result for an authoring session.</summary>
public sealed class MauiTestAgentSessionResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("snapshot")] public MauiTestAgentAuthoringSnapshot? Snapshot { get; set; }
    [JsonPropertyName("error")] public MauiTestAgentError? Error { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Request to atomically validate and consume a mutation grant before dispatch.</summary>
public sealed class MauiTestAgentMutationAuthorizationRequest
{
    [JsonPropertyName("envelope")] public MauiTestAgentRequestEnvelope? Envelope { get; set; }
    [JsonPropertyName("action")] public string? Action { get; set; }
    [JsonPropertyName("selector")] public FlowSelector? Selector { get; set; }
    [JsonPropertyName("route")] public string? Route { get; set; }
    [JsonPropertyName("sideEffectClass")] public string? SideEffectClass { get; set; }
    [JsonPropertyName("valueLength")] public int? ValueLength { get; set; }
    [JsonPropertyName("valueDigest")] public string? ValueDigest { get; set; }
    [JsonPropertyName("currentTargetState")] public MauiTestAgentTargetState? CurrentTargetState { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Result of grant validation. A false DispatchAllowed result must never be dispatched.</summary>
public sealed class MauiTestAgentMutationAuthorizationResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("dispatchAllowed")] public bool DispatchAllowed { get; set; }
    [JsonPropertyName("authorizationId")] public string? AuthorizationId { get; set; }
    [JsonPropertyName("remainingActions")] public int? RemainingActions { get; set; }
    [JsonPropertyName("grantDigest")] public string? GrantDigest { get; set; }
    [JsonPropertyName("error")] public MauiTestAgentError? Error { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Completion of an already-authorized request, represented only by bounded digests.</summary>
public sealed class MauiTestAgentMutationCompletion
{
    [JsonPropertyName("authorizationId")] public string? AuthorizationId { get; set; }
    [JsonPropertyName("outcome")] public string? Outcome { get; set; }
    [JsonPropertyName("actionDigest")] public string? ActionDigest { get; set; }
    [JsonPropertyName("resultDigest")] public string? ResultDigest { get; set; }
    [JsonPropertyName("runId")] public string? RunId { get; set; }
    [JsonPropertyName("errorCode")] public string? ErrorCode { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Structured, provider-neutral semantic action used by the restricted MCP tool.</summary>
public sealed class MauiTestAgentActionRequest
{
    [JsonPropertyName("envelope")] public MauiTestAgentRequestEnvelope? Envelope { get; set; }
    [JsonPropertyName("authorizationId")] public string? AuthorizationId { get; set; }
    [JsonPropertyName("action")] public string? Action { get; set; }
    [JsonPropertyName("selector")] public FlowSelector? Selector { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("route")] public string? Route { get; set; }
    [JsonPropertyName("deltaX")] public double? DeltaX { get; set; }
    [JsonPropertyName("deltaY")] public double? DeltaY { get; set; }
    [JsonPropertyName("itemIndex")] public int? ItemIndex { get; set; }
    [JsonPropertyName("appendDraft")] public bool AppendDraft { get; set; }
    [JsonPropertyName("execute")] public bool Execute { get; set; } = true;
    [JsonPropertyName("sideEffectClass")] public string? SideEffectClass { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Typed assertion add/verify request used by the restricted MCP protocol.</summary>
public sealed class MauiTestAgentAssertionRequest
{
    [JsonPropertyName("envelope")] public MauiTestAgentRequestEnvelope? Envelope { get; set; }
    [JsonPropertyName("authorizationId")] public string? AuthorizationId { get; set; }
    [JsonPropertyName("operation")] public string? Operation { get; set; }
    [JsonPropertyName("assertion")] public FlowAssert? Assertion { get; set; }
    [JsonPropertyName("stepSequence")] public int? StepSequence { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Static or live validation request used by the restricted MCP protocol.</summary>
public sealed class MauiTestAgentValidationRequest
{
    [JsonPropertyName("envelope")] public MauiTestAgentRequestEnvelope? Envelope { get; set; }
    [JsonPropertyName("mode")] public string? Mode { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Start, status, or cancellation request for a broker-owned test run.</summary>
public sealed class MauiTestAgentRunRequest
{
    [JsonPropertyName("envelope")] public MauiTestAgentRequestEnvelope? Envelope { get; set; }
    [JsonPropertyName("operation")] public string? Operation { get; set; }
    [JsonPropertyName("timeoutMs")] public int? TimeoutMs { get; set; }
    [JsonPropertyName("runCapabilityToken")] public string? RunCapabilityToken { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Read-only safe trace request.</summary>
public sealed class MauiTestAgentTraceRequest
{
    [JsonPropertyName("envelope")] public MauiTestAgentRequestEnvelope? Envelope { get; set; }
    [JsonPropertyName("runId")] public string? RunId { get; set; }
    [JsonPropertyName("runCapabilityToken")] public string? RunCapabilityToken { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Broker-owned binding between an authoring session and a capability-scoped workflow run.</summary>
public sealed class MauiTestAgentRunBindingRequest
{
    [JsonPropertyName("sessionId")] public string? SessionId { get; set; }
    [JsonPropertyName("readCapabilityId")] public string? ReadCapabilityId { get; set; }
    [JsonPropertyName("envelope")] public MauiTestAgentRequestEnvelope? Envelope { get; set; }
    [JsonPropertyName("runId")] public string? RunId { get; set; }
    [JsonPropertyName("runCapabilityToken")] public string? RunCapabilityToken { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Safe lookup result for a broker-owned workflow run capability.</summary>
public sealed class MauiTestAgentRunBindingResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("runId")] public string? RunId { get; set; }
    [JsonPropertyName("runCapabilityToken")] public string? RunCapabilityToken { get; set; }
    [JsonPropertyName("error")] public MauiTestAgentError? Error { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Inert proposal, preview, or rejection request. Apply, approval, and rollback are intentionally absent.</summary>
public sealed class MauiTestAgentPatchRequest
{
    [JsonPropertyName("envelope")] public MauiTestAgentRequestEnvelope? Envelope { get; set; }
    [JsonPropertyName("operation")] public string? Operation { get; set; }
    [JsonPropertyName("proposal")] public MauiFlowRepairProposal? Proposal { get; set; }
    [JsonPropertyName("proposalId")] public string? ProposalId { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Safe record of an inert patch proposal retained by an authoring session.</summary>
public sealed class MauiTestAgentPatchRecord
{
    [JsonPropertyName("proposalId")] public string? ProposalId { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("proposal")] public MauiFlowRepairProposal? Proposal { get; set; }
    [JsonPropertyName("reasonDigest")] public string? ReasonDigest { get; set; }
    [JsonPropertyName("recordedAt")] public DateTimeOffset? RecordedAt { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Structured result for an inert patch proposal operation.</summary>
public sealed class MauiTestAgentPatchResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("record")] public MauiTestAgentPatchRecord? Record { get; set; }
    [JsonPropertyName("records")] public List<MauiTestAgentPatchRecord> Records { get; set; } = [];
    [JsonPropertyName("error")] public MauiTestAgentError? Error { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Bounded safe audit projection for a single authoring session.</summary>
public sealed class MauiTestAgentAuditResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("entries")] public List<MauiTestAgentAuditEntry> Entries { get; set; } = [];
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }
    [JsonPropertyName("error")] public MauiTestAgentError? Error { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Typed protocol error with retryability and safe artifact references.</summary>
public sealed class MauiTestAgentError
{
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("retryable")] public bool Retryable { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("artifactRefs")] public List<MauiFlowArtifactReference> ArtifactRefs { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Explicit label for bounded data that must not influence authorization or policy scope.</summary>
public sealed class MauiTestAgentUntrustedInput
{
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("digest")] public string? Digest { get; set; }
    [JsonPropertyName("truncated")] public bool? Truncated { get; set; }
    [JsonPropertyName("policyInfluencing")] public bool PolicyInfluencing { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Bounded append-only audit entry. It deliberately contains only IDs and digests.</summary>
public sealed class MauiTestAgentAuditEntry
{
    [JsonPropertyName("sequence")] public long Sequence { get; set; }
    [JsonPropertyName("at")] public DateTimeOffset At { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("requestId")] public string? RequestId { get; set; }
    [JsonPropertyName("sessionId")] public string? SessionId { get; set; }
    [JsonPropertyName("agentId")] public string? AgentId { get; set; }
    [JsonPropertyName("agentInstanceId")] public string? AgentInstanceId { get; set; }
    [JsonPropertyName("planRevision")] public int? PlanRevision { get; set; }
    [JsonPropertyName("flowRevision")] public int? FlowRevision { get; set; }
    [JsonPropertyName("runId")] public string? RunId { get; set; }
    [JsonPropertyName("policyDecision")] public string? PolicyDecision { get; set; }
    [JsonPropertyName("intentDigest")] public string? IntentDigest { get; set; }
    [JsonPropertyName("grantDigest")] public string? GrantDigest { get; set; }
    [JsonPropertyName("actionDigest")] public string? ActionDigest { get; set; }
    [JsonPropertyName("resultDigest")] public string? ResultDigest { get; set; }
    [JsonPropertyName("errorCode")] public string? ErrorCode { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Common structured result shape returned by restricted MCP tools.</summary>
public sealed class MauiTestAgentToolResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("requestId")] public string? RequestId { get; set; }
    [JsonPropertyName("error")] public MauiTestAgentError? Error { get; set; }
    [JsonPropertyName("untrustedInputs")] public List<MauiTestAgentUntrustedInput> UntrustedInputs { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
