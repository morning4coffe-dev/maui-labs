using Microsoft.Maui.DevFlow.Driver;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.Cli.DevFlow.Broker;

/// <summary>
/// Broker-owned services exposed to one Inspector target. This is deliberately an adapter over
/// the coordinator and trust stores: it does not implement replay, artifact parsing, or policy.
/// </summary>
internal sealed class InspectorWorkflowServices
{
    private readonly WorkflowRunCoordinator _runs;
    private readonly ArtifactTrustImportService _imports;
    private readonly ArtifactTrustStore _artifacts;
    private readonly WorkflowRepairProposalStore _repairs;
    private readonly WorkflowXamlSourceProposalStore _sources;
    private readonly WorkflowCSharpSourceProposalStore _csharpSources;
    private readonly WorkflowRunTarget _target;
    private readonly Func<bool> _isTargetCurrent;
    private readonly CancellationToken _cancellationToken;

    public InspectorWorkflowServices(
        WorkflowRunCoordinator runs,
        ArtifactTrustImportService imports,
        ArtifactTrustStore artifacts,
        WorkflowRepairProposalStore repairs,
        WorkflowXamlSourceProposalStore sources,
        WorkflowCSharpSourceProposalStore csharpSources,
        WorkflowRunTarget target,
        Func<bool> isTargetCurrent,
        CancellationToken cancellationToken)
    {
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _imports = imports ?? throw new ArgumentNullException(nameof(imports));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _repairs = repairs ?? throw new ArgumentNullException(nameof(repairs));
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _csharpSources = csharpSources ?? throw new ArgumentNullException(nameof(csharpSources));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _isTargetCurrent = isTargetCurrent ?? throw new ArgumentNullException(nameof(isTargetCurrent));
        _cancellationToken = cancellationToken;
    }

    public WorkflowRunTarget Target => _target;

    public bool IsTargetCurrent() => _isTargetCurrent();

    public WorkflowRunCapabilitiesResponse GetCapabilities() => _runs.GetCapabilities();

    public WorkflowRunPreflightResult Preflight(WorkflowRunStartRequest request)
        => _runs.Preflight(request, _target, _isTargetCurrent);

    public WorkflowRunStartResult Start(
        WorkflowRunStartRequest request,
        Func<AgentClient, IFlowReplayEvidenceCapture?>? evidenceCaptureFactory = null,
        WorkflowRunLeaseHandoff? leaseHandoff = null)
    {
        if (!_isTargetCurrent())
        {
            return WorkflowRunStartResult.Conflict(
                "The requested agent instance is stale or no longer connected.");
        }

        return _runs.Start(
            request,
            _target,
            _isTargetCurrent,
            new WorkflowRunExecutionOptions
            {
                EvidenceCaptureFactory = evidenceCaptureFactory,
                ReproductionExpectation = request.ReproductionExpectation,
            },
            leaseHandoff);
    }

    public WorkflowRunAccessResult GetRunStatus(string runId, string? capabilityToken)
        => _runs.GetStatus(runId, capabilityToken);

    public WorkflowRunCancelResult CancelRun(string runId, string? capabilityToken)
        => _runs.Cancel(runId, capabilityToken);

    public WorkflowRunPriorSelectorResolutionResult GetPriorSelectorResolution(
        string sourceRunId,
        string sourceStepId)
        => _runs.GetPriorSelectorResolution(sourceRunId, sourceStepId);

    public InspectorArtifactTrustResult ImportArtifact(ReadOnlyMemory<byte> bytes, string? kind)
    {
        if (!ArtifactTrustImportKinds.IsKnown(kind))
        {
            return InspectorArtifactTrustResult.Failure(
                400,
                "An explicit supported artifact kind is required.");
        }

        var imported = _imports.Import(bytes, kind!, policy: null, verifiedProvenance: null, _cancellationToken);
        if (!imported.Ok || imported.Artifact is null)
        {
            return InspectorArtifactTrustResult.Failure(
                400,
                imported.Error ?? "The artifact could not be imported.");
        }

        var stored = _artifacts.Add(imported.Artifact);
        return !stored.Ok
            ? InspectorArtifactTrustResult.Failure(
                409,
                stored.Error ?? "The artifact could not be retained.")
            : InspectorArtifactTrustResult.Success(
                201,
                new ArtifactTrustRouteResponse
                {
                    Ok = true,
                    CapabilityToken = stored.CapabilityToken,
                    Status = stored.Status,
                });
    }

