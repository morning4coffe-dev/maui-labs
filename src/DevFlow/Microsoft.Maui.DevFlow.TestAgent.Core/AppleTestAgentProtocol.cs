using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.Maui.DevFlow.TestAgent.Protocol;

/// <summary>Version and bounded-transfer constants for the Apple XCTest test-agent protocol.</summary>
public static class AppleTestAgentProtocolVersions
{
    public const int Schema = 1;
    public const string Name = "maui-apple-test-agent-v1";
    public const string DeviceAgentVersion = "1.0.0-experimental";
    public const int MaximumArtifactChunkBytes = 64 * 1024;
    public const int MaximumArtifactBytes = 4 * 1024 * 1024;
    public const int MaximumArtifactChunks = 128;
    public const int MaximumOperationResultBytes = 512 * 1024;
    public const int MaximumSessionSeconds = 600;
    public const int MaximumCommandsPerSession = 2_048;
}

/// <summary>Operation names intentionally limited to the <c>IMauiFlowDriver</c> surface.</summary>
public static class AppleTestAgentOperations
{
    public const string Status = "status";
    public const string Tree = "tree";
    public const string Query = "query";
    public const string Element = "element";
    public const string Property = "property";
    public const string Tap = "tap";
    public const string Fill = "fill";
    public const string SetProperty = "set-property";
    public const string Scroll = "scroll";
    public const string Navigate = "navigate";
    public const string Back = "back";
    public const string SetTheme = "set-theme";
    public const string Screenshot = "screenshot";
    public const string Wait = "wait";
    public const string Shutdown = "shutdown";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Status, Tree, Query, Element, Property, Tap, Fill, SetProperty, Scroll, Navigate, Back,
        SetTheme, Screenshot, Wait, Shutdown,
    };
}

/// <summary>Stable operation-level error codes. Values never include target values or credentials.</summary>
public static class AppleTestAgentErrorCodes
{
    public const string InvalidRequest = "apple-agent-invalid-request";
    public const string AuthenticationFailed = "apple-agent-authentication-failed";
    public const string ReplayRejected = "apple-agent-replay-rejected";
    public const string SessionMismatch = "apple-agent-session-mismatch";
    public const string TargetMismatch = "apple-agent-target-mismatch";
    public const string StaleEpoch = "apple-agent-stale-epoch";
    public const string ApprovalMismatch = "apple-agent-approval-mismatch";
    public const string SequenceRejected = "apple-agent-sequence-rejected";
    public const string CommandConflict = "apple-agent-command-conflict";
    public const string DeadlineExpired = "apple-agent-deadline-expired";
    public const string UnknownCompletion = "apple-agent-unknown-completion";
    public const string Cancelled = "apple-agent-cancelled";
    public const string AgentOrphaned = "apple-agent-orphaned";
    public const string AttachmentRejected = "apple-agent-attachment-rejected";
    public const string CapabilityMissing = "apple-agent-capability-missing";
    public const string ArtifactRejected = "apple-agent-artifact-rejected";
}

