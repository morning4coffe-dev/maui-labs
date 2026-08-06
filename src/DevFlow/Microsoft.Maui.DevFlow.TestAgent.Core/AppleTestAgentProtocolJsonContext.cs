using System.Text.Json.Serialization;

namespace Microsoft.Maui.DevFlow.TestAgent.Protocol;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppleTestAgentSession))]
[JsonSerializable(typeof(AppleTestAgentTarget))]
[JsonSerializable(typeof(AppleTestAgentAuthentication))]
[JsonSerializable(typeof(AppleTestAgentOperationCommand))]
[JsonSerializable(typeof(AppleTestAgentCommandReceipt))]
[JsonSerializable(typeof(AppleTestAgentOperationCompletion))]
[JsonSerializable(typeof(AppleTestAgentCancellation))]
[JsonSerializable(typeof(AppleTestAgentError))]
[JsonSerializable(typeof(AppleTestAgentCapabilities))]
[JsonSerializable(typeof(AppleTestAgentHello))]
[JsonSerializable(typeof(AppleTestAgentArtifactChunk))]
[JsonSerializable(typeof(AppleTestAgentArtifactReference))]
[JsonSerializable(typeof(List<AppleTestAgentArtifactReference>))]
public partial class AppleTestAgentProtocolJsonContext : JsonSerializerContext;