    public ArtifactTrustStoreReadResult GetArtifactStatus(string artifactId, string? capabilityToken)
        => _artifacts.GetStatus(artifactId, capabilityToken);

    public ArtifactTrustStoreReadResult GetArtifactProjection(string artifactId, string? capabilityToken)
        => _artifacts.GetSafeProjection(artifactId, capabilityToken);

    public ArtifactTrustStoreBindResult BindLocalReproduction(
        string artifactId,
        string? capabilityToken,
        MauiLocalReproductionExpectation current,
        string localRunId)
    {
        var local = _runs.GetLocalReproductionFacts(localRunId);
        if (!local.Ok || local.Facts is null)
        {
            return new ArtifactTrustStoreBindResult
            {
                StatusCode = 409,
                Error = local.Error ?? "The local run cannot establish reproduction facts.",
            };
        }

        return _artifacts.BindLocalReproduction(
            artifactId,
            capabilityToken,
            local.Facts,
            current);
    }

    public WorkflowRepairStoreResult ProposeRepair(MauiFlowRepairProposal proposal, bool agentOriginated = false)
        => _repairs.Propose(proposal, agentOriginated);

    public WorkflowRepairStoreResult GetRepair(string proposalId)
        => _repairs.Get(proposalId);

    public WorkflowRepairStoreResult PreviewRepair(string proposalId)
        => _repairs.Preview(proposalId);

    public WorkflowRepairStoreResult RejectRepair(string proposalId, string? reviewer, string? reasonCode)
        => _repairs.Reject(proposalId, reviewer, reasonCode);

    public WorkflowRepairGrantIssueResult IssueRepairGrant(WorkflowRepairGrantIssueRequest request)
        => _repairs.IssueGrant(request);

    public WorkflowRepairStoreResult RecordRepairValidation(
        string proposalId,
        string validationGrant,
        WorkflowRepairValidationRecord validation)
        => _repairs.RecordValidation(proposalId, validationGrant, validation);

    public WorkflowRepairStoreResult BeginRepairApply(
        string proposalId,
        string approvalGrant,
        WorkflowRepairGrantBinding binding)
        => _repairs.BeginApply(proposalId, approvalGrant, binding);

    public WorkflowRepairStoreResult CompleteRepairApply(
        string proposalId,
        WorkflowRepairApplyRecord apply)
        => _repairs.CompleteApply(proposalId, apply);

    public WorkflowRepairStoreResult RecordRepairVerification(
        string proposalId,
        IReadOnlyList<WorkflowRepairVerificationRun> verification)
        => _repairs.RecordVerification(proposalId, verification);

    public WorkflowRepairStoreResult BeginRepairRollback(
        string proposalId,
        string rollbackGrant,
        WorkflowRepairGrantBinding binding)
        => _repairs.BeginRollback(proposalId, rollbackGrant, binding);

    public WorkflowRepairStoreResult CompleteRepairRollback(
        string proposalId,
        WorkflowRepairRollbackRecord rollback)
        => _repairs.CompleteRollback(proposalId, rollback);

    public WorkflowXamlSourceStoreResult ProposeXamlSource(
        MauiXamlSourceProposal proposal,
        bool agentOriginated = false)
        => _sources.Propose(proposal, agentOriginated);

    public WorkflowXamlSourceStoreResult GetXamlSource(string proposalId)
        => _sources.Get(proposalId);

    public WorkflowXamlSourceStoreResult PreviewXamlSource(string proposalId)
        => _sources.Preview(proposalId);

    public WorkflowXamlSourceStoreResult RejectXamlSource(
        string proposalId,
        string? reviewer,
        string? reasonCode)
        => _sources.Reject(proposalId, reviewer, reasonCode);

    public WorkflowXamlSourceGrantIssueResult IssueXamlSourceGrant(
        WorkflowXamlSourceGrantIssueRequest request)
        => _sources.IssueGrant(request);

    public WorkflowXamlSourceStoreResult AwaitXamlSourceHostApply(
        string proposalId,
        WorkflowXamlSourceGrantBinding binding,
        WorkflowXamlSourceHostCapability capability)
        => _sources.AwaitHostApply(proposalId, binding, capability);

    public WorkflowXamlSourceStoreResult BeginXamlSourceApply(
        string proposalId,
        string grant,
        WorkflowXamlSourceGrantBinding binding,
        WorkflowXamlSourceHostCapability capability)
        => _sources.BeginApply(proposalId, grant, binding, capability);

