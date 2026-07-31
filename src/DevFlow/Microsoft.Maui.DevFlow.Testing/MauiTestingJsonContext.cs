using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.Maui.DevFlow.Testing;

/// <summary>Source-generated JSON metadata for non-executable DevFlow testing contracts.</summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MauiTestPlan))]
[JsonSerializable(typeof(MauiFlowSideEffectPolicy))]
[JsonSerializable(typeof(MauiFlowRunRequest))]
[JsonSerializable(typeof(MauiFlowRunContext))]
[JsonSerializable(typeof(MauiFlowRunReport))]
[JsonSerializable(typeof(MauiFlowRunReport[]))]
[JsonSerializable(typeof(MauiFlowRunTarget))]
[JsonSerializable(typeof(MauiFlowResetResult))]
[JsonSerializable(typeof(MauiFlowResetReference))]
[JsonSerializable(typeof(MauiFlowResetOutcome))]
[JsonSerializable(typeof(MauiFlowAppStateSeedFingerprint))]
[JsonSerializable(typeof(MauiFlowBackendTestDataSeedFingerprint))]
[JsonSerializable(typeof(MauiFlowReplayPreconditions))]
[JsonSerializable(typeof(MauiFlowCheckpoint))]
[JsonSerializable(typeof(MauiFlowCompensatorReference))]
[JsonSerializable(typeof(MauiFlowCompensatorOutcome))]
[JsonSerializable(typeof(MauiIndependentBusinessOracleDeclaration))]
[JsonSerializable(typeof(MauiIndependentBusinessOracleResult))]
[JsonSerializable(typeof(MauiFlowReplayEligibilityDecision))]
[JsonSerializable(typeof(MauiFlowReplayEligibilityReason))]
[JsonSerializable(typeof(MauiFlowRunVerification))]
[JsonSerializable(typeof(MauiFlowRunOutcome))]
[JsonSerializable(typeof(MauiFlowRunEvent))]
[JsonSerializable(typeof(MauiFlowStepAttempt))]
[JsonSerializable(typeof(MauiFlowTargetResolution))]
[JsonSerializable(typeof(MauiFlowActionabilityAttempt))]
[JsonSerializable(typeof(MauiFlowDispatchReceipt))]
[JsonSerializable(typeof(MauiFlowAssertionResult))]
[JsonSerializable(typeof(MauiFlowValueDisclosure))]
[JsonSerializable(typeof(MauiFlowFailure))]
[JsonSerializable(typeof(MauiFlowArtifactReference))]
[JsonSerializable(typeof(MauiFlowReportOmission))]
[JsonSerializable(typeof(MauiFlowRepairProposal))]
[JsonSerializable(typeof(MauiFlowRepairOutcome))]
[JsonSerializable(typeof(MauiFlowMigrationResult))]
[JsonSerializable(typeof(MauiFlowRequirements))]
[JsonSerializable(typeof(MauiFlowCapabilitySet))]
[JsonSerializable(typeof(MauiFlowRequirementValidation))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
public sealed partial class MauiTestingJsonContext : JsonSerializerContext;