/// <summary>Identity of the host-approved target app and the process currently controlled by XCTest.</summary>
public sealed class AppleTestAgentTarget
{
    [JsonPropertyName("platform")] public string Platform { get; set; } = "";
    [JsonPropertyName("targetBundleId")] public string TargetBundleId { get; set; } = "";
    [JsonPropertyName("appInstanceId")] public string? AppInstanceId { get; set; }
    [JsonPropertyName("appBuildDigest")] public string? AppBuildDigest { get; set; }
    [JsonPropertyName("deviceIdDigest")] public string? DeviceIdDigest { get; set; }
    [JsonPropertyName("agentEndpoint")] public string? AgentEndpoint { get; set; }
    [JsonPropertyName("experimental")] public bool Experimental { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// Session facts issued by the macOS host. The capability token is represented only by a digest;
/// its secret material is transport-local and is never serialized into artifacts.
/// </summary>
public sealed class AppleTestAgentSession
{
    [JsonPropertyName("schema")] public int Schema { get; set; } = AppleTestAgentProtocolVersions.Schema;
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("hostInstanceId")] public string HostInstanceId { get; set; } = "";
    [JsonPropertyName("target")] public AppleTestAgentTarget Target { get; set; } = new();
    [JsonPropertyName("authorityEpoch")] public long AuthorityEpoch { get; set; }
    [JsonPropertyName("approvalDigest")] public string? ApprovalDigest { get; set; }
    [JsonPropertyName("capabilityTokenDigest")] public string? CapabilityTokenDigest { get; set; }
    [JsonPropertyName("expiresAt")] public DateTimeOffset ExpiresAt { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Proof attached to every HTTP request. It intentionally contains no bearer token.</summary>
public sealed class AppleTestAgentAuthentication
{
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("timestampUnixSeconds")] public long TimestampUnixSeconds { get; set; }
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = "";
    [JsonPropertyName("signature")] public string Signature { get; set; } = "";
}

/// <summary>One operation request, fenced by an epoch, contiguous sequence, digest, and deadline.</summary>
public sealed class AppleTestAgentOperationCommand
{
    [JsonPropertyName("schema")] public int Schema { get; set; } = AppleTestAgentProtocolVersions.Schema;
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("target")] public AppleTestAgentTarget Target { get; set; } = new();
    [JsonPropertyName("authorityEpoch")] public long AuthorityEpoch { get; set; }
    [JsonPropertyName("commandId")] public string CommandId { get; set; } = "";
    [JsonPropertyName("sequence")] public long Sequence { get; set; }
    [JsonPropertyName("actionDigest")] public string ActionDigest { get; set; } = "";
    [JsonPropertyName("approvalDigest")] public string? ApprovalDigest { get; set; }
    [JsonPropertyName("deadline")] public DateTimeOffset Deadline { get; set; }
    [JsonPropertyName("operation")] public string Operation { get; set; } = "";
    [JsonPropertyName("arguments")] public Dictionary<string, string>? Arguments { get; set; }
    [JsonPropertyName("hostSignature")] public string? HostSignature { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Metadata-only receipt for a command transition. Request and result bodies are never retained.</summary>
public sealed class AppleTestAgentCommandReceipt
{
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("commandId")] public string CommandId { get; set; } = "";
    [JsonPropertyName("sequence")] public long Sequence { get; set; }
    [JsonPropertyName("actionDigest")] public string ActionDigest { get; set; } = "";
    [JsonPropertyName("authorityEpoch")] public long AuthorityEpoch { get; set; }
    [JsonPropertyName("approvalDigest")] public string? ApprovalDigest { get; set; }
    [JsonPropertyName("acknowledgementState")] public string AcknowledgementState { get; set; } = "prepared";
    [JsonPropertyName("completionCertainty")] public string CompletionCertainty { get; set; } = "pending";
    [JsonPropertyName("at")] public DateTimeOffset At { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Completion of an operation. Result bytes are base64 encoded and bounded by the host.</summary>
public sealed class AppleTestAgentOperationCompletion
{
    [JsonPropertyName("receipt")] public AppleTestAgentCommandReceipt Receipt { get; set; } = new();
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("completionCertainty")] public string CompletionCertainty { get; set; } = "certain";
    [JsonPropertyName("resultBase64")] public string? ResultBase64 { get; set; }
    [JsonPropertyName("error")] public AppleTestAgentError? Error { get; set; }
    [JsonPropertyName("artifacts")] public List<AppleTestAgentArtifactReference> Artifacts { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Explicit cancellation state for a command; cancellation never implies an automatic retry.</summary>
public sealed class AppleTestAgentCancellation
{
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("commandId")] public string CommandId { get; set; } = "";
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("requestedAt")] public DateTimeOffset RequestedAt { get; set; }
}

/// <summary>Safe, typed error returned by the transport or device agent.</summary>
public sealed class AppleTestAgentError
{
    [JsonPropertyName("code")] public string Code { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("retryable")] public bool Retryable { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Capability declaration made by the native XCTest agent during an authenticated hello.</summary>
public sealed class AppleTestAgentCapabilities
{
    [JsonPropertyName("protocol")] public string Protocol { get; set; } = AppleTestAgentProtocolVersions.Name;
    [JsonPropertyName("operations")] public List<string> Operations { get; set; } = [];
    [JsonPropertyName("targetForegroundOwned")] public bool TargetForegroundOwned { get; set; }
    [JsonPropertyName("authenticatedTransport")] public bool AuthenticatedTransport { get; set; }
    [JsonPropertyName("maxArtifactChunkBytes")] public int MaxArtifactChunkBytes { get; set; }
    [JsonPropertyName("deviceAgentVersion")] public string? DeviceAgentVersion { get; set; }
    [JsonPropertyName("targetProcessId")] public int? TargetProcessId { get; set; }
    [JsonPropertyName("targetAgentInstanceId")] public string? TargetAgentInstanceId { get; set; }
    [JsonPropertyName("webViewContextIdentity")] public string? WebViewContextIdentity { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Authenticated native-agent attachment to a specific host session and target process.</summary>
public sealed class AppleTestAgentHello
{
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("target")] public AppleTestAgentTarget Target { get; set; } = new();
    [JsonPropertyName("capabilities")] public AppleTestAgentCapabilities Capabilities { get; set; } = new();
    [JsonPropertyName("agentInstanceId")] public string AgentInstanceId { get; set; } = "";
    [JsonPropertyName("attachedAt")] public DateTimeOffset AttachedAt { get; set; }
}

/// <summary>Reference to a bounded artifact returned over the authenticated transport.</summary>
public sealed class AppleTestAgentArtifactReference
{
    [JsonPropertyName("artifactId")] public string ArtifactId { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("sizeBytes")] public long SizeBytes { get; set; }
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }
}

/// <summary>One bounded base64 artifact chunk. Each chunk carries its own content digest.</summary>
public sealed class AppleTestAgentArtifactChunk
{
    [JsonPropertyName("sessionId")] public string SessionId { get; set; } = "";
    [JsonPropertyName("artifactId")] public string ArtifactId { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("chunkIndex")] public int ChunkIndex { get; set; }
    [JsonPropertyName("totalChunks")] public int TotalChunks { get; set; }
    [JsonPropertyName("contentBase64")] public string ContentBase64 { get; set; } = "";
    [JsonPropertyName("contentDigest")] public string ContentDigest { get; set; } = "";
    [JsonPropertyName("isFinal")] public bool IsFinal { get; set; }
}
