using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.Maui.DevFlow.Testing;

/// <summary>
/// A bounded, provider-neutral account of one flow run. It contains observed facts only and is
/// safe to persist or attach to a redacted evidence bundle.
/// </summary>
public sealed class MauiFlowRunReport
{
    [JsonPropertyName("schema")] public int Schema { get; set; } = 1;
    [JsonPropertyName("runId")] public string? RunId { get; set; }
    [JsonPropertyName("flowId")] public string? FlowId { get; set; }
    [JsonPropertyName("flowRevision")] public int? FlowRevision { get; set; }
    [JsonPropertyName("flowDigest")] public string? FlowDigest { get; set; }
    [JsonPropertyName("legacyFlowIdentity")] public string? LegacyFlowIdentity { get; set; }
    [JsonPropertyName("target")] public MauiFlowRunTarget? Target { get; set; }
    [JsonPropertyName("reset")] public MauiFlowResetResult? Reset { get; set; }
    [JsonPropertyName("preconditions")] public MauiFlowReplayPreconditions? Preconditions { get; set; }
    [JsonPropertyName("sideEffectPolicy")] public string? SideEffectPolicy { get; set; }
    [JsonPropertyName("compensator")] public MauiFlowCompensatorOutcome? Compensator { get; set; }
    [JsonPropertyName("businessOracles")] public List<MauiIndependentBusinessOracleResult> BusinessOracles { get; set; } = [];
    [JsonPropertyName("replayEligibility")] public MauiFlowReplayEligibilityDecision? ReplayEligibility { get; set; }
    [JsonPropertyName("verification")] public MauiFlowRunVerification? Verification { get; set; }
    [JsonPropertyName("startedAt")] public DateTimeOffset? StartedAt { get; set; }
    [JsonPropertyName("endedAt")] public DateTimeOffset? EndedAt { get; set; }
    [JsonPropertyName("outcome")] public MauiFlowRunOutcome? Outcome { get; set; }
    [JsonPropertyName("divergenceStepId")] public string? DivergenceStepId { get; set; }
    [JsonPropertyName("events")] public List<MauiFlowRunEvent> Events { get; set; } = [];
    [JsonPropertyName("steps")] public List<MauiFlowStepAttempt> Steps { get; set; } = [];
    [JsonPropertyName("failure")] public MauiFlowFailure? Failure { get; set; }
    [JsonPropertyName("artifacts")] public List<MauiFlowArtifactReference> Artifacts { get; set; } = [];
    [JsonPropertyName("selectorHealth")] public MauiFlowSelectorHealthSummary? SelectorHealth { get; set; }
    [JsonPropertyName("reportDigest")] public string? ReportDigest { get; set; }
    [JsonPropertyName("reportPath")] public string? ReportPath { get; set; }
    [JsonPropertyName("truncated")] public bool? Truncated { get; set; }
    [JsonPropertyName("truncationReason")] public string? TruncationReason { get; set; }
    [JsonPropertyName("omissions")] public List<MauiFlowReportOmission> Omissions { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>The selected application, device, agent, and display facts for a run.</summary>
public sealed class MauiFlowRunTarget
{
    [JsonPropertyName("targetId")] public string? TargetId { get; set; }
    [JsonPropertyName("platform")] public string? Platform { get; set; }
    [JsonPropertyName("deviceId")] public string? DeviceId { get; set; }
    [JsonPropertyName("deviceProfile")] public string? DeviceProfile { get; set; }
    [JsonPropertyName("appId")] public string? AppId { get; set; }
    [JsonPropertyName("appBuildFingerprint")] public string? AppBuildFingerprint { get; set; }
    [JsonPropertyName("appSourceFingerprint")] public string? AppSourceFingerprint { get; set; }
    [JsonPropertyName("packageDigest")] public string? PackageDigest { get; set; }
    [JsonPropertyName("agentId")] public string? AgentId { get; set; }
    [JsonPropertyName("agentInstanceId")] public string? AgentInstanceId { get; set; }
    [JsonPropertyName("locale")] public string? Locale { get; set; }
    [JsonPropertyName("theme")] public string? Theme { get; set; }
    [JsonPropertyName("orientation")] public string? Orientation { get; set; }
    [JsonPropertyName("displayProfile")] public string? DisplayProfile { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Observed reset and backend/test-data state for a run.</summary>
public sealed class MauiFlowResetResult
{
    [JsonPropertyName("requested")] public bool? Requested { get; set; }
    [JsonPropertyName("succeeded")] public bool? Succeeded { get; set; }
    [JsonPropertyName("appStateSucceeded")] public bool? AppStateSucceeded { get; set; }
    [JsonPropertyName("backendTestDataSucceeded")] public bool? BackendTestDataSucceeded { get; set; }
    [JsonPropertyName("strategy")] public string? Strategy { get; set; }
    [JsonPropertyName("resetIdentity")] public string? ResetIdentity { get; set; }
    [JsonPropertyName("seedFingerprint")] public string? SeedFingerprint { get; set; }
    [JsonPropertyName("backendStateFingerprint")] public string? BackendStateFingerprint { get; set; }
    [JsonPropertyName("reference")] public MauiFlowResetReference? Reference { get; set; }
    [JsonPropertyName("appStateSeed")] public MauiFlowAppStateSeedFingerprint? AppStateSeed { get; set; }
    [JsonPropertyName("backendTestDataSeed")] public MauiFlowBackendTestDataSeedFingerprint? BackendTestDataSeed { get; set; }
    [JsonPropertyName("outcome")] public MauiFlowResetOutcome? Outcome { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>The terminal or in-progress state reported for a flow run.</summary>
public sealed class MauiFlowRunOutcome
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("summary")] public string? Summary { get; set; }
    [JsonPropertyName("terminal")] public bool? Terminal { get; set; }
    [JsonPropertyName("verified")] public bool? Verified { get; set; }
    [JsonPropertyName("verificationReason")] public string? VerificationReason { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Records whether a terminal run can be marked verified independently of whether its replay
/// completed. A passing UI replay without its required business oracle is deliberately not
/// verified.
/// </summary>
public sealed class MauiFlowRunVerification
{
    [JsonPropertyName("verified")] public bool? Verified { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("checkedAt")] public DateTimeOffset? CheckedAt { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Known run outcome values.</summary>
public static class MauiFlowRunOutcomes
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string TimedOut = "timed-out";
    public const string LeaseLost = "lease-lost";
    public const string InfrastructureError = "infrastructure-error";
    public const string UnknownCompletion = "unknown-completion";
    public const string Orphaned = "orphaned";
}

/// <summary>An ordered, bounded event emitted while a run was prepared or executed.</summary>
public sealed class MauiFlowRunEvent
{
    [JsonPropertyName("sequence")] public int? Sequence { get; set; }
    [JsonPropertyName("at")] public DateTimeOffset? At { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("stepId")] public string? StepId { get; set; }
    [JsonPropertyName("data")] public JsonElement? Data { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>The observed attempt for one flow step.</summary>
public sealed class MauiFlowStepAttempt
{
    [JsonPropertyName("stepId")] public string? StepId { get; set; }
    [JsonPropertyName("sequence")] public int? Sequence { get; set; }
    [JsonPropertyName("action")] public string? Action { get; set; }
    [JsonPropertyName("intent")] public string? Intent { get; set; }
    [JsonPropertyName("startedAt")] public DateTimeOffset? StartedAt { get; set; }
    [JsonPropertyName("endedAt")] public DateTimeOffset? EndedAt { get; set; }
    [JsonPropertyName("durationMs")] public long? DurationMs { get; set; }
    [JsonPropertyName("selector")] public FlowSelector? Selector { get; set; }
    [JsonPropertyName("selectorRequest")] public MauiFlowSelectorRequest? SelectorRequest { get; set; }
    [JsonPropertyName("candidateCount")] public int? CandidateCount { get; set; }
    [JsonPropertyName("candidateSummary")] public MauiFlowCandidateSummary? CandidateSummary { get; set; }
    [JsonPropertyName("targetResolution")] public MauiFlowTargetResolution? TargetResolution { get; set; }
    [JsonPropertyName("actionability")] public List<MauiFlowActionabilityAttempt> Actionability { get; set; } = [];
    [JsonPropertyName("dispatch")] public MauiFlowDispatchReceipt? Dispatch { get; set; }
    [JsonPropertyName("assertions")] public List<MauiFlowAssertionResult> Assertions { get; set; } = [];
    [JsonPropertyName("expectedCheckpoint")] public MauiFlowCheckpoint? ExpectedCheckpoint { get; set; }
    [JsonPropertyName("observedCheckpoint")] public MauiFlowCheckpoint? ObservedCheckpoint { get; set; }
    [JsonPropertyName("seedFingerprint")] public string? SeedFingerprint { get; set; }
    [JsonPropertyName("backendStateFingerprint")] public string? BackendStateFingerprint { get; set; }
    [JsonPropertyName("commandId")] public string? CommandId { get; set; }
    [JsonPropertyName("commandSequence")] public long? CommandSequence { get; set; }
    [JsonPropertyName("actionDigest")] public string? ActionDigest { get; set; }
    [JsonPropertyName("authorityEpoch")] public long? AuthorityEpoch { get; set; }
    [JsonPropertyName("acknowledgementState")] public string? AcknowledgementState { get; set; }
    [JsonPropertyName("completionCertainty")] public string? CompletionCertainty { get; set; }
    [JsonPropertyName("failureClass")] public string? FailureClass { get; set; }
    [JsonPropertyName("fingerprint")] public MauiElementFingerprint? Fingerprint { get; set; }
    [JsonPropertyName("selectorCandidates")] public List<MauiSelectorCandidate> SelectorCandidates { get; set; } = [];
    [JsonPropertyName("selectorCandidateOmissions")] public List<MauiSelectorEvidenceOmission> SelectorCandidateOmissions { get; set; } = [];
    [JsonPropertyName("artifacts")] public List<MauiFlowArtifactReference> Artifacts { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Bounded summary of value-free selector evidence captured during a run.</summary>
public sealed class MauiFlowSelectorHealthSummary
{
    [JsonPropertyName("ruleVersion")] public string RuleVersion { get; set; } = MauiSelectorHealthRules.RuleVersion;
    [JsonPropertyName("rankerRuleVersion")] public string RankerRuleVersion { get; set; } = MauiSelectorHealthRules.RankerRuleVersion;
    [JsonPropertyName("calibrationState")] public string CalibrationState { get; set; } = MauiSelectorHealthRules.Uncalibrated;
    [JsonPropertyName("capturedSteps")] public int CapturedSteps { get; set; }
    [JsonPropertyName("candidateCount")] public int CandidateCount { get; set; }
    [JsonPropertyName("omissionCount")] public int OmissionCount { get; set; }
}

/// <summary>A selector request projected without retaining sensitive typed text.</summary>
public sealed class MauiFlowSelectorRequest
{
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("value")] public MauiFlowValueDisclosure? Value { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>A privacy-safe candidate and final-target summary.</summary>
public sealed class MauiFlowCandidateSummary
{
    [JsonPropertyName("count")] public int? Count { get; set; }
    [JsonPropertyName("types")] public List<string> Types { get; set; } = [];
    [JsonPropertyName("final")] public string? Final { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>The result of resolving a selector for a specific step attempt.</summary>
public sealed class MauiFlowTargetResolution
{
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("matchCount")] public int? MatchCount { get; set; }
    [JsonPropertyName("elementId")] public string? ElementId { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("finalResolution")] public string? FinalResolution { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>One capability-honest actionability poll or check before dispatch.</summary>
public sealed class MauiFlowActionabilityAttempt
{
    [JsonPropertyName("sequence")] public int? Sequence { get; set; }
    [JsonPropertyName("attempt")] public int? Attempt { get; set; }
    [JsonPropertyName("at")] public DateTimeOffset? At { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("passed")] public bool? Passed { get; set; }
    [JsonPropertyName("resolved")] public bool? Resolved { get; set; }
    [JsonPropertyName("visible")] public bool? Visible { get; set; }
    [JsonPropertyName("enabled")] public bool? Enabled { get; set; }
    [JsonPropertyName("hasBounds")] public bool? HasBounds { get; set; }
    [JsonPropertyName("boundsStable")] public bool? BoundsStable { get; set; }
    [JsonPropertyName("waitDurationMs")] public long? WaitDurationMs { get; set; }
    [JsonPropertyName("outcome")] public string? Outcome { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>A command receipt and completion certainty captured for a mutation.</summary>
public sealed class MauiFlowDispatchReceipt
{
    [JsonPropertyName("commandId")] public string? CommandId { get; set; }
    [JsonPropertyName("sequence")] public long? Sequence { get; set; }
    [JsonPropertyName("actionDigest")] public string? ActionDigest { get; set; }
    [JsonPropertyName("authorityEpoch")] public long? AuthorityEpoch { get; set; }
    [JsonPropertyName("acknowledgementState")] public string? AcknowledgementState { get; set; }
    [JsonPropertyName("completionCertainty")] public string? CompletionCertainty { get; set; }
    [JsonPropertyName("receivedAt")] public DateTimeOffset? ReceivedAt { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>The observed result of a flow assertion.</summary>
public sealed class MauiFlowAssertionResult
{
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("passed")] public bool? Passed { get; set; }
    [JsonPropertyName("skipped")] public bool? Skipped { get; set; }
    [JsonPropertyName("expected")] public string? Expected { get; set; }
    [JsonPropertyName("actual")] public string? Actual { get; set; }
    [JsonPropertyName("expectedDisclosure")] public MauiFlowValueDisclosure? ExpectedDisclosure { get; set; }
    [JsonPropertyName("actualDisclosure")] public MauiFlowValueDisclosure? ActualDisclosure { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>How a value was represented in a report without storing sensitive typed text.</summary>
public sealed class MauiFlowValueDisclosure
{
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("length")] public int? Length { get; set; }
    [JsonPropertyName("digest")] public string? Digest { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Checkpoint facts used to distinguish locator drift from state drift.</summary>
public sealed class MauiFlowCheckpoint
{
    [JsonPropertyName("appBuildFingerprint")] public string? AppBuildFingerprint { get; set; }
    [JsonPropertyName("agentInstanceId")] public string? AgentInstanceId { get; set; }
    [JsonPropertyName("seedFingerprint")] public string? SeedFingerprint { get; set; }
    [JsonPropertyName("backendStateFingerprint")] public string? BackendStateFingerprint { get; set; }
    [JsonPropertyName("route")] public string? Route { get; set; }
    [JsonPropertyName("window")] public string? Window { get; set; }
    [JsonPropertyName("modal")] public string? Modal { get; set; }
    [JsonPropertyName("locale")] public string? Locale { get; set; }
    [JsonPropertyName("theme")] public string? Theme { get; set; }
    [JsonPropertyName("orientation")] public string? Orientation { get; set; }
    [JsonPropertyName("displayProfile")] public string? DisplayProfile { get; set; }
    [JsonPropertyName("collectionItemKey")] public string? CollectionItemKey { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>A deterministic primary failure finding for the run or a step attempt.</summary>
public sealed class MauiFlowFailure
{
    [JsonPropertyName("failureId")] public string? FailureId { get; set; }
    [JsonPropertyName("class")] public string? Class { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("phase")] public string? Phase { get; set; }
    [JsonPropertyName("retryable")] public bool? Retryable { get; set; }
    [JsonPropertyName("repairEligible")] public bool? RepairEligible { get; set; }
    [JsonPropertyName("legacyKind")] public string? LegacyKind { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("stepId")] public string? StepId { get; set; }
    [JsonPropertyName("at")] public DateTimeOffset? At { get; set; }
    [JsonPropertyName("artifacts")] public List<MauiFlowArtifactReference> Artifacts { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Known terminal classifications for a flow run.</summary>
public static class MauiFlowFailureClasses
{
    public const string FlowInvalid = "flow-invalid";
    public const string SchemaUnsupported = "schema-unsupported";
    public const string CapabilityMissing = "capability-missing";
    public const string LeaseConflict = "lease-conflict";
    public const string LeaseLost = "lease-lost";
    public const string Cancelled = "cancelled";
    public const string Timeout = "timeout";
    public const string ResetFailed = "reset-failed";
    public const string PreconditionUnsatisfied = "precondition-unsatisfied";
    public const string RouteStateDrift = "route-state-drift";
    public const string LocatorNotFound = "locator-not-found";
    public const string LocatorAmbiguous = "locator-ambiguous";
    public const string NotVisible = "not-visible";
    public const string Disabled = "disabled";
    public const string UnstableBounds = "unstable-bounds";
    public const string ActionRejected = "action-rejected";
    public const string DriveFailed = "drive-failed";
    public const string UnknownCompletion = "unknown-completion";
    public const string WorkflowCommandConflict = "workflow-command-conflict";
    public const string SecretUnavailable = "secret-unavailable";
    public const string UnsafeValue = "unsafe-value";
    public const string AssertionFailed = "assertion-failed";
    public const string Transport = "transport";
    public const string AgentDisconnected = "agent-disconnected";
    public const string Infrastructure = "infrastructure";
}

/// <summary>A redaction-aware reference to a bounded artifact associated with a run.</summary>
public sealed class MauiFlowArtifactReference
{
    [JsonPropertyName("artifactId")] public string? ArtifactId { get; set; }
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("path")] public string? Path { get; set; }
    [JsonPropertyName("digest")] public string? Digest { get; set; }
    [JsonPropertyName("mediaType")] public string? MediaType { get; set; }
    [JsonPropertyName("redacted")] public bool? Redacted { get; set; }
    [JsonPropertyName("createdAt")] public DateTimeOffset? CreatedAt { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>An explicit reason a bounded report omitted otherwise available detail.</summary>
public sealed class MauiFlowReportOmission
{
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("count")] public int? Count { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
