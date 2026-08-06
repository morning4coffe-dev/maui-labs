using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.Cli.DevFlow.Broker;

/// <summary>
/// Broker-owned lifecycle for reviewed C# source proposals. This store cannot read, write, or
/// revert source files. It records only grants and IDE host acknowledgments bound to exact hashes.
/// </summary>
internal sealed class WorkflowCSharpSourceProposalStore
{
    private readonly object _gate = new();
    private readonly WorkflowCSharpSourceProposalStoreOptions _options;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, Record> _records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GrantRecord> _grants = new(StringComparer.Ordinal);

    internal WorkflowCSharpSourceProposalStore(
        WorkflowCSharpSourceProposalStoreOptions? options = null,
        TimeProvider? clock = null)
    {
        _options = options ?? new WorkflowCSharpSourceProposalStoreOptions();
        _clock = clock ?? TimeProvider.System;
        if (_options.MaxProposals < 1 ||
            _options.MaxGrants < 1 ||
            _options.DefaultGrantLifetime <= TimeSpan.Zero ||
            _options.MaximumGrantLifetime < _options.DefaultGrantLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "C# source proposal store limits are invalid.");
        }
    }

    internal WorkflowCSharpSourceStoreResult Propose(MauiCSharpSourceProposal proposal, bool agentOriginated = false)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        lock (_gate)
        {
            PurgeExpiredGrantsLocked();
            if (agentOriginated)
            {
                return WorkflowCSharpSourceStoreResult.Failure(
                    "agent-source-proposal-forbidden",
                    "Agent MCP cannot create, approve, apply, or write C# source proposals.");
            }
            if (!ValidateProposal(proposal, out var error))
                return WorkflowCSharpSourceStoreResult.Failure("proposal-invalid", error!);

            var id = string.IsNullOrWhiteSpace(proposal.ProposalId)
                ? OpaqueId("csharpproposal")
                : proposal.ProposalId!;
            if (_records.TryGetValue(id, out var existing))
                return WorkflowCSharpSourceStoreResult.Success(CreateSnapshot(existing));

            EvictTerminalLocked();
            if (_records.Count >= _options.MaxProposals)
            {
                return WorkflowCSharpSourceStoreResult.Failure(
                    "proposal-capacity-exhausted",
                    "The broker C# source proposal store is full; resolve a terminal proposal first.");
            }

            var now = _clock.GetUtcNow();
            var normalized = WithMetadata(
                proposal,
                id,
                revision: 1,
                state: MauiCSharpSourceProposalStates.Proposed,
                createdAt: now,
                reviewer: null,
                grantDigest: null,
                expiresAt: null,
                apply: null,
                verification: null,
                rollback: null);
            var record = new Record(normalized, MauiCSharpSourceProposalStates.Proposed, 1, now);
            _records.Add(id, record);
            return WorkflowCSharpSourceStoreResult.Success(CreateSnapshot(record));
        }
    }

    internal WorkflowCSharpSourceStoreResult Get(string? proposalId)
    {
        lock (_gate)
        {
            RefreshExpiryLocked();
            return !TryGetLocked(proposalId, out var record, out var error)
                ? WorkflowCSharpSourceStoreResult.Failure("proposal-not-found", error!)
                : WorkflowCSharpSourceStoreResult.Success(CreateSnapshot(record!));
        }
    }

    internal WorkflowCSharpSourceStoreResult Preview(string? proposalId)
    {
        lock (_gate)
        {
            RefreshExpiryLocked();
            if (!TryGetLocked(proposalId, out var record, out var error))
                return WorkflowCSharpSourceStoreResult.Failure("proposal-not-found", error!);
            if (record!.State == MauiCSharpSourceProposalStates.Proposed)
                TransitionLocked(record, MauiCSharpSourceProposalStates.Previewed);
            if (record.State is not MauiCSharpSourceProposalStates.Previewed and
                not MauiCSharpSourceProposalStates.Approved and
                not MauiCSharpSourceProposalStates.AwaitingHostApply and
                not MauiCSharpSourceProposalStates.Applying and
                not MauiCSharpSourceProposalStates.Applied and
                not MauiCSharpSourceProposalStates.Verified and
                not MauiCSharpSourceProposalStates.RollbackRequired and
                not MauiCSharpSourceProposalStates.Reverted)
            {
                return WorkflowCSharpSourceStoreResult.Failure(
                    "proposal-not-previewable",
                    $"C# source proposal state '{record.State}' cannot be previewed.");
            }
            return WorkflowCSharpSourceStoreResult.Success(CreateSnapshot(record));
        }
    }

    internal WorkflowCSharpSourceStoreResult Reject(string? proposalId, string? reviewer, string? reasonCode)
    {
        lock (_gate)
        {
            RefreshExpiryLocked();
            if (!TryGetLocked(proposalId, out var record, out var error))
                return WorkflowCSharpSourceStoreResult.Failure("proposal-not-found", error!);
            if (IsTerminal(record!.State))
            {
                return WorkflowCSharpSourceStoreResult.Failure(
                    "proposal-terminal",
                    $"C# source proposal state '{record.State}' cannot be rejected.");
            }
            record.Reviewer = Bounded(reviewer, 256);
            record.ReasonCode = Bounded(reasonCode, 128) ?? "rejected";
            TransitionLocked(record, MauiCSharpSourceProposalStates.Rejected);
            return WorkflowCSharpSourceStoreResult.Success(CreateSnapshot(record));
        }
    }

    internal WorkflowCSharpSourceGrantIssueResult IssueGrant(WorkflowCSharpSourceGrantIssueRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            RefreshExpiryLocked();
            PurgeExpiredGrantsLocked();
            if (!TryGetLocked(request.ProposalId, out var record, out var error))
                return WorkflowCSharpSourceGrantIssueResult.Failure("proposal-not-found", error!);
            var found = record!;
            if (!request.HumanConfirmed || string.IsNullOrWhiteSpace(request.Reviewer))
            {
                return WorkflowCSharpSourceGrantIssueResult.Failure(
                    "human-approval-required",
                    "A named human reviewer must explicitly issue the C# source grant.");
            }
            if (!IsGrantAllowed(found, request.Kind, out error))
                return WorkflowCSharpSourceGrantIssueResult.Failure("grant-state-invalid", error!);
            if (!BindingMatches(
                    found,
                    request.Binding,
                    againstAppliedContent: request.Kind == WorkflowCSharpSourceGrantKinds.Rollback,
                    out error))
            {
                return WorkflowCSharpSourceGrantIssueResult.Failure("grant-binding-mismatch", error!);
            }
            if (_grants.Count >= _options.MaxGrants)
            {
                return WorkflowCSharpSourceGrantIssueResult.Failure(
                    "grant-capacity-exhausted",
                    "The broker C# source grant store is full.");
            }

            var now = _clock.GetUtcNow();
            var expiry = request.ExpiresAt ?? now.Add(_options.DefaultGrantLifetime);
            if (expiry <= now || expiry > now.Add(_options.MaximumGrantLifetime))
            {
                return WorkflowCSharpSourceGrantIssueResult.Failure(
                    "grant-expiry-invalid",
                    "The C# source grant expiry is outside broker policy.");
            }
            if (request.Kind == WorkflowCSharpSourceGrantKinds.Apply)
                TransitionLocked(found, MauiCSharpSourceProposalStates.Approved);

            var secret = OpaqueSecret("csharpgrant");
            var digest = Hash(secret);
            var grant = new GrantRecord(
                secret,
                digest,
                request.Kind!,
                found.Proposal.ProposalId!,
                found.Revision,
                request.Kind == WorkflowCSharpSourceGrantKinds.Rollback
                    ? found.Proposal.RollbackPatchDigest!
                    : found.Proposal.PatchDigest!,
                found.Proposal.RollbackPatchDigest!,
                request.Kind == WorkflowCSharpSourceGrantKinds.Rollback
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
            return WorkflowCSharpSourceGrantIssueResult.Success(
                secret,
                digest,
                expiry,
                CreateSnapshot(found));
        }
    }

    internal WorkflowCSharpSourceStoreResult AwaitHostApply(
        string? proposalId,
        WorkflowCSharpSourceGrantBinding? binding,
        WorkflowCSharpSourceHostCapability? capability)
    {
        lock (_gate)
        {
            RefreshExpiryLocked();
            if (!TryGetLocked(proposalId, out var record, out var error))
                return WorkflowCSharpSourceStoreResult.Failure("proposal-not-found", error!);
            if (record!.State != MauiCSharpSourceProposalStates.Approved)
            {
                return WorkflowCSharpSourceStoreResult.Failure(
                    "approval-required",
                    "A current source-specific human approval is required before IDE handoff.");
            }
            if (!BindingMatches(record, binding, againstAppliedContent: false, out error))
            {
                record.ReasonCode = "source-binding-stale";
                TransitionLocked(record, MauiCSharpSourceProposalStates.Stale);
                return WorkflowCSharpSourceStoreResult.Failure("source-binding-stale", error!, CreateSnapshot(record));
            }
            if (!SupportsIDEApply(capability, out error))
                return WorkflowCSharpSourceStoreResult.Failure("host-apply-unsupported", error!);

            TransitionLocked(record, MauiCSharpSourceProposalStates.AwaitingHostApply);
            return WorkflowCSharpSourceStoreResult.Success(CreateSnapshot(record));
        }
    }

    internal WorkflowCSharpSourceStoreResult BeginHostApply(
        string? proposalId,
        string? grant,
        WorkflowCSharpSourceGrantBinding? binding,
        WorkflowCSharpSourceHostCapability? capability)
    {
        lock (_gate)
        {
            RefreshExpiryLocked();
            if (!TryGetLocked(proposalId, out var record, out var error))
                return WorkflowCSharpSourceStoreResult.Failure("proposal-not-found", error!);
            if (record!.State == MauiCSharpSourceProposalStates.Stale)
            {
                return WorkflowCSharpSourceStoreResult.Failure(
                    MauiCSharpSourceProposalStates.Stale,
                    "The C# source proposal is stale and must be regenerated.");
            }
            if (record.State is not MauiCSharpSourceProposalStates.Approved and
                not MauiCSharpSourceProposalStates.AwaitingHostApply)
            {
                return WorkflowCSharpSourceStoreResult.Failure(
                    "approval-required",
                    "A current human-issued C# source approval is required before IDE apply.");
            }
            if (!BindingMatches(record, binding, againstAppliedContent: false, out error))
            {
                record.ReasonCode = "source-binding-stale";
                TransitionLocked(record, MauiCSharpSourceProposalStates.Stale);
                return WorkflowCSharpSourceStoreResult.Failure("source-binding-stale", error!, CreateSnapshot(record));
            }
            if (!SupportsIDEApply(capability, out error))
                return WorkflowCSharpSourceStoreResult.Failure("host-apply-unsupported", error!);
            if (!GrantMatchesBinding(grant, binding, out error) ||
                !ConsumeGrantLocked(grant, record, WorkflowCSharpSourceGrantKinds.Apply, out error))
            {
                return WorkflowCSharpSourceStoreResult.Failure("approval-grant-invalid", error!);
            }

            record.HostKind = capability!.HostKind;
            TransitionLocked(record, MauiCSharpSourceProposalStates.Applying);
            return WorkflowCSharpSourceStoreResult.Success(CreateSnapshot(record));
        }
    }

    internal WorkflowCSharpSourceStoreResult CompleteHostApply(
        string? proposalId,
        WorkflowCSharpSourceHostApplyRecord apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        lock (_gate)
        {
            if (!TryGetLocked(proposalId, out var record, out var error))
                return WorkflowCSharpSourceStoreResult.Failure("proposal-not-found", error!);
            if (record!.State != MauiCSharpSourceProposalStates.Applying)
            {
                return WorkflowCSharpSourceStoreResult.Failure(
                    "apply-state-invalid",
                    "The C# source proposal is not awaiting an IDE host acknowledgment.");
            }

            if (!apply.Applied ||
                !FixedEquals(apply.PreContentDigest, record.Proposal.BaseContentDigest) ||
                !FixedEquals(apply.AppliedContentDigest, record.Proposal.Patch.AfterDigest) ||
                !FixedEquals(apply.PatchDigest, record.Proposal.PatchDigest))
            {
                record.ReasonCode = Bounded(apply.ErrorCode, 128) ?? "host-apply-ack-invalid";
                TransitionLocked(record, MauiCSharpSourceProposalStates.Stale);
                return WorkflowCSharpSourceStoreResult.Failure(
                    record.ReasonCode,
                    apply.Error ?? "The IDE host did not acknowledge the exact reviewed C# patch.",
                    CreateSnapshot(record));
            }

            record.PreContentDigest = apply.PreContentDigest;
            record.AppliedContentDigest = apply.AppliedContentDigest;
            record.ApplyRunId = Bounded(apply.ApplyRunId, 256);
            TransitionLocked(record, MauiCSharpSourceProposalStates.Applied);
            return WorkflowCSharpSourceStoreResult.Success(CreateSnapshot(record));
        }
    }

    internal WorkflowCSharpSourceStoreResult RecordVerification(
        string? proposalId,
        WorkflowCSharpSourceVerificationRecord verification)
    {
        ArgumentNullException.ThrowIfNull(verification);
        lock (_gate)
        {
            if (!TryGetLocked(proposalId, out var record, out var error))
                return WorkflowCSharpSourceStoreResult.Failure("proposal-not-found", error!);
            if (record!.State != MauiCSharpSourceProposalStates.Applied)
            {
                return WorkflowCSharpSourceStoreResult.Failure(
                    "verification-state-invalid",
                    "Only an IDE-applied C# source proposal can receive verification facts.");
            }

            record.Verification = CloneVerification(verification);
            var requirements = CheckVerificationRequirements(record, record.Verification);
            if (requirements.Count > 0 &&
                requirements.All(static reason => string.Equals(reason, "pending-external-qa", StringComparison.Ordinal)))
            {
                record.Verification.Reasons = requirements;
                return WorkflowCSharpSourceStoreResult.Success(CreateSnapshot(record));
            }
            if (requirements.Count == 0)
            {
                TransitionLocked(record, MauiCSharpSourceProposalStates.Verified);
                return WorkflowCSharpSourceStoreResult.Success(CreateSnapshot(record));
            }

            record.ReasonCode = "verification-failed";
            record.LastRecoveryState = MauiCSharpSourceProposalStates.VerificationFailed;
            record.Verification.Reasons = requirements;
            TransitionLocked(record, MauiCSharpSourceProposalStates.RollbackRequired);
            return WorkflowCSharpSourceStoreResult.Success(CreateSnapshot(record));
        }
    }

    internal WorkflowCSharpSourceStoreResult BeginRollback(
        string? proposalId,
        string? grant,
        WorkflowCSharpSourceGrantBinding? binding,
        WorkflowCSharpSourceHostCapability? capability)
    {
        lock (_gate)
        {
            if (!TryGetLocked(proposalId, out var record, out var error))
                return WorkflowCSharpSourceStoreResult.Failure("proposal-not-found", error!);
            if (record!.State != MauiCSharpSourceProposalStates.RollbackRequired)
            {
                return WorkflowCSharpSourceStoreResult.Failure(
                    "rollback-not-required",
                    "This C# source proposal is not in rollback-required state.");
            }
            if (string.IsNullOrWhiteSpace(record.AppliedContentDigest))
            {
                return WorkflowCSharpSourceStoreResult.Failure(
                    "rollback-data-unavailable",
                    "The IDE host did not acknowledge an applied C# content digest.");
            }
            if (!BindingMatches(record, binding, againstAppliedContent: true, out error))
                return WorkflowCSharpSourceStoreResult.Failure("rollback-binding-mismatch", error!);
            if (!SupportsIDEApply(capability, out error))
                return WorkflowCSharpSourceStoreResult.Failure("host-apply-unsupported", error!);
            if (!GrantMatchesBinding(grant, binding, out error) ||
                !ConsumeGrantLocked(grant, record, WorkflowCSharpSourceGrantKinds.Rollback, out error))
            {
                return WorkflowCSharpSourceStoreResult.Failure("rollback-grant-invalid", error!);
            }

            record.HostKind = capability!.HostKind;
            return WorkflowCSharpSourceStoreResult.Success(CreateSnapshot(record));
        }
    }

    internal WorkflowCSharpSourceStoreResult CompleteRollback(
        string? proposalId,
        WorkflowCSharpSourceRollbackRecord rollback)
    {
        ArgumentNullException.ThrowIfNull(rollback);
        lock (_gate)
        {
            if (!TryGetLocked(proposalId, out var record, out var error))
                return WorkflowCSharpSourceStoreResult.Failure("proposal-not-found", error!);
            if (record!.State != MauiCSharpSourceProposalStates.RollbackRequired)
            {
                return WorkflowCSharpSourceStoreResult.Failure(
                    "rollback-state-invalid",
                    "The C# source proposal is not awaiting an IDE rollback acknowledgment.");
            }

            var reverted = rollback.Reverted &&
                FixedEquals(rollback.PreContentDigest, record.AppliedContentDigest) &&
                FixedEquals(rollback.ContentDigest, record.Proposal.BaseContentDigest) &&
                FixedEquals(rollback.PatchDigest, record.Proposal.RollbackPatchDigest);
            record.RollbackContentDigest = Bounded(rollback.ContentDigest, 256);
            record.ReasonCode = reverted ? null : Bounded(rollback.ErrorCode, 128) ?? "rollback-ack-invalid";
            TransitionLocked(
                record,
                reverted
                    ? MauiCSharpSourceProposalStates.Reverted
                    : MauiCSharpSourceProposalStates.RollbackFailed);
            return reverted
                ? WorkflowCSharpSourceStoreResult.Success(CreateSnapshot(record))
                : WorkflowCSharpSourceStoreResult.Failure(
                    record.ReasonCode!,
                    rollback.Error ?? "The IDE host did not acknowledge the exact reviewed rollback patch.",
                    CreateSnapshot(record));
        }
    }

    private static bool ValidateProposal(MauiCSharpSourceProposal proposal, out string? error)
    {
        error = null;
        if (proposal.Schema != 1 ||
            !string.Equals(proposal.Language, "CSharp", StringComparison.Ordinal) ||
            proposal.Eligibility?.Eligible != true ||
            proposal.Operation is null ||
            proposal.Element is null ||
            proposal.Patch is null ||
            proposal.RollbackPatch is null ||
            string.IsNullOrWhiteSpace(proposal.Operation.OperationId) ||
            string.IsNullOrWhiteSpace(proposal.Operation.FileRelativePath) ||
            string.IsNullOrWhiteSpace(proposal.Operation.SourceHash) ||
            string.IsNullOrWhiteSpace(proposal.Operation.SourceAnchor) ||
            string.IsNullOrWhiteSpace(proposal.Operation.SymbolId) ||
            string.IsNullOrWhiteSpace(proposal.Operation.SemanticType) ||
            !string.Equals(proposal.Operation.Attribute, "AutomationId", StringComparison.Ordinal) ||
            !MauiAutomationIdProposalPolicy.TryValidate(proposal.Operation.NewLiteral, out _) ||
            string.IsNullOrWhiteSpace(proposal.BaseContentDigest) ||
            string.IsNullOrWhiteSpace(proposal.PatchDigest) ||
            string.IsNullOrWhiteSpace(proposal.RollbackPatchDigest) ||
            string.IsNullOrWhiteSpace(proposal.DiffDigest) ||
            string.IsNullOrWhiteSpace(proposal.Diff) ||
            proposal.Patch.Start is < 0 ||
            proposal.Patch.Length is < 0 ||
            proposal.Patch.Replacement is null ||
            proposal.RollbackPatch.Start is < 0 ||
            proposal.RollbackPatch.Length is < 0 ||
            proposal.RollbackPatch.Replacement is null ||
            !FixedEquals(proposal.BaseContentDigest, proposal.Patch.BeforeDigest) ||
            !FixedEquals(proposal.Patch.AfterDigest, proposal.RollbackPatch.BeforeDigest) ||
            !FixedEquals(proposal.RollbackPatch.AfterDigest, proposal.BaseContentDigest))
        {
            error = "The C# source proposal must contain one eligible Roslyn-proven literal AutomationId operation and exact forward/rollback patches.";
            return false;
        }
        return true;
    }

    private bool IsGrantAllowed(Record record, string? kind, out string? error)
    {
        error = null;
        switch (kind)
        {
            case WorkflowCSharpSourceGrantKinds.Apply when record.State == MauiCSharpSourceProposalStates.Previewed:
                return true;
            case WorkflowCSharpSourceGrantKinds.Rollback when record.State == MauiCSharpSourceProposalStates.RollbackRequired:
                return true;
            default:
                error = $"C# source proposal state '{record.State}' cannot receive a {kind} grant.";
                return false;
        }
    }

    private static bool BindingMatches(
        Record record,
        WorkflowCSharpSourceGrantBinding? binding,
        bool againstAppliedContent,
        out string? error)
    {
        error = null;
        var expectedDigest = againstAppliedContent
            ? record.AppliedContentDigest
            : record.Proposal.BaseContentDigest;
        var expectedFlowReferences = ComputeFlowReferencesDigest(record.Proposal.AffectedFlows);
        if (binding is null ||
            string.IsNullOrWhiteSpace(binding.FileRelativePath) ||
            string.IsNullOrWhiteSpace(binding.BaseContentDigest) ||
            string.IsNullOrWhiteSpace(binding.SourceHash) ||
            string.IsNullOrWhiteSpace(binding.PatchDigest) ||
            string.IsNullOrWhiteSpace(binding.RollbackPatchDigest) ||
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
            !FixedEquals(record.Proposal.RollbackPatchDigest, binding.RollbackPatchDigest) ||
            !FixedEquals(expectedFlowReferences, binding.FlowReferencesDigest))
        {
            error = "The C# source grant must bind the exact file, hashes, patches, project identity, and flow references.";
            return false;
        }
        return true;
    }

    private bool GrantMatchesBinding(
        string? secret,
        WorkflowCSharpSourceGrantBinding? binding,
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
            error = "The C# source grant project, host, or affected-flow binding no longer matches.";
            return false;
        }
        return true;
    }

    private bool ConsumeGrantLocked(string? secret, Record record, string expectedKind, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(secret) || !_grants.TryGetValue(secret, out var grant))
        {
            error = "The C# source grant is missing, stale, or already used.";
            return false;
        }
        var now = _clock.GetUtcNow();
        if (grant.Used || grant.ExpiresAt <= now)
        {
            _grants.Remove(secret);
            if (expectedKind == WorkflowCSharpSourceGrantKinds.Apply &&
                (record.State is MauiCSharpSourceProposalStates.Approved or MauiCSharpSourceProposalStates.AwaitingHostApply))
            {
                record.ReasonCode = "approval-expired";
                TransitionLocked(record, MauiCSharpSourceProposalStates.Stale);
            }
            error = grant.Used ? "The C# source grant was already used." : "The C# source grant expired.";
            return false;
        }
        var expectedContent = expectedKind == WorkflowCSharpSourceGrantKinds.Rollback
            ? record.AppliedContentDigest
            : record.Proposal.BaseContentDigest;
        var expectedPatch = expectedKind == WorkflowCSharpSourceGrantKinds.Rollback
            ? record.Proposal.RollbackPatchDigest
            : record.Proposal.PatchDigest;
        if (grant.Kind != expectedKind ||
            !string.Equals(grant.ProposalId, record.Proposal.ProposalId, StringComparison.Ordinal) ||
            !FixedEquals(grant.PatchDigest, expectedPatch) ||
            !FixedEquals(grant.ContentDigest, expectedContent) ||
            !FixedEquals(grant.SourceHash, record.Proposal.Operation.SourceHash))
        {
            error = "The C# source grant is not bound to the current proposal revision.";
            return false;
        }
        grant.Used = true;
        return true;
    }

    private static bool SupportsIDEApply(WorkflowCSharpSourceHostCapability? capability, out string? error)
    {
        error = null;
        if (capability is null ||
            !capability.IsExplicitLocalHostAction ||
            !capability.CanApplyCSharpSource ||
            string.IsNullOrWhiteSpace(capability.HostKind))
        {
            error = "This host cannot perform an explicit IDE-mediated C# source apply. Preview or download the patch instead.";
            return false;
        }
        if (capability.HostKind.Contains("canvas", StringComparison.OrdinalIgnoreCase))
        {
            error = "Canvas reports C# source apply unsupported; use VS Code or another native IDE host.";
            return false;
        }
        return true;
    }

    private static List<string> CheckVerificationRequirements(
        Record record,
        WorkflowCSharpSourceVerificationRecord verification)
    {
        var reasons = new List<string>();
        if (record.Proposal.AffectedPlatforms.Count == 0)
            reasons.Add("affected-platforms-required");
        foreach (var platform in record.Proposal.AffectedPlatforms)
        {
            var result = verification.Platforms.FirstOrDefault(candidate =>
                string.Equals(candidate.TargetFramework, platform.TargetFramework, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.Platform, platform.Platform, StringComparison.OrdinalIgnoreCase));
            if (result is null)
            {
                reasons.Add("platform-verification-missing");
                continue;
            }
            if (result.PendingExternalQa && IsAppleTarget(result))
            {
                reasons.Add("pending-external-qa");
                continue;
            }
            if (!result.BuildSucceeded)
                reasons.Add("platform-build-incomplete");
            if (!result.RuntimeRemapConfirmed)
                reasons.Add("runtime-remap-unconfirmed");
            if (!result.AutomationIdUnique)
                reasons.Add("runtime-uniqueness-unconfirmed");
            if (!result.ReplaySucceeded)
                reasons.Add("platform-replay-incomplete");
            if (!result.IndependentOracleSucceeded)
                reasons.Add("platform-oracle-incomplete");
        }
        if (!verification.AffectedFlowsReplayed)
            reasons.Add("affected-flows-not-replayed");
        if (!verification.IndependentOracleSucceeded)
            reasons.Add("independent-oracle-failed");
        return reasons.Distinct(StringComparer.Ordinal).Take(32).ToList();
    }

    private static bool IsAppleTarget(WorkflowCSharpSourcePlatformVerificationResult result)
        => result.Platform?.Contains("ios", StringComparison.OrdinalIgnoreCase) == true ||
           result.Platform?.Contains("maccatalyst", StringComparison.OrdinalIgnoreCase) == true ||
           result.Platform?.Contains("macos", StringComparison.OrdinalIgnoreCase) == true ||
           result.TargetFramework?.Contains("-ios", StringComparison.OrdinalIgnoreCase) == true ||
           result.TargetFramework?.Contains("-maccatalyst", StringComparison.OrdinalIgnoreCase) == true ||
           result.TargetFramework?.Contains("-macos", StringComparison.OrdinalIgnoreCase) == true;

    private void RefreshExpiryLocked()
    {
        var now = _clock.GetUtcNow();
        foreach (var record in _records.Values)
        {
            if ((record.State is MauiCSharpSourceProposalStates.Approved or MauiCSharpSourceProposalStates.AwaitingHostApply) &&
                record.ExpiresAt is { } expiry &&
                expiry <= now)
            {
                record.ReasonCode = "approval-expired";
                TransitionLocked(record, MauiCSharpSourceProposalStates.Stale);
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
        MauiCSharpSourceProposalStates.Rejected or
        MauiCSharpSourceProposalStates.Stale or
        MauiCSharpSourceProposalStates.Verified or
        MauiCSharpSourceProposalStates.Reverted or
        MauiCSharpSourceProposalStates.RollbackFailed;

    private void TransitionLocked(Record record, string state)
    {
        record.State = state;
        record.Revision++;
        record.UpdatedAt = _clock.GetUtcNow();
    }

    private bool TryGetLocked(string? proposalId, out Record? record, out string? error)
    {
        record = null;
        error = null;
        if (string.IsNullOrWhiteSpace(proposalId) || !_records.TryGetValue(proposalId, out record))
        {
            error = "The C# source proposal was not found.";
            return false;
        }
        return true;
    }

    private static WorkflowCSharpSourceProposalSnapshot CreateSnapshot(Record record) => new()
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
            new MauiCSharpSourceApplyState
            {
                State = record.State,
                HostKind = record.HostKind,
                AppliedAt = record.State is MauiCSharpSourceProposalStates.Applied or
                    MauiCSharpSourceProposalStates.Verified or
                    MauiCSharpSourceProposalStates.RollbackRequired or
                    MauiCSharpSourceProposalStates.Reverted
                    ? record.UpdatedAt
                    : null,
                PreContentDigest = record.PreContentDigest,
                AppliedContentDigest = record.AppliedContentDigest,
                PatchDigest = record.Proposal.PatchDigest,
                ReasonCode = record.ReasonCode,
            },
            new MauiCSharpSourceVerificationState
            {
                State = record.LastRecoveryState ?? record.State,
                VerificationRunIds = record.Verification?.VerificationRunIds.ToList() ?? [],
                Platforms = record.Verification?.Platforms.Select(static platform => new MauiCSharpSourcePlatformVerification
                {
                    Platform = platform.Platform,
                    TargetFramework = platform.TargetFramework,
                    BuildState = platform.PendingExternalQa ? "pending-external-qa" : platform.BuildSucceeded ? "succeeded" : "failed-or-pending",
                    RuntimeRemapState = platform.PendingExternalQa ? "pending-external-qa" : platform.RuntimeRemapConfirmed ? "confirmed" : "unconfirmed",
                    UniquenessState = platform.PendingExternalQa ? "pending-external-qa" : platform.AutomationIdUnique ? "confirmed" : "unconfirmed",
                    ReplayState = platform.PendingExternalQa ? "pending-external-qa" : platform.ReplaySucceeded ? "succeeded" : "unconfirmed",
                    OracleState = platform.PendingExternalQa ? "pending-external-qa" : platform.IndependentOracleSucceeded ? "succeeded" : "unconfirmed",
                    ReasonCode = platform.ReasonCode,
                }).ToList() ?? [],
                CompletedAt = record.Verification?.CompletedAt,
                ReasonCode = record.ReasonCode,
            },
            new MauiCSharpSourceRollbackState
            {
                State = record.State is MauiCSharpSourceProposalStates.Reverted or MauiCSharpSourceProposalStates.RollbackFailed
                    ? record.State
                    : null,
                RevertedAt = record.State is MauiCSharpSourceProposalStates.Reverted or MauiCSharpSourceProposalStates.RollbackFailed
                    ? record.UpdatedAt
                    : null,
                PreContentDigest = record.AppliedContentDigest,
                ContentDigest = record.RollbackContentDigest,
                PatchDigest = record.Proposal.RollbackPatchDigest,
                ReasonCode = record.ReasonCode,
            }),
        State = record.State,
        Revision = record.Revision,
        Reviewer = record.Reviewer,
        GrantDigest = record.GrantDigest,
        ExpiresAt = record.ExpiresAt,
        ReasonCode = record.ReasonCode,
        LastRecoveryState = record.LastRecoveryState,
        AppliedContentDigest = record.AppliedContentDigest,
        PreContentDigest = record.PreContentDigest,
        HostKind = record.HostKind,
        Verification = record.Verification is null ? null : CloneVerification(record.Verification),
    };

    private static MauiCSharpSourceProposal WithMetadata(
        MauiCSharpSourceProposal source,
        string id,
        int revision,
        string state,
        DateTimeOffset createdAt,
        string? reviewer,
        string? grantDigest,
        DateTimeOffset? expiresAt,
        MauiCSharpSourceApplyState? apply,
        MauiCSharpSourceVerificationState? verification,
        MauiCSharpSourceRollbackState? rollback)
    {
        var node = JsonSerializer.SerializeToNode(source, MauiTestingJsonContext.Default.MauiCSharpSourceProposal)!.AsObject();
        node["proposalId"] = id;
        node["revision"] = revision;
        node["state"] = state;
        node["createdAt"] = createdAt;
        node["approval"] = reviewer is null && expiresAt is null
            ? null
            : JsonSerializer.SerializeToNode(new MauiCSharpSourceApproval
            {
                State = state,
                Reviewer = reviewer,
                ExpiresAt = expiresAt,
                GrantDigest = grantDigest,
            }, MauiTestingJsonContext.Default.MauiCSharpSourceApproval);
        node["apply"] = JsonSerializer.SerializeToNode(apply, MauiTestingJsonContext.Default.MauiCSharpSourceApplyState);
        node["verification"] = JsonSerializer.SerializeToNode(verification, MauiTestingJsonContext.Default.MauiCSharpSourceVerificationState);
        node["rollback"] = JsonSerializer.SerializeToNode(rollback, MauiTestingJsonContext.Default.MauiCSharpSourceRollbackState);
        return node.Deserialize(MauiTestingJsonContext.Default.MauiCSharpSourceProposal)
            ?? throw new InvalidOperationException("A C# source proposal could not be normalized.");
    }

    private static WorkflowCSharpSourceVerificationRecord CloneVerification(WorkflowCSharpSourceVerificationRecord value)
        => new()
        {
            Platforms = value.Platforms.Take(16).Select(platform => new WorkflowCSharpSourcePlatformVerificationResult
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
            }).ToList(),
            AffectedFlowsReplayed = value.AffectedFlowsReplayed,
            IndependentOracleSucceeded = value.IndependentOracleSucceeded,
            VerificationRunIds = value.VerificationRunIds.Where(static value => !string.IsNullOrWhiteSpace(value)).Take(64).ToList(),
            CompletedAt = value.CompletedAt ?? DateTimeOffset.UtcNow,
            Reasons = value.Reasons.Where(static value => !string.IsNullOrWhiteSpace(value)).Take(32).ToList(),
        };

    internal static string ComputeFlowReferencesDigest(IReadOnlyList<MauiCSharpSourceFlowFollowUp>? flows)
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
        => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty))).ToLowerInvariant();

    private static bool FixedEquals(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) &&
           !string.IsNullOrWhiteSpace(right) &&
           CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    private static string? Bounded(string? value, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maximum ? trimmed : null;
    }

    private sealed class Record
    {
        public Record(MauiCSharpSourceProposal proposal, string state, int revision, DateTimeOffset createdAt)
        {
            Proposal = proposal;
            State = state;
            Revision = revision;
            CreatedAt = createdAt;
            UpdatedAt = createdAt;
        }

        public MauiCSharpSourceProposal Proposal { get; }
        public string State { get; set; }
        public int Revision { get; set; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset UpdatedAt { get; set; }
        public string? Reviewer { get; set; }
        public string? GrantDigest { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public string? ReasonCode { get; set; }
        public string? LastRecoveryState { get; set; }
        public string? HostKind { get; set; }
        public string? PreContentDigest { get; set; }
        public string? AppliedContentDigest { get; set; }
        public string? RollbackContentDigest { get; set; }
        public string? ApplyRunId { get; set; }
        public WorkflowCSharpSourceVerificationRecord? Verification { get; set; }
    }

    private sealed class GrantRecord
    {
        public GrantRecord(
            string secret,
            string digest,
            string kind,
            string proposalId,
            int proposalRevision,
            string patchDigest,
            string rollbackPatchDigest,
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
            RollbackPatchDigest = rollbackPatchDigest;
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
        public string RollbackPatchDigest { get; }
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

internal sealed class WorkflowCSharpSourceProposalStoreOptions
{
    public int MaxProposals { get; init; } = 128;
    public int MaxGrants { get; init; } = 256;
    public TimeSpan DefaultGrantLifetime { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan MaximumGrantLifetime { get; init; } = TimeSpan.FromMinutes(30);
}

internal static class WorkflowCSharpSourceGrantKinds
{
    public const string Apply = "apply";
    public const string Rollback = "rollback";
}

internal sealed class WorkflowCSharpSourceGrantBinding
{
    public string? FileRelativePath { get; init; }
    public string? BaseContentDigest { get; init; }
    public string? SourceHash { get; init; }
    public string? PatchDigest { get; init; }
    public string? RollbackPatchDigest { get; init; }
    public string? ProjectIdentity { get; init; }
    public string? FlowReferencesDigest { get; init; }
    public string? HostKind { get; init; }
}

internal sealed class WorkflowCSharpSourceHostCapability
{
    public string? HostKind { get; init; }
    public bool CanOpenNativeDiff { get; init; }
    public bool CanDownloadPatch { get; init; }
    public bool CanApplyCSharpSource { get; init; }
    public bool IsExplicitLocalHostAction { get; init; }
}

internal sealed class WorkflowCSharpSourceGrantIssueRequest
{
    public string? ProposalId { get; init; }
    public string? Kind { get; init; }
    public string? Reviewer { get; init; }
    public bool HumanConfirmed { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public WorkflowCSharpSourceGrantBinding? Binding { get; init; }
}

internal sealed class WorkflowCSharpSourceGrantIssueResult
{
    public bool Ok { get; private init; }
    public string? Grant { get; private init; }
    public string? GrantDigest { get; private init; }
    public DateTimeOffset? ExpiresAt { get; private init; }
    public WorkflowCSharpSourceProposalSnapshot? Proposal { get; private init; }
    public string? Code { get; private init; }
    public string? Error { get; private init; }

    public static WorkflowCSharpSourceGrantIssueResult Success(
        string grant,
        string digest,
        DateTimeOffset expiresAt,
        WorkflowCSharpSourceProposalSnapshot proposal)
        => new() { Ok = true, Grant = grant, GrantDigest = digest, ExpiresAt = expiresAt, Proposal = proposal };

    public static WorkflowCSharpSourceGrantIssueResult Failure(string code, string error)
        => new() { Code = code, Error = error };
}

internal sealed class WorkflowCSharpSourceHostApplyRecord
{
    public bool Applied { get; init; }
    public string? PreContentDigest { get; init; }
    public string? AppliedContentDigest { get; init; }
    public string? PatchDigest { get; init; }
    public string? ApplyRunId { get; init; }
    public string? ErrorCode { get; init; }
    public string? Error { get; init; }
}

internal sealed class WorkflowCSharpSourcePlatformVerificationResult
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

internal sealed class WorkflowCSharpSourceVerificationRecord
{
    public List<WorkflowCSharpSourcePlatformVerificationResult> Platforms { get; init; } = [];
    public bool AffectedFlowsReplayed { get; init; }
    public bool IndependentOracleSucceeded { get; init; }
    public List<string> VerificationRunIds { get; init; } = [];
    public DateTimeOffset? CompletedAt { get; init; }
    public List<string> Reasons { get; set; } = [];
}

internal sealed class WorkflowCSharpSourceRollbackRecord
{
    public bool Reverted { get; init; }
    public string? PreContentDigest { get; init; }
    public string? ContentDigest { get; init; }
    public string? PatchDigest { get; init; }
    public string? ErrorCode { get; init; }
    public string? Error { get; init; }
}

internal sealed class WorkflowCSharpSourceProposalSnapshot
{
    public MauiCSharpSourceProposal Proposal { get; init; } = new();
    public string State { get; init; } = "";
    public int Revision { get; init; }
    public string? Reviewer { get; init; }
    public string? GrantDigest { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? ReasonCode { get; init; }
    public string? LastRecoveryState { get; init; }
    public string? PreContentDigest { get; init; }
    public string? AppliedContentDigest { get; init; }
    public string? HostKind { get; init; }
    public WorkflowCSharpSourceVerificationRecord? Verification { get; init; }
}

internal sealed class WorkflowCSharpSourceStoreResult
{
    public bool Ok { get; private init; }
    public WorkflowCSharpSourceProposalSnapshot? Proposal { get; private init; }
    public string? Code { get; private init; }
    public string? Error { get; private init; }

    public static WorkflowCSharpSourceStoreResult Success(WorkflowCSharpSourceProposalSnapshot proposal)
        => new() { Ok = true, Proposal = proposal };

    public static WorkflowCSharpSourceStoreResult Failure(
        string code,
        string error,
        WorkflowCSharpSourceProposalSnapshot? proposal = null)
        => new() { Code = code, Error = error, Proposal = proposal };
}

/// <summary>Append-only redacted C# proposal lifecycle history; no source text or literals are written.</summary>
internal sealed class WorkflowCSharpSourceHistoryStore
{
    private const int MaxEntries = 256;
    private const int MaxBytes = 1_048_576;
    private static readonly ConcurrentDictionary<string, object> Gates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string _projectRoot;

    internal WorkflowCSharpSourceHistoryStore(string projectRoot)
        => _projectRoot = Path.GetFullPath(projectRoot);

    internal WorkflowCSharpSourceHistoryAppendResult Append(WorkflowCSharpSourceProposalSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string? error = null;
        if (!IsSafeProposalId(snapshot.Proposal.ProposalId) ||
            !TryGetHistoryPath(snapshot.Proposal.ProposalId!, out var path, out error))
        {
            return WorkflowCSharpSourceHistoryAppendResult.Failure(
                "source-history-invalid",
                error ?? "The C# source proposal history identity is invalid.");
        }

        lock (Gates.GetOrAdd(path!, static _ => new object()))
        {
            try
            {
                var existing = File.Exists(path) ? File.ReadAllText(path!) : string.Empty;
                if (Encoding.UTF8.GetByteCount(existing) > MaxBytes)
                {
                    return WorkflowCSharpSourceHistoryAppendResult.Failure(
                        "source-history-invalid",
                        "The bounded C# source proposal history is too large.");
                }
                var lines = existing.ReplaceLineEndings("\n")
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .ToList();
                if (lines.Count >= MaxEntries)
                {
                    return WorkflowCSharpSourceHistoryAppendResult.Failure(
                        "source-history-invalid",
                        "The bounded C# source proposal history has reached its entry limit.");
                }

                var previousHash = ValidateChain(lines, out error);
                if (error is not null)
                    return WorkflowCSharpSourceHistoryAppendResult.Failure("source-history-invalid", error);

                var node = new JsonObject
                {
                    ["schema"] = 1,
                    ["language"] = "CSharp",
                    ["sequence"] = lines.Count + 1,
                    ["at"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    ["previousHash"] = previousHash,
                    ["proposalId"] = Token(snapshot.Proposal.ProposalId),
                    ["revision"] = snapshot.Revision,
                    ["state"] = snapshot.State,
                    ["operationId"] = Token(snapshot.Proposal.Operation.OperationId),
                    ["fileDigest"] = Token(snapshot.Proposal.Operation.FileRelativePath),
                    ["sourceHash"] = SafeDigest(snapshot.Proposal.Operation.SourceHash),
                    ["sourceAnchor"] = SafeDigest(snapshot.Proposal.Operation.SourceAnchor),
                    ["baseContentDigest"] = SafeDigest(snapshot.Proposal.BaseContentDigest),
                    ["patchDigest"] = SafeDigest(snapshot.Proposal.PatchDigest),
                    ["rollbackPatchDigest"] = SafeDigest(snapshot.Proposal.RollbackPatchDigest),
                    ["diffDigest"] = SafeDigest(snapshot.Proposal.DiffDigest),
                    ["flowReferencesDigest"] = WorkflowCSharpSourceProposalStore.ComputeFlowReferencesDigest(snapshot.Proposal.AffectedFlows),
                    ["grantDigest"] = SafeDigest(snapshot.GrantDigest),
                    ["preContentDigest"] = SafeDigest(snapshot.PreContentDigest),
                    ["appliedContentDigest"] = SafeDigest(snapshot.AppliedContentDigest),
                    ["reasonCode"] = Token(snapshot.ReasonCode),
                };
                var material = Canonicalize(node);
                node["hash"] = "sha256:" + Hash(material);
                var line = Canonicalize(node);
                var content = existing + (existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : string.Empty) + line + "\n";
                if (Encoding.UTF8.GetByteCount(content) > MaxBytes)
                {
                    return WorkflowCSharpSourceHistoryAppendResult.Failure(
                        "source-history-invalid",
                        "Appending the C# source proposal history would exceed its size cap.");
                }
                AtomicWrite(path!, content);
                return WorkflowCSharpSourceHistoryAppendResult.Success(path!);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return WorkflowCSharpSourceHistoryAppendResult.Failure(
                    "source-history-write-failed",
                    "The redacted C# source proposal history could not be written.");
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
                error = "The C# source proposal history directory cannot use a symbolic link or reparse point.";
                return false;
            }
            var candidate = Path.GetFullPath(Path.Combine(root, "csharp-" + proposalId + ".history.jsonl"));
            if (!IsUnderRoot(candidate, root))
            {
                error = "The C# source proposal history path escapes its trusted root.";
                return false;
            }
            path = candidate;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = "The C# source proposal history root could not be resolved safely.";
            return false;
        }
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
                var declaredPrevious = node?["previousHash"]?.GetValue<string>();
                var hash = node?["hash"]?.GetValue<string>();
                if (node is null ||
                    !string.Equals(previous, declaredPrevious, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(hash))
                {
                    error = "The existing C# source proposal history hash chain is invalid.";
                    return null;
                }
                node.Remove("hash");
                if (!FixedEquals(hash, "sha256:" + Hash(Canonicalize(node))))
                {
                    error = "The existing C# source proposal history hash chain failed verification.";
                    return null;
                }
                previous = hash;
            }
            catch (JsonException)
            {
                error = "The existing C# source proposal history contains invalid JSON.";
                return null;
            }
        }
        return previous;
    }

    private static void AtomicWrite(string path, string content)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsSafeProposalId(string? value)
        => value is { Length: > 0 and <= 128 } &&
           value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

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

    private static bool FixedEquals(string left, string right)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

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
}

internal sealed class WorkflowCSharpSourceHistoryAppendResult
{
    public bool Ok { get; private init; }
    public string? HistoryPath { get; private init; }
    public string? Code { get; private init; }
    public string? Error { get; private init; }

    public static WorkflowCSharpSourceHistoryAppendResult Success(string path)
        => new() { Ok = true, HistoryPath = path };

    public static WorkflowCSharpSourceHistoryAppendResult Failure(string code, string error)
        => new() { Code = code, Error = error };
}
