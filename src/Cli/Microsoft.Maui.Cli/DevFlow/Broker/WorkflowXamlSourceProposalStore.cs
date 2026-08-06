using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.Cli.DevFlow.Broker;

/// <summary>
/// Broker-owned lifecycle state for bounded XAML AutomationId source proposals. The store has no
/// source-writing capability: an explicit local host uses the XAML proposal service
/// after this store consumes a source-specific grant.
/// </summary>
internal sealed class WorkflowXamlSourceProposalStore
{
    private readonly object _gate = new();
    private readonly WorkflowXamlSourceProposalStoreOptions _options;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, SourceRecord> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SourceGrantRecord> _grants = new(StringComparer.Ordinal);

    internal WorkflowXamlSourceProposalStore(
        WorkflowXamlSourceProposalStoreOptions? options = null,
        TimeProvider? clock = null)
    {
        _options = options ?? new WorkflowXamlSourceProposalStoreOptions();
        _clock = clock ?? TimeProvider.System;
        if (_options.MaxProposals < 1 ||
            _options.MaxGrants < 1 ||
            _options.DefaultGrantLifetime <= TimeSpan.Zero ||
            _options.MaximumGrantLifetime < _options.DefaultGrantLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Source proposal store limits are invalid.");
        }
    }

    internal WorkflowXamlSourceStoreResult Propose(MauiXamlSourceProposal proposal, bool agentOriginated = false)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        lock (_gate)
        {
            PurgeExpiredGrantsLocked();
            if (!ValidateProposal(proposal, out var error))
                return WorkflowXamlSourceStoreResult.Failure("proposal-invalid", error!);

            var id = string.IsNullOrWhiteSpace(proposal.ProposalId)
                ? OpaqueId("xamlproposal")
                : proposal.ProposalId!;
            if (_records.TryGetValue(id, out var existing))
                return WorkflowXamlSourceStoreResult.Success(CreateSnapshot(existing));

            EvictTerminalLocked();
            if (_records.Count >= _options.MaxProposals)
            {
                return WorkflowXamlSourceStoreResult.Failure(
                    "proposal-capacity-exhausted",
                    "The broker source proposal store is full; resolve an existing proposal first.");
            }

            var now = _clock.GetUtcNow();
            var normalized = WithMetadata(
                proposal,
                id,
                revision: 1,
                state: MauiXamlSourceProposalStates.Proposed,
                createdAt: now,
                reviewer: null,
                grantDigest: null,
                expiresAt: null,
                apply: null,
                verification: null,
                rollback: null);
            var record = new SourceRecord(normalized, MauiXamlSourceProposalStates.Proposed, 1, agentOriginated, now);
            _records.Add(id, record);
            return WorkflowXamlSourceStoreResult.Success(CreateSnapshot(record));
        }
    }

    internal WorkflowXamlSourceStoreResult Get(string? proposalId)
    {
        lock (_gate)
        {
            RefreshExpiryLocked();
            return !TryGetLocked(proposalId, out var record, out var error)
                ? WorkflowXamlSourceStoreResult.Failure("proposal-not-found", error!)
                : WorkflowXamlSourceStoreResult.Success(CreateSnapshot(record!));
        }
    }

    internal WorkflowXamlSourceStoreResult Preview(string? proposalId)
    {
        lock (_gate)
        {
            RefreshExpiryLocked();
            if (!TryGetLocked(proposalId, out var record, out var error))
                return WorkflowXamlSourceStoreResult.Failure("proposal-not-found", error!);
            if (record!.State == MauiXamlSourceProposalStates.Proposed)
                TransitionLocked(record, MauiXamlSourceProposalStates.Previewed);
            if (record.State is not MauiXamlSourceProposalStates.Previewed and
                not MauiXamlSourceProposalStates.Approved and
                not MauiXamlSourceProposalStates.AwaitingHostApply and
                not MauiXamlSourceProposalStates.Applying and
                not MauiXamlSourceProposalStates.Applied and
                not MauiXamlSourceProposalStates.Verified and
                not MauiXamlSourceProposalStates.RollbackRequired and
                not MauiXamlSourceProposalStates.Reverted)
            {
                return WorkflowXamlSourceStoreResult.Failure(
                    "proposal-not-previewable",
                    $"Source proposal state '{record.State}' cannot be previewed.");
            }
            return WorkflowXamlSourceStoreResult.Success(CreateSnapshot(record));
        }
    }

    internal WorkflowXamlSourceStoreResult Reject(string? proposalId, string? reviewer, string? reasonCode)
    {
        lock (_gate)
        {
            RefreshExpiryLocked();
            if (!TryGetLocked(proposalId, out var record, out var error))
                return WorkflowXamlSourceStoreResult.Failure("proposal-not-found", error!);
            if (IsTerminal(record!.State))
            {
                return WorkflowXamlSourceStoreResult.Failure(
                    "proposal-terminal",
                    $"Source proposal state '{record.State}' cannot be rejected.");
            }
            record.Reviewer = Bounded(reviewer, 256);
            record.ReasonCode = Bounded(reasonCode, 128) ?? "rejected";
            TransitionLocked(record, MauiXamlSourceProposalStates.Rejected);
            return WorkflowXamlSourceStoreResult.Success(CreateSnapshot(record));
        }
    }

    internal WorkflowXamlSourceGrantIssueResult IssueGrant(WorkflowXamlSourceGrantIssueRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            RefreshExpiryLocked();
            PurgeExpiredGrantsLocked();
            if (!TryGetLocked(request.ProposalId, out var record, out var error))
                return WorkflowXamlSourceGrantIssueResult.Failure("proposal-not-found", error!);
            var found = record!;
            if (!request.HumanConfirmed || string.IsNullOrWhiteSpace(request.Reviewer))
            {
                return WorkflowXamlSourceGrantIssueResult.Failure(
                    "human-approval-required",
                    "A named human reviewer must explicitly approve a source proposal grant.");
            }
            if (!IsGrantAllowed(found, request.Kind, out error))
                return WorkflowXamlSourceGrantIssueResult.Failure("grant-state-invalid", error!);
            if (!BindingMatches(
                    found,
                    request.Binding,
                    againstAppliedContent: request.Kind == WorkflowXamlSourceGrantKinds.Rollback,
                    out error))
                return WorkflowXamlSourceGrantIssueResult.Failure("grant-binding-mismatch", error!);
            if (_grants.Count >= _options.MaxGrants)
            {
                return WorkflowXamlSourceGrantIssueResult.Failure(
                    "grant-capacity-exhausted",
                    "The broker source proposal grant store is full.");
            }

            var now = _clock.GetUtcNow();
            var expiry = request.ExpiresAt ?? now.Add(_options.DefaultGrantLifetime);
            if (expiry <= now || expiry > now.Add(_options.MaximumGrantLifetime))
            {
                return WorkflowXamlSourceGrantIssueResult.Failure(
                    "grant-expiry-invalid",
                    "The requested source proposal grant expiry is outside broker policy.");
            }

            if (request.Kind == WorkflowXamlSourceGrantKinds.Apply)
                TransitionLocked(found, MauiXamlSourceProposalStates.Approved);

            var secret = OpaqueSecret("xamlgrant");
            var digest = Hash(secret);
            var grant = new SourceGrantRecord(
                secret,
                digest,
                request.Kind!,
                found.Proposal.ProposalId!,
                found.Revision,
                found.Proposal.PatchDigest!,
                request.Kind == WorkflowXamlSourceGrantKinds.Rollback
                    ? found.AppliedContentDigest!
                    : found.Proposal.BaseContentDigest!,
                found.Proposal.Operation.SourceHash!,
                request.Binding!.ProjectIdentity!,
                request.Binding.FlowReferencesDigest!,
                request.Binding.HostKind!,
                Bounded(request.Reviewer, 256)!,
                expiry);
            _grants.Add(secret, grant);
            found.Reviewer = grant.Reviewer;
            found.GrantDigest = digest;
            found.ExpiresAt = expiry;

            return WorkflowXamlSourceGrantIssueResult.Success(
                secret,
                digest,
                expiry,
                CreateSnapshot(found));
        }
    }

    internal WorkflowXamlSourceStoreResult AwaitHostApply(
        string? proposalId,
        WorkflowXamlSourceGrantBinding? binding,
        WorkflowXamlSourceHostCapability? capability)
    {
        lock (_gate)
        {
            RefreshExpiryLocked();
            if (!TryGetLocked(proposalId, out var record, out var error))
                return WorkflowXamlSourceStoreResult.Failure("proposal-not-found", error!);
            if (record!.State != MauiXamlSourceProposalStates.Approved)
            {
                return WorkflowXamlSourceStoreResult.Failure(
                    "approval-required",
                    "A current source-specific human approval is required before host application.");
            }
            if (!BindingMatches(record, binding, againstAppliedContent: false, out error))
            {
                record.ReasonCode = "source-binding-stale";
                TransitionLocked(record, MauiXamlSourceProposalStates.Stale);
                return WorkflowXamlSourceStoreResult.Failure("source-binding-stale", error!, CreateSnapshot(record));
            }
            if (!SupportsExplicitLocalApply(capability, out error))
            {
                return WorkflowXamlSourceStoreResult.Failure("host-apply-unsupported", error!);
            }
            TransitionLocked(record, MauiXamlSourceProposalStates.AwaitingHostApply);
            return WorkflowXamlSourceStoreResult.Success(CreateSnapshot(record));
        }
    }

    internal WorkflowXamlSourceStoreResult BeginApply(
        string? proposalId,
        string? grant,
        WorkflowXamlSourceGrantBinding? binding,
        WorkflowXamlSourceHostCapability? capability)
    {
        lock (_gate)
        {
            RefreshExpiryLocked();
            if (!TryGetLocked(proposalId, out var record, out var error))
                return WorkflowXamlSourceStoreResult.Failure("proposal-not-found", error!);
            if (record!.State == MauiXamlSourceProposalStates.Stale)
            {
                return WorkflowXamlSourceStoreResult.Failure(
                    MauiXamlSourceProposalStates.Stale,
                    "The source proposal is stale and must be regenerated.");
            }
            if (record.State is not MauiXamlSourceProposalStates.Approved and
                not MauiXamlSourceProposalStates.AwaitingHostApply)
            {
                return WorkflowXamlSourceStoreResult.Failure(
                    "approval-required",
                    "A current human-issued source approval is required before applying.");
            }
            if (!BindingMatches(record, binding, againstAppliedContent: false, out error))
            {
                record.ReasonCode = "source-binding-stale";
                TransitionLocked(record, MauiXamlSourceProposalStates.Stale);
                return WorkflowXamlSourceStoreResult.Failure("source-binding-stale", error!, CreateSnapshot(record));
            }
            if (!SupportsExplicitLocalApply(capability, out error))
                return WorkflowXamlSourceStoreResult.Failure("host-apply-unsupported", error!);
            if (!GrantMatchesBinding(grant, binding, out error) ||
                !ConsumeGrantLocked(grant, record, WorkflowXamlSourceGrantKinds.Apply, out error))
            {
                return WorkflowXamlSourceStoreResult.Failure("approval-grant-invalid", error!);
            }

            record.HostKind = capability!.HostKind;
            TransitionLocked(record, MauiXamlSourceProposalStates.Applying);
            return WorkflowXamlSourceStoreResult.Success(CreateSnapshot(record));
        }
    }

    internal WorkflowXamlSourceStoreResult CompleteApply(
        string? proposalId,
        WorkflowXamlSourceApplyRecord apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        lock (_gate)
        {
            if (!TryGetLocked(proposalId, out var record, out var error))
                return WorkflowXamlSourceStoreResult.Failure("proposal-not-found", error!);
            if (record!.State != MauiXamlSourceProposalStates.Applying)
            {
                return WorkflowXamlSourceStoreResult.Failure(
                    "apply-state-invalid",
                    "The source proposal is not awaiting a local host apply result.");
            }
            if (!apply.Applied ||
                string.IsNullOrWhiteSpace(apply.AppliedContentDigest) ||
                apply.OriginalBytes is null ||
                string.IsNullOrWhiteSpace(apply.OriginalContentDigest))
            {
                record.ReasonCode = Bounded(apply.ErrorCode, 128) ?? "apply-failed";
                TransitionLocked(record, MauiXamlSourceProposalStates.Stale);
                return WorkflowXamlSourceStoreResult.Failure(
                    record.ReasonCode,
                    apply.Error ?? "The local host did not apply the source proposal.",
                    CreateSnapshot(record));
            }

            record.AppliedContentDigest = apply.AppliedContentDigest;
            record.OriginalContentDigest = apply.OriginalContentDigest;
            record.OriginalBytes = apply.OriginalBytes.ToArray();
            record.ApplyRunId = Bounded(apply.ApplyRunId, 256);
            TransitionLocked(record, MauiXamlSourceProposalStates.Applied);
            return WorkflowXamlSourceStoreResult.Success(CreateSnapshot(record));
        }
    }

    internal WorkflowXamlSourceStoreResult RecordVerification(
        string? proposalId,
        WorkflowXamlSourceVerificationRecord verification)
    {
        ArgumentNullException.ThrowIfNull(verification);
        lock (_gate)
        {
            if (!TryGetLocked(proposalId, out var record, out var error))
                return WorkflowXamlSourceStoreResult.Failure("proposal-not-found", error!);
            if (record!.State != MauiXamlSourceProposalStates.Applied)
            {
                return WorkflowXamlSourceStoreResult.Failure(
                    "verification-state-invalid",
                    "Only an applied source proposal can receive verification results.");
            }

            record.Verification = CloneVerification(verification);
            var requirements = CheckVerificationRequirements(record, record.Verification);
            if (requirements.Count > 0 &&
                requirements.All(static reason => string.Equals(reason, "pending-external-qa", StringComparison.Ordinal)))
            {
                // Apple targets unavailable on Windows are neither verified nor failed. Keep the
                // applied patch intact and make the external-QA requirement explicit; a rollback
                // is reserved for an actual failed build/remap/replay/oracle fact.
                record.Verification.Reasons = requirements;
                return WorkflowXamlSourceStoreResult.Success(CreateSnapshot(record));
            }
            if (requirements.Count == 0)
            {
                TransitionLocked(record, MauiXamlSourceProposalStates.Verified);
                return WorkflowXamlSourceStoreResult.Success(CreateSnapshot(record));
            }

            record.ReasonCode = "verification-failed";
            record.LastRecoveryState = MauiXamlSourceProposalStates.VerificationFailed;
            record.Verification.Reasons = requirements;
            TransitionLocked(record, MauiXamlSourceProposalStates.RollbackRequired);
            return WorkflowXamlSourceStoreResult.Success(CreateSnapshot(record));
        }
    }

    internal WorkflowXamlSourceStoreResult BeginRollback(
        string? proposalId,
        string? rollbackGrant,
        WorkflowXamlSourceGrantBinding? binding,
        WorkflowXamlSourceHostCapability? capability)
    {
        lock (_gate)
        {
            if (!TryGetLocked(proposalId, out var record, out var error))
                return WorkflowXamlSourceStoreResult.Failure("proposal-not-found", error!);
            if (record!.State != MauiXamlSourceProposalStates.RollbackRequired)
            {
                return WorkflowXamlSourceStoreResult.Failure(
                    "rollback-not-required",
                    "This source proposal is not in rollback-required state.");
            }
            if (record.OriginalBytes is null || string.IsNullOrWhiteSpace(record.AppliedContentDigest))
            {
                return WorkflowXamlSourceStoreResult.Failure(
                    "rollback-data-unavailable",
                    "The original source bytes are unavailable for an atomic rollback.");
            }
            if (!BindingMatches(record, binding, againstAppliedContent: true, out error))
                return WorkflowXamlSourceStoreResult.Failure("rollback-binding-mismatch", error!);
            if (!SupportsExplicitLocalApply(capability, out error))
                return WorkflowXamlSourceStoreResult.Failure("host-apply-unsupported", error!);
            if (!GrantMatchesBinding(rollbackGrant, binding, out error) ||
                !ConsumeGrantLocked(rollbackGrant, record, WorkflowXamlSourceGrantKinds.Rollback, out error))
            {
                return WorkflowXamlSourceStoreResult.Failure("rollback-grant-invalid", error!);
            }

            record.HostKind = capability!.HostKind;
            return WorkflowXamlSourceStoreResult.Success(CreateSnapshot(record));
        }
    }

    internal WorkflowXamlSourceStoreResult CompleteRollback(
        string? proposalId,
        WorkflowXamlSourceRollbackRecord rollback)
    {
        ArgumentNullException.ThrowIfNull(rollback);
        lock (_gate)
        {
            if (!TryGetLocked(proposalId, out var record, out var error))
                return WorkflowXamlSourceStoreResult.Failure("proposal-not-found", error!);
            if (record!.State != MauiXamlSourceProposalStates.RollbackRequired)
            {
                return WorkflowXamlSourceStoreResult.Failure(
                    "rollback-state-invalid",
                    "The source proposal is not awaiting rollback.");
            }

            record.RollbackContentDigest = Bounded(rollback.ContentDigest, 256);
            record.ReasonCode = Bounded(rollback.ErrorCode, 128);
            TransitionLocked(
                record,
                rollback.Reverted
                    ? MauiXamlSourceProposalStates.Reverted
                    : MauiXamlSourceProposalStates.RollbackFailed);
            return rollback.Reverted
                ? WorkflowXamlSourceStoreResult.Success(CreateSnapshot(record))
                : WorkflowXamlSourceStoreResult.Failure(
                    rollback.ErrorCode ?? "rollback-failed",
                    rollback.Error ?? "The local host rollback did not complete.",
                    CreateSnapshot(record));
        }
    }

    internal bool TryGetRollbackBytes(
        string? proposalId,
        out byte[]? originalBytes,
        out string? expectedAppliedContentDigest)
    {
        lock (_gate)
        {
            originalBytes = null;
            expectedAppliedContentDigest = null;
            if (!TryGetLocked(proposalId, out var record, out _) ||
                record!.OriginalBytes is null ||
                string.IsNullOrWhiteSpace(record.AppliedContentDigest))
            {
                return false;
            }
            originalBytes = record.OriginalBytes.ToArray();
            expectedAppliedContentDigest = record.AppliedContentDigest;
            return true;
        }
    }

    private static bool ValidateProposal(MauiXamlSourceProposal proposal, out string? error)
    {
        error = null;
        if (proposal.Schema != 1 ||
            proposal.Eligibility?.Eligible != true ||
            proposal.Operation is null ||
            proposal.Patch is null ||
            proposal.Element is null ||
            string.IsNullOrWhiteSpace(proposal.Operation.OperationId) ||
            string.IsNullOrWhiteSpace(proposal.Operation.FileRelativePath) ||
            string.IsNullOrWhiteSpace(proposal.Operation.SourceHash) ||
            string.IsNullOrWhiteSpace(proposal.Operation.SourceAnchor) ||
            !string.Equals(proposal.Operation.Attribute, "AutomationId", StringComparison.Ordinal) ||
            !MauiXamlAutomationIdGrammar.TryValidate(proposal.Operation.NewLiteral, out _) ||
            string.IsNullOrWhiteSpace(proposal.BaseContentDigest) ||
            string.IsNullOrWhiteSpace(proposal.PatchDigest) ||
            string.IsNullOrWhiteSpace(proposal.DiffDigest) ||
            string.IsNullOrWhiteSpace(proposal.Diff) ||
            proposal.Patch.Start is < 0 ||
            proposal.Patch.Length is < 0 ||
            proposal.Patch.Replacement is null ||
            !FixedEquals(proposal.BaseContentDigest, proposal.Patch.BeforeDigest))
        {
            error = "The source proposal must contain one eligible literal AutomationId operation, exact source bindings, and a deterministic patch.";
            return false;
        }
        return true;
    }

    private bool IsGrantAllowed(SourceRecord record, string? kind, out string? error)
    {
        error = null;
        switch (kind)
        {
            case WorkflowXamlSourceGrantKinds.Apply:
                if (record.State == MauiXamlSourceProposalStates.Previewed)
                    return true;
                break;
            case WorkflowXamlSourceGrantKinds.Rollback:
                if (record.State == MauiXamlSourceProposalStates.RollbackRequired)
                    return true;
                break;
            default:
                error = "The requested source proposal grant kind is not supported.";
                return false;
        }
        error ??= $"Source proposal state '{record.State}' cannot receive a {kind} grant.";
        return false;
    }

    private static bool BindingMatches(
        SourceRecord record,
        WorkflowXamlSourceGrantBinding? binding,
        bool againstAppliedContent,
        out string? error)
    {
        error = null;
        var expectedDigest = againstAppliedContent
            ? record.AppliedContentDigest
            : record.Proposal.BaseContentDigest;
        var expectedFlowRefs = ComputeFlowReferencesDigest(record.Proposal.AffectedFlows);
        if (binding is null ||
            string.IsNullOrWhiteSpace(binding.FileRelativePath) ||
            string.IsNullOrWhiteSpace(binding.BaseContentDigest) ||
            string.IsNullOrWhiteSpace(binding.SourceHash) ||
            string.IsNullOrWhiteSpace(binding.PatchDigest) ||
            string.IsNullOrWhiteSpace(binding.ProjectIdentity) ||
            string.IsNullOrWhiteSpace(binding.FlowReferencesDigest) ||
            string.IsNullOrWhiteSpace(binding.HostKind) ||
            !string.Equals(
                record.Proposal.Operation.FileRelativePath,
                binding.FileRelativePath.Replace('\\', '/'),
                StringComparison.Ordinal) ||
            !FixedEquals(expectedDigest, binding.BaseContentDigest) ||
            !FixedEquals(record.Proposal.Operation.SourceHash, binding.SourceHash) ||
            !FixedEquals(record.Proposal.PatchDigest, binding.PatchDigest) ||
            !FixedEquals(expectedFlowRefs, binding.FlowReferencesDigest))
        {
            error = "The source grant must be bound to the exact proposal patch, file hash, project identity, and affected flow references.";
            return false;
        }
        return true;
    }

    private bool GrantMatchesBinding(
        string? secret,
        WorkflowXamlSourceGrantBinding? binding,
        out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(secret) ||
            binding is null ||
            !_grants.TryGetValue(secret, out var grant) ||
            !string.Equals(grant.ProjectIdentity, binding.ProjectIdentity, StringComparison.Ordinal) ||
            !string.Equals(grant.HostKind, binding.HostKind, StringComparison.Ordinal) ||
            !FixedEquals(grant.FlowReferencesDigest, binding.FlowReferencesDigest))
        {
            error = "The source grant project, host, or affected-flow binding no longer matches.";
            return false;
        }
        return true;
    }

    private bool ConsumeGrantLocked(
        string? secret,
        SourceRecord record,
        string expectedKind,
        out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(secret) || !_grants.TryGetValue(secret, out var grant))
        {
            error = "The source grant is missing, stale, or already used.";
            return false;
        }
        var now = _clock.GetUtcNow();
        if (grant.Used || grant.ExpiresAt <= now)
        {
            _grants.Remove(secret);
            if (expectedKind == WorkflowXamlSourceGrantKinds.Apply &&
                (record.State is MauiXamlSourceProposalStates.Approved or MauiXamlSourceProposalStates.AwaitingHostApply))
            {
                record.ReasonCode = "approval-expired";
                TransitionLocked(record, MauiXamlSourceProposalStates.Stale);
            }
            error = grant.Used ? "The source grant was already used." : "The source grant expired.";
            return false;
        }
        if (grant.Kind != expectedKind ||
            !string.Equals(grant.ProposalId, record.Proposal.ProposalId, StringComparison.Ordinal) ||
            !FixedEquals(grant.PatchDigest, record.Proposal.PatchDigest) ||
            !FixedEquals(
                grant.ContentDigest,
                expectedKind == WorkflowXamlSourceGrantKinds.Rollback
                    ? record.AppliedContentDigest
                    : record.Proposal.BaseContentDigest) ||
            !FixedEquals(grant.SourceHash, record.Proposal.Operation.SourceHash))
        {
            error = "The source grant is not bound to the current proposal revision.";
            return false;
        }

        grant.Used = true;
        return true;
    }

    private static bool SupportsExplicitLocalApply(
        WorkflowXamlSourceHostCapability? capability,
        out string? error)
    {
        error = null;
        if (capability is null ||
            !capability.IsExplicitLocalHostAction ||
            !capability.CanApplySource ||
            string.IsNullOrWhiteSpace(capability.HostKind))
        {
            error = "This host cannot perform an explicit local source apply. Preview or download the patch instead.";
            return false;
        }
        if (capability.HostKind.Contains("canvas", StringComparison.OrdinalIgnoreCase))
        {
            error = "Canvas reports source apply unsupported; use a native local host.";
            return false;
        }
        return true;
    }

    private static List<string> CheckVerificationRequirements(
        SourceRecord record,
        WorkflowXamlSourceVerificationRecord verification)
    {
        var reasons = new List<string>();
        var platforms = record.Proposal.AffectedPlatforms;
        if (platforms.Count == 0)
            reasons.Add("affected-platforms-required");
        foreach (var platform in platforms)
        {
            var match = verification.Platforms.FirstOrDefault(result =>
                string.Equals(result.TargetFramework, platform.TargetFramework, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(result.Platform, platform.Platform, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                reasons.Add("platform-verification-missing");
                continue;
            }
            if (match.PendingExternalQa && IsAppleTarget(match))
            {
                reasons.Add("pending-external-qa");
                continue;
            }
            if (!IsBuildSatisfied(match))
                reasons.Add("platform-build-incomplete");
            if (!match.RuntimeRemapConfirmed)
                reasons.Add("runtime-remap-unconfirmed");
            if (!match.AutomationIdUnique)
                reasons.Add("runtime-uniqueness-unconfirmed");
            if (!match.ReplaySucceeded)
                reasons.Add("platform-replay-incomplete");
            if (!match.IndependentOracleSucceeded)
                reasons.Add("platform-oracle-incomplete");
        }
        if (!verification.AffectedFlowsReplayed)
            reasons.Add("affected-flows-not-replayed");
        if (!verification.IndependentOracleSucceeded)
            reasons.Add("independent-oracle-failed");
        return reasons.Distinct(StringComparer.Ordinal).Take(32).ToList();
    }

    private static bool IsBuildSatisfied(WorkflowXamlSourcePlatformVerificationResult result)
        => result.BuildSucceeded;

    private static bool IsAppleTarget(WorkflowXamlSourcePlatformVerificationResult result)
        => (result.Platform?.Contains("ios", StringComparison.OrdinalIgnoreCase) == true ||
            result.Platform?.Contains("maccatalyst", StringComparison.OrdinalIgnoreCase) == true ||
            result.Platform?.Contains("macos", StringComparison.OrdinalIgnoreCase) == true ||
            result.TargetFramework?.Contains("-ios", StringComparison.OrdinalIgnoreCase) == true ||
            result.TargetFramework?.Contains("-maccatalyst", StringComparison.OrdinalIgnoreCase) == true ||
            result.TargetFramework?.Contains("-macos", StringComparison.OrdinalIgnoreCase) == true);

    private void RefreshExpiryLocked()
    {
        var now = _clock.GetUtcNow();
        foreach (var record in _records.Values)
        {
            if ((record.State is MauiXamlSourceProposalStates.Approved or MauiXamlSourceProposalStates.AwaitingHostApply) &&
                record.ExpiresAt is { } expiresAt &&
                expiresAt <= now)
            {
                record.ReasonCode = "approval-expired";
                TransitionLocked(record, MauiXamlSourceProposalStates.Stale);
            }
        }
    }

    private void PurgeExpiredGrantsLocked()
    {
        var now = _clock.GetUtcNow();
        foreach (var key in _grants
                     .Where(pair => pair.Value.ExpiresAt <= now || pair.Value.Used)
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            _grants.Remove(key);
        }
    }

    private void EvictTerminalLocked()
    {
        while (_records.Count >= _options.MaxProposals)
        {
            var candidate = _records.Values
                .Where(static record => IsTerminal(record.State))
                .OrderBy(static record => record.UpdatedAt)
                .FirstOrDefault();
            if (candidate is null)
                return;
            _records.Remove(candidate.Proposal.ProposalId!);
        }
    }

    private static bool IsTerminal(string state) => state is
        MauiXamlSourceProposalStates.Rejected or
        MauiXamlSourceProposalStates.Stale or
        MauiXamlSourceProposalStates.Verified or
        MauiXamlSourceProposalStates.Reverted or
        MauiXamlSourceProposalStates.RollbackFailed;

    private void TransitionLocked(SourceRecord record, string state)
    {
        record.State = state;
        record.Revision++;
        record.UpdatedAt = _clock.GetUtcNow();
    }

    private bool TryGetLocked(string? proposalId, out SourceRecord? record, out string? error)
    {
        record = null;
        error = null;
        if (string.IsNullOrWhiteSpace(proposalId) || !_records.TryGetValue(proposalId, out record))
        {
            error = "The source proposal was not found.";
            return false;
        }
        return true;
    }

    private static WorkflowXamlSourceProposalSnapshot CreateSnapshot(SourceRecord record) => new()
    {
        Proposal = WithMetadata(
            record.Proposal,
            record.Proposal.ProposalId!,
            record.Revision,
            record.State,
            record.Proposal.CreatedAt ?? record.CreatedAt,
            record.Reviewer,
            record.GrantDigest,
            record.ExpiresAt,
            new MauiXamlSourceApplyState
            {
                State = record.State,
                HostKind = record.HostKind,
                AppliedAt = record.State is MauiXamlSourceProposalStates.Applied or
                    MauiXamlSourceProposalStates.Verified or
                    MauiXamlSourceProposalStates.RollbackRequired or
                    MauiXamlSourceProposalStates.Reverted
                    ? record.UpdatedAt
                    : null,
                AppliedContentDigest = record.AppliedContentDigest,
                ReasonCode = record.ReasonCode,
            },
            new MauiXamlSourceVerificationState
            {
                State = record.LastRecoveryState ?? record.State,
                VerificationRunIds = record.Verification?.VerificationRunIds.ToList() ?? [],
                Platforms = record.Verification?.Platforms
                    .Select(static platform => new MauiXamlSourcePlatformVerification
                    {
                        Platform = platform.Platform,
                        TargetFramework = platform.TargetFramework,
                        BuildState = platform.PendingExternalQa
                            ? "pending-external-qa"
                            : platform.BuildSucceeded ? "succeeded" : "failed-or-pending",
                        RuntimeRemapState = platform.PendingExternalQa
                            ? "pending-external-qa"
                            : platform.RuntimeRemapConfirmed ? "confirmed" : "unconfirmed",
                        UniquenessState = platform.PendingExternalQa
                            ? "pending-external-qa"
                            : platform.AutomationIdUnique ? "confirmed" : "unconfirmed",
                        ReplayState = platform.PendingExternalQa
                            ? "pending-external-qa"
                            : platform.ReplaySucceeded ? "succeeded" : "unconfirmed",
                        OracleState = platform.PendingExternalQa
                            ? "pending-external-qa"
                            : platform.IndependentOracleSucceeded ? "succeeded" : "unconfirmed",
                        ReasonCode = platform.ReasonCode,
                    })
                    .ToList() ?? [],
                CompletedAt = record.Verification?.CompletedAt,
                ReasonCode = record.ReasonCode,
            },
            new MauiXamlSourceRollbackState
            {
                State = record.State is MauiXamlSourceProposalStates.Reverted or
                    MauiXamlSourceProposalStates.RollbackFailed
                    ? record.State
                    : null,
                RevertedAt = record.State is MauiXamlSourceProposalStates.Reverted or
                    MauiXamlSourceProposalStates.RollbackFailed
                    ? record.UpdatedAt
                    : null,
                ContentDigest = record.RollbackContentDigest,
                ReasonCode = record.ReasonCode,
            }),
        State = record.State,
        Revision = record.Revision,
        AgentOriginated = record.AgentOriginated,
        Reviewer = record.Reviewer,
        GrantDigest = record.GrantDigest,
        ExpiresAt = record.ExpiresAt,
        ReasonCode = record.ReasonCode,
        LastRecoveryState = record.LastRecoveryState,
        AppliedContentDigest = record.AppliedContentDigest,
        OriginalContentDigest = record.OriginalContentDigest,
        HostKind = record.HostKind,
        Verification = record.Verification is null ? null : CloneVerification(record.Verification),
    };

    private static MauiXamlSourceProposal WithMetadata(
        MauiXamlSourceProposal source,
        string id,
        int revision,
        string state,
        DateTimeOffset createdAt,
        string? reviewer,
        string? grantDigest,
        DateTimeOffset? expiresAt,
        MauiXamlSourceApplyState? apply,
        MauiXamlSourceVerificationState? verification,
        MauiXamlSourceRollbackState? rollback)
    {
        var node = JsonSerializer.SerializeToNode(source, MauiTestingJsonContext.Default.MauiXamlSourceProposal)!.AsObject();
        node["proposalId"] = id;
        node["revision"] = revision;
        node["state"] = state;
        node["createdAt"] = createdAt;
        node["approval"] = reviewer is null && expiresAt is null
            ? null
            : JsonSerializer.SerializeToNode(new MauiXamlSourceApproval
            {
                State = state,
                Reviewer = reviewer,
                ExpiresAt = expiresAt,
                GrantDigest = grantDigest,
            }, MauiTestingJsonContext.Default.MauiXamlSourceApproval);
        node["apply"] = JsonSerializer.SerializeToNode(apply, MauiTestingJsonContext.Default.MauiXamlSourceApplyState);
        node["verification"] = JsonSerializer.SerializeToNode(verification, MauiTestingJsonContext.Default.MauiXamlSourceVerificationState);
        node["rollback"] = JsonSerializer.SerializeToNode(rollback, MauiTestingJsonContext.Default.MauiXamlSourceRollbackState);
        return node.Deserialize(MauiTestingJsonContext.Default.MauiXamlSourceProposal)
            ?? throw new InvalidOperationException("A source proposal could not be normalized.");
    }

    private static WorkflowXamlSourceVerificationRecord CloneVerification(
        WorkflowXamlSourceVerificationRecord value) => new()
        {
            Platforms = value.Platforms
                .Take(16)
                .Select(platform => new WorkflowXamlSourcePlatformVerificationResult
                {
                    Platform = Bounded(platform.Platform, 64),
                    TargetFramework = Bounded(platform.TargetFramework, 128),
                    BuildSucceeded = platform.BuildSucceeded,
                    PendingExternalQa = platform.PendingExternalQa,
                    RuntimeRemapConfirmed = platform.RuntimeRemapConfirmed,
                    AutomationIdUnique = platform.AutomationIdUnique,
                    ReplaySucceeded = platform.ReplaySucceeded,
                    IndependentOracleSucceeded = platform.IndependentOracleSucceeded,
                    ReasonCode = Bounded(platform.ReasonCode, 128),
                })
                .ToList(),
            AffectedFlowsReplayed = value.AffectedFlowsReplayed,
            IndependentOracleSucceeded = value.IndependentOracleSucceeded,
            VerificationRunIds = value.VerificationRunIds
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Take(64)
                .ToList(),
            CompletedAt = value.CompletedAt ?? DateTimeOffset.UtcNow,
            Reasons = value.Reasons
                .Where(static reason => !string.IsNullOrWhiteSpace(reason))
                .Take(32)
                .ToList(),
        };

    internal static string ComputeFlowReferencesDigest(
        IReadOnlyList<MauiXamlSourceFlowFollowUp>? flows)
    {
        var canonical = string.Join("\n", (flows ?? [])
            .OrderBy(flow => flow.FlowPath, StringComparer.Ordinal)
            .ThenBy(flow => flow.FlowId, StringComparer.Ordinal)
            .Select(flow => string.Join("|",
                flow.FlowPath ?? string.Empty,
                flow.FlowId ?? string.Empty,
                flow.FlowDigest ?? string.Empty,
                string.Join(",", flow.StepIds.OrderBy(static step => step, StringComparer.Ordinal)))));
        return Hash(canonical);
    }

    private static string OpaqueId(string prefix)
        => prefix + "_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static string OpaqueSecret(string prefix)
        => prefix + "_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    private static string Hash(string? value)
        => "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty))).ToLowerInvariant();

    private static bool FixedEquals(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) &&
           !string.IsNullOrWhiteSpace(right) &&
           CryptographicOperations.FixedTimeEquals(
               Encoding.UTF8.GetBytes(left),
               Encoding.UTF8.GetBytes(right));

    private static string? Bounded(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : null;
    }

    private sealed class SourceRecord
    {
        public SourceRecord(
            MauiXamlSourceProposal proposal,
            string state,
            int revision,
            bool agentOriginated,
            DateTimeOffset createdAt)
        {
            Proposal = proposal;
            State = state;
            Revision = revision;
            AgentOriginated = agentOriginated;
            CreatedAt = createdAt;
            UpdatedAt = createdAt;
        }

        public MauiXamlSourceProposal Proposal { get; }
        public string State { get; set; }
        public int Revision { get; set; }
        public bool AgentOriginated { get; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset UpdatedAt { get; set; }
        public string? Reviewer { get; set; }
        public string? GrantDigest { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public string? ReasonCode { get; set; }
        public string? LastRecoveryState { get; set; }
        public string? HostKind { get; set; }
        public string? AppliedContentDigest { get; set; }
        public string? OriginalContentDigest { get; set; }
        public byte[]? OriginalBytes { get; set; }
        public string? RollbackContentDigest { get; set; }
        public string? ApplyRunId { get; set; }
        public WorkflowXamlSourceVerificationRecord? Verification { get; set; }
    }

    private sealed class SourceGrantRecord
    {
        public SourceGrantRecord(
            string secret,
            string digest,
            string kind,
            string proposalId,
            int proposalRevision,
            string patchDigest,
            string contentDigest,
            string sourceHash,
            string projectIdentity,
            string flowReferencesDigest,
            string hostKind,
            string reviewer,
            DateTimeOffset expiresAt)
        {
            Secret = secret;
            Digest = digest;
            Kind = kind;
            ProposalId = proposalId;
            ProposalRevision = proposalRevision;
            PatchDigest = patchDigest;
            ContentDigest = contentDigest;
            SourceHash = sourceHash;
            ProjectIdentity = projectIdentity;
            FlowReferencesDigest = flowReferencesDigest;
            HostKind = hostKind;
            Reviewer = reviewer;
            ExpiresAt = expiresAt;
        }

        public string Secret { get; }
        public string Digest { get; }
        public string Kind { get; }
        public string ProposalId { get; }
        public int ProposalRevision { get; }
        public string PatchDigest { get; }
        public string ContentDigest { get; }
        public string SourceHash { get; }
        public string ProjectIdentity { get; }
        public string FlowReferencesDigest { get; }
        public string HostKind { get; }
        public string Reviewer { get; }
        public DateTimeOffset ExpiresAt { get; }
        public bool Used { get; set; }
    }
}

internal sealed class WorkflowXamlSourceProposalStoreOptions
{
    public int MaxProposals { get; init; } = 128;
    public int MaxGrants { get; init; } = 256;
    public TimeSpan DefaultGrantLifetime { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan MaximumGrantLifetime { get; init; } = TimeSpan.FromMinutes(30);
}

internal static class WorkflowXamlSourceGrantKinds
{
    public const string Apply = "apply";
    public const string Rollback = "rollback";
}

internal sealed class WorkflowXamlSourceGrantBinding
{
    public string? FileRelativePath { get; init; }
    public string? BaseContentDigest { get; init; }
    public string? SourceHash { get; init; }
    public string? PatchDigest { get; init; }
    public string? ProjectIdentity { get; init; }
    public string? FlowReferencesDigest { get; init; }
    public string? HostKind { get; init; }
}

internal sealed class WorkflowXamlSourceHostCapability
{
    public string? HostKind { get; init; }
    public bool CanOpenNativeDiff { get; init; }
    public bool CanDownloadPatch { get; init; }
    public bool CanApplySource { get; init; }
    public bool IsExplicitLocalHostAction { get; init; }
}

internal sealed class WorkflowXamlSourceGrantIssueRequest
{
    public string? ProposalId { get; init; }
    public string? Kind { get; init; }
    public string? Reviewer { get; init; }
    public bool HumanConfirmed { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public WorkflowXamlSourceGrantBinding? Binding { get; init; }
}

internal sealed class WorkflowXamlSourceGrantIssueResult
{
    public bool Ok { get; private init; }
    public string? Grant { get; private init; }
    public string? GrantDigest { get; private init; }
    public DateTimeOffset? ExpiresAt { get; private init; }
    public WorkflowXamlSourceProposalSnapshot? Proposal { get; private init; }
    public string? Code { get; private init; }
    public string? Error { get; private init; }

    public static WorkflowXamlSourceGrantIssueResult Success(
        string grant,
        string digest,
        DateTimeOffset expiresAt,
        WorkflowXamlSourceProposalSnapshot proposal) => new()
        {
            Ok = true,
            Grant = grant,
            GrantDigest = digest,
            ExpiresAt = expiresAt,
            Proposal = proposal,
        };

    public static WorkflowXamlSourceGrantIssueResult Failure(string code, string error) => new()
    {
        Code = code,
        Error = error,
    };
}

internal sealed class WorkflowXamlSourceApplyRecord
{
    public bool Applied { get; init; }
    public string? AppliedContentDigest { get; init; }
    public byte[]? OriginalBytes { get; init; }
    public string? OriginalContentDigest { get; init; }
    public string? ApplyRunId { get; init; }
    public string? ErrorCode { get; init; }
    public string? Error { get; init; }
}

internal sealed class WorkflowXamlSourcePlatformVerificationResult
{
    public string? Platform { get; init; }
    public string? TargetFramework { get; init; }
    public bool BuildSucceeded { get; init; }
    public bool PendingExternalQa { get; init; }
    public bool RuntimeRemapConfirmed { get; init; }
    public bool AutomationIdUnique { get; init; }
    public bool ReplaySucceeded { get; init; }
    public bool IndependentOracleSucceeded { get; init; }
    public string? ReasonCode { get; init; }
}

internal sealed class WorkflowXamlSourceVerificationRecord
{
    public List<WorkflowXamlSourcePlatformVerificationResult> Platforms { get; init; } = [];
    public bool AffectedFlowsReplayed { get; init; }
    public bool IndependentOracleSucceeded { get; init; }
    public List<string> VerificationRunIds { get; init; } = [];
    public DateTimeOffset? CompletedAt { get; init; }
    public List<string> Reasons { get; set; } = [];
}

internal sealed class WorkflowXamlSourceRollbackRecord
{
    public bool Reverted { get; init; }
    public string? ContentDigest { get; init; }
    public string? ErrorCode { get; init; }
    public string? Error { get; init; }
}

internal sealed class WorkflowXamlSourceProposalSnapshot
{
    public MauiXamlSourceProposal Proposal { get; init; } = new();
    public string State { get; init; } = "";
    public int Revision { get; init; }
    public bool AgentOriginated { get; init; }
    public string? Reviewer { get; init; }
    public string? GrantDigest { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? ReasonCode { get; init; }
    public string? LastRecoveryState { get; init; }
    public string? AppliedContentDigest { get; init; }
    public string? OriginalContentDigest { get; init; }
    public string? HostKind { get; init; }
    public WorkflowXamlSourceVerificationRecord? Verification { get; init; }
}

internal sealed class WorkflowXamlSourceStoreResult
{
    public bool Ok { get; private init; }
    public WorkflowXamlSourceProposalSnapshot? Proposal { get; private init; }
    public string? Code { get; private init; }
    public string? Error { get; private init; }

    public static WorkflowXamlSourceStoreResult Success(WorkflowXamlSourceProposalSnapshot proposal)
        => new() { Ok = true, Proposal = proposal };

    public static WorkflowXamlSourceStoreResult Failure(
        string code,
        string error,
        WorkflowXamlSourceProposalSnapshot? proposal = null)
        => new() { Code = code, Error = error, Proposal = proposal };
}

/// <summary>
/// Append-only redacted history for source proposals. It intentionally records hashes and state
/// only: source excerpts, patch replacements, prompts, IDs, and reviewer names never leave the
/// in-memory proposal into this audit file.
/// </summary>
internal sealed class WorkflowXamlSourceHistoryStore
{
    internal const int MaxEntries = 256;
    internal const int MaxLineBytes = 16_384;
    internal const int MaxBytes = 1_048_576;
    private static readonly ConcurrentDictionary<string, object> Gates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string _projectRoot;

    internal WorkflowXamlSourceHistoryStore(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        _projectRoot = Path.GetFullPath(projectRoot);
    }

    internal WorkflowXamlSourceHistoryAppendResult Append(
        WorkflowXamlSourceProposalSnapshot snapshot,
        string? state = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var proposal = snapshot.Proposal;
        if (!IsSafeProposalId(proposal.ProposalId))
        {
            return WorkflowXamlSourceHistoryAppendResult.Failure(
                "source-history-invalid",
                "The source proposal identity is invalid for a history path.");
        }
        if (!TryGetHistoryPath(proposal.ProposalId!, out var historyPath, out var error))
            return WorkflowXamlSourceHistoryAppendResult.Failure("source-history-invalid", error!);

        lock (Gates.GetOrAdd(historyPath!, static _ => new object()))
        {
            try
            {
                var existing = File.Exists(historyPath) ? File.ReadAllText(historyPath!) : string.Empty;
                if (Encoding.UTF8.GetByteCount(existing) > MaxBytes)
                {
                    return WorkflowXamlSourceHistoryAppendResult.Failure(
                        "source-history-invalid",
                        "The bounded source proposal history is too large to append safely.");
                }
                var lines = existing.ReplaceLineEndings("\n")
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .ToList();
                if (lines.Count >= MaxEntries)
                {
                    return WorkflowXamlSourceHistoryAppendResult.Failure(
                        "source-history-invalid",
                        "The bounded source proposal history has reached its entry limit.");
                }
                var previous = ValidateChain(lines, out error);
                if (error is not null)
                    return WorkflowXamlSourceHistoryAppendResult.Failure("source-history-invalid", error);

                var node = CreateSafeNode(snapshot, state ?? snapshot.State, lines.Count + 1, previous);
                var material = Canonicalize(node);
                node["hash"] = "sha256:" + Hash(material);
                var line = Canonicalize(node);
                if (Encoding.UTF8.GetByteCount(line) > MaxLineBytes)
                {
                    return WorkflowXamlSourceHistoryAppendResult.Failure(
                        "source-history-invalid",
                        "The source proposal history entry exceeds its bounded size.");
                }

                var content = existing;
                if (!string.IsNullOrEmpty(content) && !content.EndsWith('\n'))
                    content += "\n";
                content += line + "\n";
                if (Encoding.UTF8.GetByteCount(content) > MaxBytes)
                {
                    return WorkflowXamlSourceHistoryAppendResult.Failure(
                        "source-history-invalid",
                        "Appending the source proposal history entry would exceed its size cap.");
                }

                AtomicWriteText(historyPath!, content);
                return WorkflowXamlSourceHistoryAppendResult.Success(historyPath!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return WorkflowXamlSourceHistoryAppendResult.Failure(
                    "source-history-write-failed",
                    "The redacted source proposal history could not be written.");
            }
        }
    }

    private bool TryGetHistoryPath(string proposalId, out string? path, out string? error)
    {
        path = null;
        error = null;
        try
        {
            if (!Directory.Exists(_projectRoot) || IsReparsePoint(_projectRoot))
            {
                error = "The project root is not a safe local directory.";
                return false;
            }
            var root = Path.Combine(_projectRoot, "maui-tests", ".devflow", "source-proposals");
            Directory.CreateDirectory(root);
            if (PathContainsReparsePoint(_projectRoot, root))
            {
                error = "The source proposal history directory cannot use a symbolic link or reparse point.";
                return false;
            }
            var candidate = Path.GetFullPath(Path.Combine(root, proposalId + ".history.jsonl"));
            if (!IsUnderRoot(candidate, root))
            {
                error = "The source proposal history path escapes its trusted root.";
                return false;
            }
            path = candidate;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = "The source proposal history root could not be resolved safely.";
            return false;
        }
    }

    private static JsonObject CreateSafeNode(
        WorkflowXamlSourceProposalSnapshot snapshot,
        string state,
        int sequence,
        string? previousHash)
    {
        var proposal = snapshot.Proposal;
        return new JsonObject
        {
            ["schema"] = 1,
            ["sequence"] = sequence,
            ["at"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["previousHash"] = previousHash,
            ["proposalId"] = Token(proposal.ProposalId),
            ["proposalRevision"] = snapshot.Revision,
            ["state"] = SafeState(state),
            ["operationId"] = Token(proposal.Operation.OperationId),
            ["fileDigest"] = Token(proposal.Operation.FileRelativePath),
            ["sourceHash"] = SafeDigest(proposal.Operation.SourceHash),
            ["sourceAnchor"] = SafeDigest(proposal.Operation.SourceAnchor),
            ["baseContentDigest"] = SafeDigest(proposal.BaseContentDigest),
            ["patchDigest"] = SafeDigest(proposal.PatchDigest),
            ["diffDigest"] = SafeDigest(proposal.DiffDigest),
            ["flowReferencesDigest"] = WorkflowXamlSourceProposalStore.ComputeFlowReferencesDigest(proposal.AffectedFlows),
            ["reviewerDigest"] = Token(snapshot.Reviewer),
            ["grantDigest"] = SafeDigest(snapshot.GrantDigest),
            ["applyContentDigest"] = SafeDigest(snapshot.AppliedContentDigest),
            ["originalContentDigest"] = SafeDigest(snapshot.OriginalContentDigest),
            ["reasonCode"] = Token(snapshot.ReasonCode),
            ["verificationRunIds"] = SafeTokens(snapshot.Verification?.VerificationRunIds),
        };
    }

    private static string? ValidateChain(IReadOnlyList<string> lines, out string? error)
    {
        error = null;
        string? previous = null;
        foreach (var line in lines)
        {
            try
            {
                var node = JsonNode.Parse(line) as JsonObject;
                var hash = node?["hash"]?.GetValue<string>();
                var declaredPrevious = node?["previousHash"]?.GetValue<string>();
                if (node is null ||
                    !string.Equals(previous, declaredPrevious, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(hash))
                {
                    error = "The existing source proposal history hash chain is invalid.";
                    return null;
                }
                node.Remove("hash");
                var expected = "sha256:" + Hash(Canonicalize(node));
                if (!FixedEquals(hash, expected))
                {
                    error = "The existing source proposal history hash chain failed verification.";
                    return null;
                }
                previous = hash;
            }
            catch (JsonException)
            {
                error = "The existing source proposal history contains invalid JSON.";
                return null;
            }
            catch (InvalidOperationException)
            {
                error = "The existing source proposal history has an invalid safe projection.";
                return null;
            }
        }
        return previous;
    }

    private static void AtomicWriteText(string path, string content)
    {
        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var backup = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.bak");
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (File.Exists(path))
            {
                File.Replace(temp, path, backup, ignoreMetadataErrors: true);
                TryDelete(backup);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            TryDelete(temp);
            TryDelete(backup);
        }
    }

    private static string Canonicalize(JsonObject node)
        => node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string? Token(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : "sha256:" + Hash(value.Trim());

    private static string? SafeDigest(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var candidate = value.Trim();
        var hex = candidate.StartsWith("sha256:", StringComparison.Ordinal) ? candidate[7..] : candidate;
        return hex.Length == 64 && hex.All(Uri.IsHexDigit)
            ? candidate.StartsWith("sha256:", StringComparison.Ordinal) ? candidate : "sha256:" + candidate
            : Token(candidate);
    }

    private static JsonArray SafeTokens(IEnumerable<string>? values)
    {
        var result = new JsonArray();
        foreach (var value in (values ?? [])
                     .Where(static value => !string.IsNullOrWhiteSpace(value))
                     .Take(64)
                     .Select(Token))
        {
            result.Add(value);
        }
        return result;
    }

    private static string SafeState(string value)
    {
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            MauiXamlSourceProposalStates.Proposed,
            MauiXamlSourceProposalStates.Previewed,
            MauiXamlSourceProposalStates.Approved,
            MauiXamlSourceProposalStates.AwaitingHostApply,
            MauiXamlSourceProposalStates.Applying,
            MauiXamlSourceProposalStates.Applied,
            MauiXamlSourceProposalStates.VerificationFailed,
            MauiXamlSourceProposalStates.RollbackRequired,
            MauiXamlSourceProposalStates.Verified,
            MauiXamlSourceProposalStates.Stale,
            MauiXamlSourceProposalStates.Rejected,
            MauiXamlSourceProposalStates.Reverted,
            MauiXamlSourceProposalStates.RollbackFailed,
        };
        return known.Contains(value) ? value : Token(value)!;
    }

    private static bool IsSafeProposalId(string? value)
        => value is { Length: > 0 and <= 128 } &&
           value.All(static ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_');

    private static bool IsUnderRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) &&
               !string.Equals(relative, "..", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool PathContainsReparsePoint(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (IsReparsePoint(current))
                return true;
        }
        return false;
    }

    private static bool IsReparsePoint(string path)
    {
        var info = Directory.Exists(path)
            ? (FileSystemInfo)new DirectoryInfo(path)
            : new FileInfo(path);
        info.Refresh();
        return (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null;
    }

    private static bool FixedEquals(string left, string right)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class WorkflowXamlSourceHistoryAppendResult
{
    public bool Ok { get; private init; }
    public string? HistoryPath { get; private init; }
    public string? Code { get; private init; }
    public string? Error { get; private init; }

    public static WorkflowXamlSourceHistoryAppendResult Success(string path)
        => new() { Ok = true, HistoryPath = path };

    public static WorkflowXamlSourceHistoryAppendResult Failure(string code, string error)
        => new() { Code = code, Error = error };
}
