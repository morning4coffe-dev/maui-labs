using System.Globalization;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.Cli.DevFlow.Broker;

/// <summary>
/// Trusted attestation of the reset, seed, and backend facts a broker cannot observe from a
/// running app. Implementations build, reset, and seed outside the broker and attest only the
/// state they actually applied. A broker never synthesises these facts and never echoes them
/// from a request.
/// </summary>
internal interface IWorkflowRepairResetAttester
{
    Task<WorkflowRepairResetAttestation?> AttestAsync(
        WorkflowRepairTransientValidationRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Reset, seed, and backend facts attested by a lifecycle-capable host.</summary>
internal sealed class WorkflowRepairResetAttestation
{
    public bool Succeeded { get; init; }
    public string? SeedFingerprint { get; init; }
    public string? BackendStateFingerprint { get; init; }
    public string? CollectionItemKey { get; init; }
    public List<string> EvidenceIds { get; init; } = [];
    public string? FailureCode { get; init; }
}

/// <summary>One broker-retained run produced by a bounded transient repair replay.</summary>
internal sealed class WorkflowRepairTransientReplayOutcome
{
    public string? RunId { get; init; }
    public MauiFlowRunReport? Report { get; init; }
    public string? EvidenceId { get; init; }
    public string? FailureCode { get; init; }
}

/// <summary>Reads the checkpoint facts the connected app can prove about itself.</summary>
internal delegate Task<MauiFlowCheckpoint?> WorkflowRepairCheckpointObserver(CancellationToken cancellationToken);

/// <summary>Re-establishes a classified route on the connected app.</summary>
internal delegate Task<bool> WorkflowRepairRouteRestore(string route, CancellationToken cancellationToken);

/// <summary>Runs one bounded, broker-retained replay of a transient in-memory flow.</summary>
internal delegate Task<WorkflowRepairTransientReplayOutcome?> WorkflowRepairTransientReplayRunner(
    MauiFlow flow,
    MauiTestPlan? plan,
    CancellationToken cancellationToken);

/// <summary>
/// Broker-side lifecycle host for bounded transient repair validation. It composes existing
/// broker primitives — live checkpoint observation, route restore, and the retained workflow run
/// path — and never writes a flow, a plan, or a proposed selector to a workspace.
/// </summary>
internal sealed class BrokerWorkflowRepairValidationHost : IWorkflowRepairValidationHost
{
    private readonly WorkflowRepairCheckpointObserver _observeCheckpoint;
    private readonly WorkflowRepairRouteRestore _restoreRoute;
    private readonly WorkflowRepairTransientReplayRunner _replay;
    private readonly IWorkflowRepairResetAttester? _resetAttester;

    internal BrokerWorkflowRepairValidationHost(
        WorkflowRepairCheckpointObserver observeCheckpoint,
        WorkflowRepairRouteRestore restoreRoute,
        WorkflowRepairTransientReplayRunner replay,
        IWorkflowRepairResetAttester? resetAttester = null)
    {
        _observeCheckpoint = observeCheckpoint ?? throw new ArgumentNullException(nameof(observeCheckpoint));
        _restoreRoute = restoreRoute ?? throw new ArgumentNullException(nameof(restoreRoute));
        _replay = replay ?? throw new ArgumentNullException(nameof(replay));
        _resetAttester = resetAttester;
    }

    public async Task<WorkflowRepairLifecycleValidation> HardResetAsync(
        WorkflowRepairTransientValidationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var classified = request.ClassifiedCheckpoint;
        if (classified is null)
            return ResetFailed("classified-checkpoint-required");

        // Seed, backend-state, and collection-item facts are not observable from a running app.
        // Without a lifecycle attester the broker cannot prove the app was reset to the classified
        // checkpoint, so validation fails closed instead of echoing the request back as evidence.
        if (_resetAttester is null)
            return ResetFailed("repair-reset-attester-unavailable");

        WorkflowRepairResetAttestation? attestation;
        try
        {
            attestation = await _resetAttester.AttestAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return ResetFailed("repair-reset-attestation-failed");
        }

        if (attestation is null || !attestation.Succeeded)
            return ResetFailed(attestation?.FailureCode ?? "repair-reset-attestation-failed", attestation?.EvidenceIds);

        if (string.IsNullOrWhiteSpace(classified.Route))
            return ResetFailed("classified-checkpoint-route-missing", attestation.EvidenceIds);

        bool restored;
        try
        {
            restored = await _restoreRoute(classified.Route!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return ResetFailed("post-reset-route-restore-failed", attestation.EvidenceIds);
        }

        if (!restored)
            return ResetFailed("post-reset-route-restore-failed", attestation.EvidenceIds);

        MauiFlowCheckpoint? observed;
        try
        {
            observed = await _observeCheckpoint(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return ResetFailed("post-reset-observation-unavailable", attestation.EvidenceIds);
        }

        if (observed is null)
            return ResetFailed("post-reset-observation-unavailable", attestation.EvidenceIds);

        return new WorkflowRepairLifecycleValidation
        {
            Succeeded = true,
            ExpectedCheckpoint = classified,
            ObservedCheckpoint = new MauiFlowCheckpoint
            {
                AppBuildFingerprint = observed.AppBuildFingerprint,
                AgentInstanceId = observed.AgentInstanceId,
                SeedFingerprint = attestation.SeedFingerprint,
                BackendStateFingerprint = attestation.BackendStateFingerprint,
                Route = observed.Route,
                Window = observed.Window,
                Modal = observed.Modal,
                Locale = observed.Locale,
                Theme = observed.Theme,
                Orientation = observed.Orientation,
                DisplayProfile = observed.DisplayProfile,
                CollectionItemKey = attestation.CollectionItemKey,
            },
            EvidenceIds = attestation.EvidenceIds
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .Take(32)
                .ToList(),
        };
    }

    public async Task<WorkflowRepairReplayValidation> ReplayWithInMemorySelectorOverrideAsync(
        WorkflowRepairTransientValidationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.InMemorySelectorOverrideOnly)
            return ReplayFailed("in-memory-override-required");

        var proposal = request.Proposal;
        if (proposal.Candidate?.Fingerprint is null ||
            proposal.ProposedSelector is null ||
            string.IsNullOrWhiteSpace(proposal.SourceStepId))
        {
            return ReplayFailed("selector-only-invariant-required");
        }

        var source = request.SourceFlow;
        if (source is null)
            return ReplayFailed("repair-source-flow-unavailable");
        if (string.IsNullOrWhiteSpace(proposal.BaseFlow?.Digest) ||
            !string.Equals(
                MauiFlowRunReportSerializer.ComputeFlowDigest(source),
                proposal.BaseFlow!.Digest,
                StringComparison.Ordinal))
        {
            return ReplayFailed("repair-source-flow-digest-mismatch");
        }

        // The canonical generator rebuilds the selector-only patch from the proposal and returns a
        // clone. The retained source flow is never mutated and the override is never persisted.
        var patched = MauiFlowRepairPatchBuilder.ApplyVerified(source, proposal);
        if (!patched.Ok || patched.PatchedFlow is null)
            return ReplayFailed("repair-transient-patch-invalid");

        var transient = patched.PatchedFlow;
        // The patch builder resolves the canonical step identity the runner also reports, so the
        // replay is compared against the same step the patch replaced.
        var repairedStepId = patched.Diff?.StepId;
        var repairedStep = FindStep(transient, repairedStepId);
        if (repairedStep is null || string.IsNullOrWhiteSpace(repairedStepId))
            return ReplayFailed("repair-source-step-unavailable");

        var downstreamAllowed = request.AllowDownstreamContinuation &&
            request.ReplaySafety?.DownstreamContinuationAllowed == true;
        if (!downstreamAllowed)
            transient.Steps.RemoveAll(step => step.Seq > repairedStep.Seq);

        WorkflowRepairTransientReplayOutcome? outcome;
        try
        {
            outcome = await _replay(transient, request.SourcePlan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return ReplayFailed("transient-replay-dispatch-failed");
        }

        if (outcome?.Report is null)
            return ReplayFailed(outcome?.FailureCode ?? "transient-replay-unavailable", outcome?.RunId);

        var report = outcome.Report;
        var attempt = report.Steps.FirstOrDefault(step =>
            string.Equals(step.StepId, repairedStepId, StringComparison.Ordinal));
        var reached = attempt is not null;
        var matchCount = attempt?.TargetResolution?.MatchCount;
        var fingerprintMatches = MauiRepairFingerprintComparer.SemanticallyMatches(
            proposal.Candidate.Fingerprint,
            attempt?.Fingerprint);
        // The proof is re-derived from the transient flow rather than trusted from the proposal,
        // and the replay must also show every observed assertion still holding.
        var assertionsUnchanged = patched.Proof?.Unchanged == true &&
            patched.Proof.ActionsUnchanged == true &&
            patched.Proof.ValuesUnchanged == true &&
            patched.Proof.OrderUnchanged == true &&
            report.Steps
                .SelectMany(static step => step.Assertions)
                .All(static assertion => assertion.Passed == true && assertion.Skipped != true);
        // Verified is the run path's own answer to "did an oracle independent of the UI selector
        // confirm the effect", and any recorded oracle must itself be independent and successful.
        var oracleSucceeded = report.Verification?.Verified == true &&
            report.ReplayEligibility?.RunVerificationAllowed == true &&
            report.BusinessOracles.All(static oracle =>
                oracle.Succeeded == true && oracle.Independent == true);
        var continuedDownstream = report.Steps.Any(step => step.Sequence > repairedStep.Seq);
        var outcomePassed = string.Equals(
            report.Outcome?.Status,
            MauiFlowRunOutcomes.Passed,
            StringComparison.Ordinal);
        var evidence = new[] { outcome.EvidenceId, report.ReportPath, report.ReportDigest }
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id!)
            .Distinct(StringComparer.Ordinal)
            .Take(32)
            .ToList();

        return new WorkflowRepairReplayValidation
        {
            ReachedFailedStep = reached,
            RunId = outcome.RunId ?? report.RunId,
            CandidateMatchCount = matchCount,
            ObservedFingerprint = attempt?.Fingerprint,
            SemanticFingerprintMatches = fingerprintMatches,
            HardAssertionsUnchanged = assertionsUnchanged,
            IndependentOracleSucceeded = oracleSucceeded,
            ContinuedDownstream = continuedDownstream,
            EvidenceIds = evidence,
            Passed = reached &&
                matchCount == 1 &&
                fingerprintMatches &&
                assertionsUnchanged &&
                oracleSucceeded &&
                outcomePassed &&
                (!continuedDownstream || downstreamAllowed),
            FailureCode = reached && outcomePassed ? null : "transient-replay-failed",
        };
    }

    /// <summary>Mirrors the canonical step identity so retained ids resolve to executable steps.</summary>
    private static FlowStep? FindStep(MauiFlow flow, string stepId)
        => flow.Steps.FirstOrDefault(step => string.Equals(
            string.IsNullOrWhiteSpace(step.StepId)
                ? step.Seq.ToString(CultureInfo.InvariantCulture)
                : step.StepId.Trim(),
            stepId,
            StringComparison.Ordinal));

    private static WorkflowRepairLifecycleValidation ResetFailed(
        string failureCode,
        IEnumerable<string>? evidenceIds = null)
        => new()
        {
            Succeeded = false,
            FailureCode = failureCode,
            EvidenceIds = (evidenceIds ?? [])
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .Take(32)
                .ToList(),
        };

    private static WorkflowRepairReplayValidation ReplayFailed(string failureCode, string? runId = null)
        => new()
        {
            FailureCode = failureCode,
            RunId = runId,
        };
}