    public WorkflowXamlSourceStoreResult CompleteXamlSourceApply(
        string proposalId,
        WorkflowXamlSourceApplyRecord apply)
        => _sources.CompleteApply(proposalId, apply);

    public WorkflowXamlSourceStoreResult RecordXamlSourceVerification(
        string proposalId,
        WorkflowXamlSourceVerificationRecord verification)
        => _sources.RecordVerification(proposalId, verification);

    public WorkflowXamlSourceStoreResult BeginXamlSourceRollback(
        string proposalId,
        string rollbackGrant,
        WorkflowXamlSourceGrantBinding binding,
        WorkflowXamlSourceHostCapability capability)
        => _sources.BeginRollback(proposalId, rollbackGrant, binding, capability);

    public WorkflowXamlSourceStoreResult CompleteXamlSourceRollback(
        string proposalId,
        WorkflowXamlSourceRollbackRecord rollback)
        => _sources.CompleteRollback(proposalId, rollback);

    public bool TryGetXamlSourceRollbackBytes(
        string proposalId,
        out byte[]? originalBytes,
        out string? expectedAppliedContentDigest)
        => _sources.TryGetRollbackBytes(proposalId, out originalBytes, out expectedAppliedContentDigest);

    public WorkflowCSharpSourceStoreResult ProposeCSharpSource(MauiCSharpSourceProposal proposal)
        => _csharpSources.Propose(proposal);

    public WorkflowCSharpSourceStoreResult GetCSharpSource(string proposalId)
        => _csharpSources.Get(proposalId);

    public WorkflowCSharpSourceStoreResult PreviewCSharpSource(string proposalId)
        => _csharpSources.Preview(proposalId);

    public WorkflowCSharpSourceStoreResult RejectCSharpSource(
        string proposalId,
        string? reviewer,
        string? reasonCode)
        => _csharpSources.Reject(proposalId, reviewer, reasonCode);

    public WorkflowCSharpSourceGrantIssueResult IssueCSharpSourceGrant(
        WorkflowCSharpSourceGrantIssueRequest request)
        => _csharpSources.IssueGrant(request);

    public WorkflowCSharpSourceStoreResult AwaitCSharpSourceHostApply(
        string proposalId,
        WorkflowCSharpSourceGrantBinding binding,
        WorkflowCSharpSourceHostCapability capability)
        => _csharpSources.AwaitHostApply(proposalId, binding, capability);

    public WorkflowCSharpSourceStoreResult BeginCSharpSourceHostApply(
        string proposalId,
        string grant,
        WorkflowCSharpSourceGrantBinding binding,
        WorkflowCSharpSourceHostCapability capability)
        => _csharpSources.BeginHostApply(proposalId, grant, binding, capability);

    public WorkflowCSharpSourceStoreResult CompleteCSharpSourceHostApply(
        string proposalId,
        WorkflowCSharpSourceHostApplyRecord apply)
        => _csharpSources.CompleteHostApply(proposalId, apply);

    public WorkflowCSharpSourceStoreResult RecordCSharpSourceVerification(
        string proposalId,
        WorkflowCSharpSourceVerificationRecord verification)
        => _csharpSources.RecordVerification(proposalId, verification);

    public WorkflowCSharpSourceStoreResult BeginCSharpSourceRollback(
        string proposalId,
        string grant,
        WorkflowCSharpSourceGrantBinding binding,
        WorkflowCSharpSourceHostCapability capability)
        => _csharpSources.BeginRollback(proposalId, grant, binding, capability);

    public WorkflowCSharpSourceStoreResult CompleteCSharpSourceRollback(
        string proposalId,
        WorkflowCSharpSourceRollbackRecord rollback)
        => _csharpSources.CompleteRollback(proposalId, rollback);
}

internal sealed class InspectorArtifactTrustResult
{
    public int StatusCode { get; private init; }
    public ArtifactTrustRouteResponse Response { get; private init; } = ArtifactTrustRouteResponse.Failure("Unknown error.");

    public static InspectorArtifactTrustResult Success(int statusCode, ArtifactTrustRouteResponse response)
        => new() { StatusCode = statusCode, Response = response };

    public static InspectorArtifactTrustResult Failure(int statusCode, string error)
        => Success(statusCode, ArtifactTrustRouteResponse.Failure(error));
}
