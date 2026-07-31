using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.Maui.DevFlow.Testing;

/// <summary>
/// A human-reviewable proposal to replace a selector after a pre-dispatch locator-not-found
/// failure. This contract does not apply a patch or execute a validation replay.
/// </summary>
public sealed class MauiFlowRepairProposal
{
    [JsonPropertyName("schema")] public int Schema { get; init; } = 1;
    [JsonPropertyName("proposalId")] public string? ProposalId { get; init; }
    [JsonPropertyName("sourceRunId")] public string? SourceRunId { get; init; }
    [JsonPropertyName("sourceStepId")] public string? SourceStepId { get; init; }
    [JsonPropertyName("sourceFailureId")] public string? SourceFailureId { get; init; }
    [JsonPropertyName("baseFlow")] public MauiFlowReference? BaseFlow { get; init; }
    [JsonPropertyName("oldSelector")] public FlowSelector? OldSelector { get; init; }
    [JsonPropertyName("proposedSelector")] public FlowSelector? ProposedSelector { get; init; }
    [JsonPropertyName("candidate")] public MauiSelectorCandidate? Candidate { get; init; }
    [JsonPropertyName("uniquenessProof")] public MauiRepairUniquenessProof? UniquenessProof { get; init; }
    [JsonPropertyName("validationRunIds")] public List<string> ValidationRunIds { get; init; } = [];
    [JsonPropertyName("patch")] public MauiFlowPatch? Patch { get; init; }
    [JsonPropertyName("patchDigest")] public string? PatchDigest { get; init; }
    [JsonPropertyName("unchangedAssertionsProof")] public MauiRepairAssertionProof? UnchangedAssertionsProof { get; init; }
    [JsonPropertyName("approval")] public MauiRepairApproval? Approval { get; init; }
    [JsonPropertyName("riskFlags")] public List<string> RiskFlags { get; init; } = [];
    [JsonPropertyName("provenance")] public MauiActorProvenance? Provenance { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Evidence about an element without retaining user-entered text or values by default.</summary>
public sealed class MauiElementFingerprint
{
    [JsonPropertyName("appId")] public string? AppId { get; init; }
    [JsonPropertyName("buildFingerprint")] public string? BuildFingerprint { get; init; }
    [JsonPropertyName("platform")] public string? Platform { get; init; }
    [JsonPropertyName("route")] public string? Route { get; init; }
    [JsonPropertyName("window")] public string? Window { get; init; }
    [JsonPropertyName("modal")] public string? Modal { get; init; }
    [JsonPropertyName("managedType")] public string? ManagedType { get; init; }
    [JsonPropertyName("role")] public string? Role { get; init; }
    [JsonPropertyName("automationId")] public string? AutomationId { get; init; }
    [JsonPropertyName("nativeAutomationId")] public string? NativeAutomationId { get; init; }
    [JsonPropertyName("sourceAnchor")] public string? SourceAnchor { get; init; }
    [JsonPropertyName("sourceHash")] public string? SourceHash { get; init; }
    [JsonPropertyName("ancestorTopologyHash")] public string? AncestorTopologyHash { get; init; }
    [JsonPropertyName("siblingTopologyHash")] public string? SiblingTopologyHash { get; init; }
    [JsonPropertyName("collectionKey")] public string? CollectionKey { get; init; }
    [JsonPropertyName("itemKey")] public string? ItemKey { get; init; }
    [JsonPropertyName("normalizedBounds")] public MauiNormalizedBounds? NormalizedBounds { get; init; }
    [JsonPropertyName("observedAt")] public DateTimeOffset? ObservedAt { get; init; }
    [JsonPropertyName("capabilityVersion")] public string? CapabilityVersion { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Bounds normalized to the current window or display profile.</summary>
public sealed class MauiNormalizedBounds
{
    [JsonPropertyName("x")] public double? X { get; init; }
    [JsonPropertyName("y")] public double? Y { get; init; }
    [JsonPropertyName("width")] public double? Width { get; init; }
    [JsonPropertyName("height")] public double? Height { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>A scored selector candidate that is not automatically activated or applied.</summary>
public sealed class MauiSelectorCandidate
{
    [JsonPropertyName("candidateId")] public string? CandidateId { get; init; }
    [JsonPropertyName("selector")] public FlowSelector? Selector { get; init; }
    [JsonPropertyName("scope")] public MauiFlowCheckpoint? Scope { get; init; }
    [JsonPropertyName("originCodes")] public List<string> OriginCodes { get; init; } = [];
    [JsonPropertyName("rationaleCodes")] public List<string> RationaleCodes { get; init; } = [];
    [JsonPropertyName("riskFlags")] public List<string> RiskFlags { get; init; } = [];
    [JsonPropertyName("score")] public double? Score { get; init; }
    [JsonPropertyName("scoreComponents")] public Dictionary<string, double> ScoreComponents { get; init; } = new(StringComparer.Ordinal);
    [JsonPropertyName("unique")] public bool? Unique { get; init; }
    [JsonPropertyName("platformValidated")] public bool? PlatformValidated { get; init; }
    [JsonPropertyName("calibrationStatus")] public string? CalibrationStatus { get; init; }
    [JsonPropertyName("fingerprint")] public MauiElementFingerprint? Fingerprint { get; init; }
    [JsonPropertyName("evidenceArtifacts")] public List<MauiFlowArtifactReference> EvidenceArtifacts { get; init; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Evidence that a proposed selector resolved to exactly one target.</summary>
public sealed class MauiRepairUniquenessProof
{
    [JsonPropertyName("matchCount")] public int? MatchCount { get; init; }
    [JsonPropertyName("validatedAt")] public DateTimeOffset? ValidatedAt { get; init; }
    [JsonPropertyName("platform")] public string? Platform { get; init; }
    [JsonPropertyName("evidenceArtifacts")] public List<MauiFlowArtifactReference> EvidenceArtifacts { get; init; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>A minimal, descriptive patch for a future host to apply after approval.</summary>
public sealed class MauiFlowPatch
{
    [JsonPropertyName("format")] public string? Format { get; init; }
    [JsonPropertyName("operations")] public List<MauiFlowPatchOperation> Operations { get; init; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>One declarative operation in a proposed flow patch.</summary>
public sealed class MauiFlowPatchOperation
{
    [JsonPropertyName("op")] public string? Op { get; init; }
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("value")] public JsonElement? Value { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Evidence that the proposal did not change the flow's hard assertions.</summary>
public sealed class MauiRepairAssertionProof
{
    [JsonPropertyName("unchanged")] public bool? Unchanged { get; init; }
    [JsonPropertyName("assertionDigest")] public string? AssertionDigest { get; init; }
    [JsonPropertyName("method")] public string? Method { get; init; }
    [JsonPropertyName("verificationRunIds")] public List<string> VerificationRunIds { get; init; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>The approval state and expiration for a repair proposal.</summary>
public sealed class MauiRepairApproval
{
    [JsonPropertyName("state")] public string? State { get; init; }
    [JsonPropertyName("reviewer")] public string? Reviewer { get; init; }
    [JsonPropertyName("approvedAt")] public DateTimeOffset? ApprovedAt { get; init; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset? ExpiresAt { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Records the immutable result of a repair proposal after review or application.</summary>
public sealed class MauiFlowRepairOutcome
{
    [JsonPropertyName("schema")] public int Schema { get; init; } = 1;
    [JsonPropertyName("proposalId")] public string? ProposalId { get; init; }
    [JsonPropertyName("state")] public string? State { get; init; }
    [JsonPropertyName("newFlowRevision")] public int? NewFlowRevision { get; init; }
    [JsonPropertyName("verificationRunIds")] public List<string> VerificationRunIds { get; init; } = [];
    [JsonPropertyName("rollbackRevision")] public int? RollbackRevision { get; init; }
    [JsonPropertyName("reviewer")] public string? Reviewer { get; init; }
    [JsonPropertyName("recordedAt")] public DateTimeOffset? RecordedAt { get; init; }
    [JsonPropertyName("safeAuditDigest")] public string? SafeAuditDigest { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("replayEligibility")] public MauiFlowReplayEligibilityDecision? ReplayEligibility { get; init; }
    [JsonPropertyName("verification")] public MauiFlowRunVerification? Verification { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Known repair outcome values.</summary>
public static class MauiFlowRepairOutcomeStates
{
    public const string Rejected = "rejected";
    public const string Stale = "stale";
    public const string Applied = "applied";
    public const string Verified = "verified";
    public const string Reverted = "reverted";
}
