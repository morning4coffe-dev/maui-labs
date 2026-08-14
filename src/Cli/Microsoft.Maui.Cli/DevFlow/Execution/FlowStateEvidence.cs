using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.Cli.DevFlow.Execution;

internal sealed record FlowStateEvidenceRequest
{
    public required MauiTestPlan Plan { get; init; }
    public required MauiFlow Flow { get; init; }
    public required ResolvedAppArtifact Artifact { get; init; }
}

internal sealed record FlowStateEvidenceResult
{
    public bool Supported { get; init; } = true;
    public string? DetailCode { get; init; }
    public string? Message { get; init; }
    public MauiFlowRunContext? RunContext { get; init; }
}

internal sealed record FlowStateAdmission
{
    public string? ProviderId { get; init; }
    public required MauiFlowRunContext RunContext { get; init; }
}

internal sealed record FlowPostRunOracleEvaluationRequest
{
    public required MauiTestPlan Plan { get; init; }
    public required MauiFlow Flow { get; init; }
    public required ResolvedAppArtifact Artifact { get; init; }
    public required string RunId { get; init; }
    public required string FlowDigest { get; init; }
    public required string DeviceIdentityFingerprint { get; init; }
    public required string AppBuildFingerprint { get; init; }
    public required string PackageDigest { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset EndedAt { get; init; }
    public required DateTimeOffset EvaluationDeadline { get; init; }
    public required MauiFlowRunReport Report { get; init; }
}

internal sealed record FlowPostRunOracleEvidenceResult
{
    public bool Supported { get; init; } = true;
    public string? DetailCode { get; init; }
    public string? Message { get; init; }
    public string? RunId { get; init; }
    public string? FlowDigest { get; init; }
    public string? DeviceIdentityFingerprint { get; init; }
    public string? AppBuildFingerprint { get; init; }
    public string? PackageDigest { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
    public IReadOnlyList<MauiIndependentBusinessOracleResult> BusinessOracles { get; init; } = [];
}

internal interface IFlowStateEvidenceProvider
{
    string ProviderId { get; }
    bool Supports(FlowStateEvidenceRequest request);

    Task<FlowStateEvidenceResult> PrepareAsync(
        FlowStateEvidenceRequest request,
        CancellationToken cancellationToken = default);

    Task<FlowPostRunOracleEvidenceResult> EvaluatePostRunAsync(
        FlowPostRunOracleEvaluationRequest request,
        CancellationToken cancellationToken = default);
}

internal interface IFlowStateEvidenceProviderRegistry
{
    Task<FlowStateAdmission> PrepareAsync(
        FlowStateEvidenceRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MauiIndependentBusinessOracleResult>> EvaluatePostRunAsync(
        FlowStateAdmission admission,
        FlowPostRunOracleEvaluationRequest request,
        CancellationToken cancellationToken = default);
}

internal sealed class FlowStateEvidenceProviderRegistry : IFlowStateEvidenceProviderRegistry
{
    private readonly IReadOnlyList<IFlowStateEvidenceProvider> _providers;

    public FlowStateEvidenceProviderRegistry(IEnumerable<IFlowStateEvidenceProvider> providers)
        => _providers = (providers ?? throw new ArgumentNullException(nameof(providers))).ToArray();

    public async Task<FlowStateAdmission> PrepareAsync(
        FlowStateEvidenceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var policy = request.Plan.ParsedSideEffectPolicy;
        if (policy == MauiFlowSideEffectPolicy.NonReplayable)
        {
            throw FlowExecutionException.Unsupported(
                "non-replayable-flow",
                "Non-replayable plans are not supported by unattended flow run.");
        }

        var matches = _providers.Where(provider => provider.Supports(request)).ToArray();
        var providerRequired =
            policy is MauiFlowSideEffectPolicy.TestTenantResettable or MauiFlowSideEffectPolicy.Compensated ||
            HasCheckpointRequirements(request.Plan.Checkpoint);
        if (matches.Length > 1)
        {
            throw FlowExecutionException.Invalid(
                "state-evidence-provider-ambiguous",
                "Multiple allow-listed state evidence providers matched the plan.");
        }
        if (matches.Length == 0)
        {
            if (providerRequired)
            {
                throw FlowExecutionException.Unsupported(
                    "state-evidence-provider-missing",
                    "The plan requires reset or state evidence, but no matching allow-listed provider is registered.");
            }

            return new FlowStateAdmission
            {
                RunContext = CreateSideEffectFreeContext(),
            };
        }

        var result = await matches[0].PrepareAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Supported || result.RunContext is null)
        {
            throw FlowExecutionException.Unsupported(
                result.DetailCode ?? "state-evidence-provider-unsupported",
                result.Message ?? "The matching state evidence provider could not establish the plan's reset and evidence contract.");
        }
        if (result.RunContext.BusinessOracles.Count > 0)
        {
            throw FlowExecutionException.Invalid(
                "pre-run-oracle-evidence-not-allowed",
                "A state evidence provider returned business-oracle evidence before the run completed.");
        }

