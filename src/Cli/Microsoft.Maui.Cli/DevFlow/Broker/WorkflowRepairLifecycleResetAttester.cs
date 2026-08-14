using Microsoft.Maui.Cli.DevFlow.Execution;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.Cli.DevFlow.Broker;

/// <summary>
/// Evaluates the plan's declared independent business oracles. It is supplied by the same host that
/// owns the app lifecycle, because an oracle that is independent of the UI is by definition
/// something the broker cannot observe through the agent it drives.
/// </summary>
internal delegate Task<IReadOnlyList<MauiIndependentBusinessOracleResult>> WorkflowRepairOracleEvaluator(
    WorkflowRepairTransientValidationRequest request,
    CancellationToken cancellationToken);

/// <summary>
/// Adapts an app lifecycle owner to the broker's reset attester contract.
/// </summary>
/// <remarks>
/// The adapter is deliberately thin and refuses more than it supplies. It copies the owner's applied
/// facts into a reset result and never derives one from the request, the connected app, or a
/// default. Where the reviewed plan requires a fact the owner did not apply — a backend seed, a
/// pinned collection item — it fails closed instead of substituting a placeholder.
/// </remarks>
internal sealed class WorkflowRepairLifecycleResetAttester : IWorkflowRepairResetAttester
{
    private readonly IFlowLifecycleResetOwner _owner;
    private readonly WorkflowRepairOracleEvaluator? _oracles;

    internal WorkflowRepairLifecycleResetAttester(
        IFlowLifecycleResetOwner owner,
        WorkflowRepairOracleEvaluator? oracles = null)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _oracles = oracles;
    }

    public async Task<WorkflowRepairAttestedState?> ObserveAttestedStateAsync(CancellationToken cancellationToken)
    {
        var applied = await _owner.GetAppliedStateAsync(cancellationToken).ConfigureAwait(false);
        return applied is null ? null : ToAttestedState(applied);
    }

    public async Task<WorkflowRepairResetAttestation?> AttestAsync(
        WorkflowRepairTransientValidationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var checkpoint = request.SourcePlan?.Checkpoint;
        var requiresBackendSeed = RequiresBackendSeed(checkpoint);
        var pinnedCollectionItem = PinnedCollectionItem(checkpoint, request.ClassifiedCheckpoint);

        var outcome = await _owner.ResetAsync(
            new FlowLifecycleResetRequest
            {
                Reason = request.Proposal.ProposalId,
                ExpectedSeedIdentity = checkpoint?.AppStateSeed?.SeedId,
                RequiresBackendSeed = requiresBackendSeed,
                RequiresCollectionItemKey = pinnedCollectionItem is not null,
            },
            cancellationToken).ConfigureAwait(false);
        if (!outcome.Succeeded || outcome.Applied is null)
            return Failed(outcome.FailureCode ?? "repair-reset-attestation-failed", outcome.EvidenceIds);

        var applied = outcome.Applied;
        if (!applied.AppStateSucceeded)
            return Failed("repair-app-state-reset-unattested", outcome.EvidenceIds);
        // "No backend seed was applied" is only an acceptable answer where the reviewed plan says no
        // backend seed is required. Where the plan requires one, the absence is a refusal, not a fact.
        if (requiresBackendSeed &&
            (!applied.BackendTestDataSucceeded ||
             string.Equals(
                 applied.BackendStateFingerprint,
                 FlowLifecycleResetFingerprints.NoBackendApplied,
                 StringComparison.Ordinal)))
        {
            return Failed("repair-backend-seed-unattested", outcome.EvidenceIds);
        }
        if (pinnedCollectionItem is not null &&
            !string.Equals(pinnedCollectionItem, applied.CollectionItemKey, StringComparison.Ordinal))
        {
            return Failed("repair-collection-item-unattested", outcome.EvidenceIds);
        }

        var oracles = _oracles is null
            ? []
            : await _oracles(request, cancellationToken).ConfigureAwait(false);

        return new WorkflowRepairResetAttestation
        {
            Succeeded = true,
            Reset = new MauiFlowResetResult
            {
                Requested = true,
                Succeeded = true,
                AppStateSucceeded = applied.AppStateSucceeded,
                BackendTestDataSucceeded = applied.BackendTestDataSucceeded || !requiresBackendSeed,
                Strategy = applied.Strategy,
                ResetIdentity = applied.ResetIdentity,
                SeedFingerprint = applied.SeedFingerprint,
                BackendStateFingerprint = applied.BackendStateFingerprint,
                AppStateSeed = new MauiFlowAppStateSeedFingerprint
                {
                    SeedId = applied.SeedIdentity,
                    Fingerprint = applied.SeedFingerprint,
                    Source = _owner.OwnerId,
                },
                BackendTestDataSeed = new MauiFlowBackendTestDataSeedFingerprint
                {
                    Fingerprint = applied.BackendStateFingerprint,
                    Source = _owner.OwnerId,
                },
                Outcome = new MauiFlowResetOutcome
                {
                    Requested = true,
                    Succeeded = true,
                    AppStateSucceeded = applied.AppStateSucceeded,
                    BackendTestDataSucceeded = applied.BackendTestDataSucceeded || !requiresBackendSeed,
                    CompletedAt = DateTimeOffset.UtcNow,
                },
            },
            CollectionItemKey = applied.CollectionItemKey,
            // Independence and success are the evaluator's claims about its own observation. The
            // adapter forwards them unchanged and never promotes an oracle it did not receive.
            BusinessOracles = [.. oracles],
            EvidenceIds = [.. outcome.EvidenceIds],
        };
    }

    private static WorkflowRepairAttestedState ToAttestedState(FlowLifecycleAppliedState applied)
        => new()
        {
            SeedFingerprint = applied.SeedFingerprint,
            BackendStateFingerprint = applied.BackendStateFingerprint,
            CollectionItemKey = applied.CollectionItemKey,
        };

    private static bool RequiresBackendSeed(MauiFlowCheckpointRequirements? checkpoint)
        => !string.IsNullOrWhiteSpace(checkpoint?.BackendStateFingerprint) ||
           !string.IsNullOrWhiteSpace(checkpoint?.BackendTestDataSeed?.Fingerprint) ||
           !string.IsNullOrWhiteSpace(checkpoint?.BackendTestDataSeed?.SeedId);

    /// <summary>
    /// The collection identity the reviewed plan or the classified checkpoint pins, ignoring the
    /// well-known "nothing is pinned" sentinel.
    /// </summary>
    private static string? PinnedCollectionItem(
        MauiFlowCheckpointRequirements? checkpoint,
        MauiFlowCheckpoint? classified)
    {
        foreach (var candidate in new[] { checkpoint?.CollectionItemKey, classified?.CollectionItemKey })
        {
            var normalized = candidate?.Trim();
            if (!string.IsNullOrWhiteSpace(normalized) &&
                !string.Equals(
                    normalized,
                    FlowLifecycleResetFingerprints.NoCollectionItem,
                    StringComparison.Ordinal))
            {
                return normalized;
            }
        }

        return null;
    }

    private static WorkflowRepairResetAttestation Failed(string failureCode, IEnumerable<string>? evidenceIds)
        => new()
        {
            Succeeded = false,
            FailureCode = failureCode,
            EvidenceIds = [.. evidenceIds ?? []],
        };
}