        result.RunContext.Intent = MauiFlowReplayIntents.OrdinaryReplay;
        var decision = MauiFlowReplaySafetyEvaluator.Evaluate(new MauiFlowRunRequest
        {
            Plan = request.Plan,
            Context = result.RunContext,
        });
        if (!decision.OrdinaryReplayAllowed)
        {
            throw FlowExecutionException.Invalid(
                "state-evidence-admission-denied",
                "The state evidence provider did not satisfy the plan's replay admission contract.");
        }
        return new FlowStateAdmission
        {
            ProviderId = matches[0].ProviderId,
            RunContext = result.RunContext,
        };
    }

    public async Task<IReadOnlyList<MauiIndependentBusinessOracleResult>> EvaluatePostRunAsync(
        FlowStateAdmission admission,
        FlowPostRunOracleEvaluationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(admission.ProviderId))
            return [];

        var provider = _providers.SingleOrDefault(candidate =>
            string.Equals(candidate.ProviderId, admission.ProviderId, StringComparison.Ordinal));
        if (provider is null)
        {
            throw FlowExecutionException.Infrastructure(
                "post-run-oracle-provider-missing",
                "The state evidence provider selected during admission is unavailable after execution.");
        }

        var result = await provider.EvaluatePostRunAsync(request, cancellationToken).ConfigureAwait(false);
        if (!result.Supported)
        {
            throw FlowExecutionException.Infrastructure(
                result.DetailCode ?? "post-run-oracle-evaluation-failed",
                result.Message ?? "The state evidence provider could not evaluate post-run business oracles.");
        }
        ValidatePostRunBinding(result, request);
        return result.BusinessOracles;
    }

    private static MauiFlowRunContext CreateSideEffectFreeContext()
    {
        var empty = new MauiFlowCheckpoint();
        return new MauiFlowRunContext
        {
            Intent = MauiFlowReplayIntents.OrdinaryReplay,
            Preconditions = new MauiFlowReplayPreconditions
            {
                Expected = empty,
                Observed = new MauiFlowCheckpoint(),
                CheckedAt = DateTimeOffset.UtcNow,
            },
            BusinessOracles = [],
            PriorMutationCompletionCertain = true,
        };
    }

    private static bool HasCheckpointRequirements(MauiFlowCheckpointRequirements? checkpoint)
    {
        if (checkpoint is null)
            return false;
        return new[]
        {
            checkpoint.AppBuildFingerprint,
            checkpoint.SeedFingerprint,
            checkpoint.BackendStateFingerprint,
            checkpoint.AppStateSeed?.Fingerprint,
            checkpoint.BackendTestDataSeed?.Fingerprint,
            checkpoint.Locale,
            checkpoint.Theme,
            checkpoint.Orientation,
            checkpoint.Route,
            checkpoint.Window,
            checkpoint.Modal,
            checkpoint.CollectionItemKey,
            checkpoint.DisplayProfile,
        }.Any(static value => !string.IsNullOrWhiteSpace(value));
    }

    private static void ValidatePostRunBinding(
        FlowPostRunOracleEvidenceResult result,
        FlowPostRunOracleEvaluationRequest request)
    {
        if (!string.Equals(result.RunId, request.RunId, StringComparison.Ordinal) ||
            !string.Equals(result.FlowDigest, request.FlowDigest, StringComparison.Ordinal) ||
            !string.Equals(
                result.DeviceIdentityFingerprint,
                request.DeviceIdentityFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(result.AppBuildFingerprint, request.AppBuildFingerprint, StringComparison.Ordinal) ||
            !string.Equals(result.PackageDigest, request.PackageDigest, StringComparison.Ordinal) ||
            result.StartedAt != request.StartedAt ||
            result.EndedAt != request.EndedAt ||
            result.ObservedAt is null ||
            result.ObservedAt < request.EndedAt ||
            result.ObservedAt > request.EvaluationDeadline)
        {
            throw FlowExecutionException.Infrastructure(
                "post-run-oracle-binding-mismatch",
                "Post-run business-oracle evidence was not bound to the exact run, device, build, flow, and time window.");
        }

        if (result.BusinessOracles.Any(oracle =>
                oracle.ObservedAt is null ||
                oracle.ObservedAt < request.EndedAt ||
                oracle.ObservedAt > request.EvaluationDeadline))
        {
            throw FlowExecutionException.Infrastructure(
                "post-run-oracle-time-invalid",
                "Post-run business-oracle evidence was observed outside the bounded evaluation window.");
        }
    }
}
