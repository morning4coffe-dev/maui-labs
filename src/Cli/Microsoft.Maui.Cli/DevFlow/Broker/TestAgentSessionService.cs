using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.Cli.DevFlow.Broker;

/// <summary>
/// Broker-owned policy boundary for the restricted test-agent profile. It retains only bounded
/// draft metadata and digests; app content, prompts, screenshots, source, and grant secrets never
/// enter its journal.
/// </summary>
internal sealed class TestAgentSessionService
{
    private readonly object _gate = new();
    private readonly TestAgentSessionServiceOptions _options;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, SessionRecord> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GrantRecord> _grants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ApprovalRequestRecord> _approvalRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _expiredGrantDigests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AuthorizationRecord> _authorizations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IdempotencyRecord> _idempotency = new(StringComparer.Ordinal);
    private readonly LinkedList<MauiTestAgentAuditEntry> _journal = [];
    private long _auditSequence;

    internal TestAgentSessionService(
        TestAgentSessionServiceOptions? options = null,
        TimeProvider? clock = null)
    {
        _options = options ?? new TestAgentSessionServiceOptions();
        _clock = clock ?? TimeProvider.System;

        if (_options.MaxSessions < 1 ||
            _options.MaxGrants < 1 ||
            _options.MaxApprovalRequests < 1 ||
            _options.MaxAuditEntries < 1 ||
            _options.MaxActionsPerGrant is < 1 or > 256 ||
            _options.SessionLifetime <= TimeSpan.Zero ||
            _options.ApprovalRequestLifetime <= TimeSpan.Zero ||
            _options.ApprovalRetention <= TimeSpan.Zero ||
            _options.MaximumGrantLifetime <= TimeSpan.Zero ||
            _options.AuditRetention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Test-agent session limits are invalid.");
        }
    }

    internal MauiTestAgentSessionResult Begin(MauiTestAgentSessionBeginRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            PurgeExpiredLocked();
            var envelope = request.Envelope;
            var error = ValidateEnvelope(envelope, requireSession: false);
            if (error is not null)
                return SessionFailure(error);

            if (!TargetsMatch(envelope!.Target, request.TargetState))
                return SessionFailure(Error(
                    MauiTestAgentErrorCodes.TargetStale,
                    MauiTestAgentErrorCategories.Target,
                    "The supplied target state does not describe the exact target in the request.",
                    retryable: false));

            var requestDigest = DigestEnvelope(envelope);
            if (_idempotency.TryGetValue(envelope.IdempotencyKey!, out var previous))
            {
                if (FixedEquals(previous.RequestDigest, requestDigest) &&
                    previous.SessionId is { } priorSessionId &&
                    _sessions.TryGetValue(priorSessionId, out var prior))
                {
                    return new MauiTestAgentSessionResult
                    {
                        Ok = true,
                        Snapshot = CreateSnapshot(prior, includeReadCapability: false),
                    };
                }

                return SessionFailure(Error(
                    MauiTestAgentErrorCodes.IdempotencyReused,
                    MauiTestAgentErrorCategories.Conflict,
                    "The idempotency key was already used for a different authoring request.",
                    retryable: false));
            }

            if (_sessions.Count >= _options.MaxSessions)
                EvictOldestTerminalSessionLocked();
            if (_sessions.Count >= _options.MaxSessions)
            {
                return SessionFailure(Error(
                    MauiTestAgentErrorCodes.InvalidRequest,
                    MauiTestAgentErrorCategories.Capability,
                    "The broker has reached its bounded authoring-session capacity.",
                    retryable: true));
            }

            var now = _clock.GetUtcNow();
            var duration = request.DurationSeconds is { } seconds
                ? TimeSpan.FromSeconds(seconds)
                : _options.SessionLifetime;
            if (duration <= TimeSpan.Zero || duration > _options.SessionLifetime)
            {
                return SessionFailure(Error(
                    MauiTestAgentErrorCodes.InvalidRequest,
                    MauiTestAgentErrorCategories.Validation,
                    $"durationSeconds must be between 1 and {(int)_options.SessionLifetime.TotalSeconds}.",
                    retryable: false));
            }

            var sessionId = OpaqueId("author");
            var flowId = envelope.Correlation?.FlowId ?? OpaqueId("flow");
            var flow = CloneFlow(request.Flow) ?? new MauiFlow
            {
                Name = "agent-draft",
                RecordedAt = now.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            };
            var plan = NormalizePlan(
                request.Plan,
                envelope,
                flowId,
                flow,
                planId: envelope.Correlation?.PlanId ?? OpaqueId("plan"),
                revision: Math.Max(1, envelope.Correlation?.PlanRevision ?? 1),
                now);
            var planId = plan.PlanId!;
            var planRevision = plan.Revision!.Value;
            var flowDigest = MauiFlowRunReportSerializer.ComputeFlowDigest(flow);
            plan = NormalizePlan(plan, envelope, flowId, flow, planId, planRevision, now);

            var readCapability = OpaqueSecret("read");
            var record = new SessionRecord(
                sessionId,
                Hash(readCapability),
                envelope.Target!,
                CloneTargetState(request.TargetState!),
                CloneProvenance(envelope.Provenance!)!,
                planId,
                planRevision,
                flowId,
                Math.Max(1, envelope.Correlation?.FlowRevision ?? 1),
                plan,
                flow,
                flowDigest,
                now,
                now.Add(duration));

            _sessions.Add(sessionId, record);
            _idempotency.Add(envelope.IdempotencyKey!, new IdempotencyRecord(requestDigest, sessionId, now));
            AppendAuditLocked(record, "session-begin", envelope, "accepted", null, null, null, null);

            var snapshot = CreateSnapshot(record, includeReadCapability: true);
            snapshot.ReadCapabilityId = readCapability;
            return new MauiTestAgentSessionResult { Ok = true, Snapshot = snapshot };
        }
    }

    internal MauiTestAgentSessionResult Status(MauiTestAgentSessionAccessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            PurgeExpiredLocked();
            if (!TryGetReadableSessionLocked(request.SessionId, request.ReadCapabilityId, out var session, out var error))
                return SessionFailure(error!);

            return new MauiTestAgentSessionResult
            {
                Ok = true,
                Snapshot = CreateSnapshot(session!, includeReadCapability: false),
            };
        }
    }

    internal MauiTestAgentSessionResult Abandon(MauiTestAgentSessionAccessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            PurgeExpiredLocked();
            if (!TryGetReadableSessionLocked(request.SessionId, request.ReadCapabilityId, out var session, out var error))
                return SessionFailure(error!);

            if (session!.State == SessionState.Active)
            {
                session.State = SessionState.Abandoned;
                AppendAuditLocked(session, "session-abandon", request.Envelope, "abandoned", null, null, null, null);
            }

            return new MauiTestAgentSessionResult
            {
                Ok = true,
                Snapshot = CreateSnapshot(session, includeReadCapability: false),
            };
        }
    }

    internal MauiTestAgentApprovalResult SubmitApprovalRequest(MauiTestAgentApprovalSubmitRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            PurgeExpiredLocked();
            var envelope = request.Envelope;
            var error = ValidateEnvelope(envelope, requireSession: true);
            if (error is not null)
                return ApprovalFailure(error);

            if (!string.Equals(envelope!.PolicyVersion, MauiTestAgentProtocolVersions.PolicyVersion, StringComparison.Ordinal))
            {
                return ApprovalFailure(Error(
                    MauiTestAgentErrorCodes.InvalidRequest,
                    MauiTestAgentErrorCategories.Validation,
                    "The approval request policy version is not supported.",
                    retryable: false));
            }

            var kind = NormalizeApprovalKind(request.Kind);
            if (kind is null || !MauiTestAgentApprovalKinds.All.Contains(kind))
            {
                return ApprovalFailure(Error(
                    MauiTestAgentErrorCodes.InvalidRequest,
                    MauiTestAgentErrorCategories.Validation,
                    "The approval request kind is not supported.",
                    retryable: false));
            }

            NormalizeApprovalScope(kind, request.Scope);
            var scopeError = ValidateScope(request.Scope);
            if (scopeError is not null)
                return ApprovalFailure(scopeError);
            if (!ApprovalKindAllowsScope(kind, request.Scope!))
            {
                return ApprovalFailure(Error(
                    MauiTestAgentErrorCodes.ApprovalRequestScopeDenied,
                    MauiTestAgentErrorCategories.Authorization,
                    "The requested actions do not match the stated approval purpose.",
                    retryable: false));
            }

            var sessionId = envelope.Correlation!.AuthoringSessionId;
            if (!TryGetReadableSessionLocked(sessionId, envelope.ReadCapabilityId, out var session, out error))
                return ApprovalFailure(error!);
            if (!TargetsMatch(session!.Target, envelope.Target) ||
                !CorrelationMatches(session, envelope.Correlation, requireAll: true) ||
                !ProvenanceMatches(session.Actor, envelope.Provenance))
            {
                return ApprovalFailure(Error(
                    MauiTestAgentErrorCodes.MutationGrantStale,
                    MauiTestAgentErrorCategories.State,
                    "The approval request does not match the current target, actor, plan, or flow revision.",
                    retryable: false));
            }

            var requestDigest = DigestApprovalRequest(request);
            if (_idempotency.TryGetValue(envelope.IdempotencyKey!, out var previous))
            {
                if (FixedEquals(previous.RequestDigest, requestDigest) &&
                    previous.ApprovalRequestId is { } priorRequestId &&
                    _approvalRequests.TryGetValue(priorRequestId, out var prior))
                {
                    return new MauiTestAgentApprovalResult
                    {
                        Ok = true,
                        Request = CloneApprovalRequest(prior, includeGrant: false),
                    };
                }

                return ApprovalFailure(Error(
                    MauiTestAgentErrorCodes.IdempotencyReused,
                    MauiTestAgentErrorCategories.Conflict,
                    "The idempotency key was already used for a different request.",
                    retryable: false));
            }

            if (_approvalRequests.Count >= _options.MaxApprovalRequests)
                EvictOldestApprovalRequestLocked();
            if (_approvalRequests.Count >= _options.MaxApprovalRequests)
            {
                return ApprovalFailure(Error(
                    MauiTestAgentErrorCodes.InvalidRequest,
                    MauiTestAgentErrorCategories.Capability,
                    "The broker has reached its bounded approval-request capacity.",
                    retryable: true));
            }

            var now = _clock.GetUtcNow();
            var expiresAt = request.ExpiresAt ?? now.Add(_options.ApprovalRequestLifetime);
            if (expiresAt <= now ||
                expiresAt > now.Add(_options.ApprovalRequestLifetime) ||
                expiresAt > session.ExpiresAt)
            {
                return ApprovalFailure(Error(
                    MauiTestAgentErrorCodes.InvalidRequest,
                    MauiTestAgentErrorCategories.Validation,
                    "The approval request expiration is outside the bounded session lifetime.",
                    retryable: false));
            }

            var approvalRequestId = OpaqueId("approval");
            var record = new ApprovalRequestRecord(
                approvalRequestId,
                kind,
                session.SessionId,
                envelope.RequestId!,
                envelope.Intent!,
                session.Target,
                session.TargetState,
                session.Actor,
                SnapshotCorrelation(session),
                request.Scope!,
                now,
                expiresAt);
            _approvalRequests.Add(approvalRequestId, record);
            _idempotency.Add(
                envelope.IdempotencyKey!,
                new IdempotencyRecord(requestDigest, session.SessionId, now, approvalRequestId));
            AppendAuditLocked(session, "approval-requested", envelope, "pending", null, null, null, null);

            return new MauiTestAgentApprovalResult
            {
                Ok = true,
                Request = CloneApprovalRequest(record, includeGrant: false),
            };
        }
    }

    internal IReadOnlyList<MauiTestAgentApprovalRecord> ListApprovalRequests(
        string? agentId,
        string? agentInstanceId,
        bool includeGrant)
    {
        lock (_gate)
        {
            PurgeExpiredLocked();
            if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(agentInstanceId))
                return [];

            return _approvalRequests.Values
                .Where(request =>
                    string.Equals(request.Target.AgentId, agentId, StringComparison.Ordinal) &&
                    string.Equals(request.Target.AgentInstanceId, agentInstanceId, StringComparison.Ordinal))
                .OrderByDescending(static request => request.CreatedAt)
                .Select(request => CloneApprovalRequest(request, includeGrant))
                .ToArray();
        }
    }

    internal MauiTestAgentApprovalResult GetApprovalRequest(string? approvalRequestId, bool includeGrant)
    {
        lock (_gate)
        {
            PurgeExpiredLocked();
            if (string.IsNullOrWhiteSpace(approvalRequestId) ||
                !_approvalRequests.TryGetValue(approvalRequestId, out var request))
            {
                return ApprovalFailure(Error(
                    MauiTestAgentErrorCodes.ApprovalRequestNotFound,
                    MauiTestAgentErrorCategories.State,
                    "The approval request was not found.",
                    retryable: false));
            }

            return new MauiTestAgentApprovalResult
            {
                Ok = true,
                Request = CloneApprovalRequest(request, includeGrant),
            };
        }
    }

    internal MauiTestAgentApprovalResult ApproveApprovalRequest(
        string? approvalRequestId,
        MauiTestAgentMutationScope? approvedScope,
        MauiTestAgentTargetState? currentTargetState,
        MauiTestAgentHumanApproval? approval,
        DateTimeOffset? grantExpiresAt)
    {
        lock (_gate)
        {
            PurgeExpiredLocked();
            if (!TryGetPendingApprovalLocked(approvalRequestId, out var request, out var session, out var error))
                return ApprovalFailure(error!);
            var pendingRequest = request!;
            var owner = session!;
            if (!IsHumanHostApproval(approval))
            {
                return ApprovalFailure(Error(
                    MauiTestAgentErrorCodes.HumanApprovalRequired,
                    MauiTestAgentErrorCategories.Authorization,
                    "An explicit Workbench or host approval is required.",
                    retryable: false));
            }

            if (!TargetsMatch(pendingRequest.Target, currentTargetState) ||
                !TargetStatesMatch(pendingRequest.TargetState, currentTargetState) ||
                !CorrelationMatches(owner, pendingRequest.Correlation, requireAll: true) ||
                !ProvenanceMatches(owner.Actor, pendingRequest.Provenance))
            {
                MarkApprovalLocked(pendingRequest, MauiTestAgentApprovalStates.Stale, "target-or-revision-stale");
                AppendAuditLocked(
                    owner,
                    "approval-stale",
                    ApprovalEnvelope(pendingRequest),
                    "stale",
                    null,
                    null,
                    null,
                    MauiTestAgentErrorCodes.MutationGrantStale);
                return ApprovalFailure(Error(
                    MauiTestAgentErrorCodes.MutationGrantStale,
                    MauiTestAgentErrorCategories.State,
                    "The target, actor, plan, or flow changed before approval.",
                    retryable: false));
            }

            var scope = approvedScope ?? pendingRequest.RequestedScope;
            NormalizeApprovalScope(pendingRequest.Kind, scope);
            var scopeError = ValidateScope(scope);
            if (scopeError is not null)
                return ApprovalFailure(scopeError);
            if (!ScopeIsSubset(scope!, pendingRequest.RequestedScope) ||
                !ApprovalKindAllowsScope(pendingRequest.Kind, scope!))
            {
                return ApprovalFailure(Error(
                    MauiTestAgentErrorCodes.ApprovalRequestScopeDenied,
                    MauiTestAgentErrorCategories.Authorization,
                    "Workbench approval may narrow, but never broaden or repurpose, the agent-requested scope.",
                    retryable: false));
            }

            var grant = IssueGrantLocked(
                owner,
                scope!,
                grantExpiresAt,
                MauiTestAgentProtocolVersions.PolicyVersion,
                pendingRequest.ApprovalRequestId);
            if (!grant.Ok)
                return ApprovalFailure(grant.Error!);

            pendingRequest.State = MauiTestAgentApprovalStates.Approved;
            pendingRequest.ApprovedScope = CloneScope(scope!);
            pendingRequest.DecidedAt = _clock.GetUtcNow();
            pendingRequest.GrantId = grant.GrantId;
            pendingRequest.GrantDigest = grant.GrantDigest;
            pendingRequest.GrantExpiresAt = grant.ExpiresAt;
            AppendAuditLocked(
                owner,
                "approval-approved",
                ApprovalEnvelope(pendingRequest),
                "approved",
                grant.GrantDigest,
                null,
                null,
                null);
            return new MauiTestAgentApprovalResult
            {
                Ok = true,
                Request = CloneApprovalRequest(pendingRequest, includeGrant: false),
            };
        }
    }

    internal MauiTestAgentApprovalResult RejectApprovalRequest(
        string? approvalRequestId,
        MauiTestAgentHumanApproval? decision,
        string? reasonCode)
    {
        lock (_gate)
        {
            PurgeExpiredLocked();
            if (!TryGetPendingApprovalLocked(approvalRequestId, out var request, out var session, out var error))
                return ApprovalFailure(error!);
            var pendingRequest = request!;
            var owner = session!;
            if (!IsHumanHostDecision(decision))
            {
                return ApprovalFailure(Error(
                    MauiTestAgentErrorCodes.HumanApprovalRequired,
                    MauiTestAgentErrorCategories.Authorization,
                    "A Workbench or host decision is required.",
                    retryable: false));
            }

            MarkApprovalLocked(pendingRequest, MauiTestAgentApprovalStates.Rejected, Bounded(reasonCode, 128) ?? "human-rejected");
            AppendAuditLocked(
                owner,
                "approval-rejected",
                ApprovalEnvelope(pendingRequest),
                "rejected",
                null,
                null,
                null,
                null);
            return new MauiTestAgentApprovalResult
            {
                Ok = true,
                Request = CloneApprovalRequest(pendingRequest, includeGrant: false),
            };
        }
    }

    internal MauiTestAgentGrantIssueResult IssueGrant(MauiTestAgentGrantIssueRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            PurgeExpiredLocked();
            if (!TryGetReadableSessionLocked(request.SessionId, request.ReadCapabilityId, out var session, out var error))
                return GrantFailure(error!);

            if (!IsHumanHostApproval(request.Approval))
            {
                AppendAuditLocked(session!, "grant-denied", null, "denied", null, null, null, MauiTestAgentErrorCodes.HumanApprovalRequired);
                return GrantFailure(Error(
                    MauiTestAgentErrorCodes.HumanApprovalRequired,
                    MauiTestAgentErrorCategories.Authorization,
                    "Only an explicit human approval from a Workbench or host may issue a mutation grant.",
                    retryable: false));
            }

            if (!TargetsMatch(session!.Target, request.TargetState) ||
                !TargetStatesMatch(session.TargetState, request.TargetState))
            {
                AppendAuditLocked(session, "grant-denied", null, "denied", null, null, null, MauiTestAgentErrorCodes.MutationGrantStale);
                return GrantFailure(Error(
                    MauiTestAgentErrorCodes.MutationGrantStale,
                    MauiTestAgentErrorCategories.State,
                    "The approved target build, instance, or seed does not match the authoring session.",
                    retryable: false));
            }

            if (!CorrelationMatches(session, request.Correlation, requireAll: false))
            {
                AppendAuditLocked(session, "grant-denied", null, "denied", null, null, null, MauiTestAgentErrorCodes.MutationGrantStale);
                return GrantFailure(Error(
                    MauiTestAgentErrorCodes.MutationGrantStale,
                    MauiTestAgentErrorCategories.State,
                    "The approved plan or flow revision does not match the current authoring session.",
                    retryable: false));
            }

            var scopeError = ValidateScope(request.Scope);
            if (scopeError is not null)
                return GrantFailure(scopeError);

            return IssueGrantLocked(
                session,
                request.Scope!,
                request.ExpiresAt,
                request.PolicyVersion ?? MauiTestAgentProtocolVersions.PolicyVersion,
                approvalRequestId: null);
        }
    }

    private MauiTestAgentGrantIssueResult IssueGrantLocked(
        SessionRecord session,
        MauiTestAgentMutationScope scope,
        DateTimeOffset? requestedExpiresAt,
        string policyVersion,
        string? approvalRequestId)
    {
            if (_grants.Count >= _options.MaxGrants)
                EvictExpiredGrantsLocked();
            if (_grants.Count >= _options.MaxGrants)
            {
                return GrantFailure(Error(
                    MauiTestAgentErrorCodes.InvalidRequest,
                    MauiTestAgentErrorCategories.Capability,
                    "The broker has reached its bounded mutation-grant capacity.",
                    retryable: true));
            }

            var now = _clock.GetUtcNow();
            var expiresAt = requestedExpiresAt ?? now.Add(_options.DefaultGrantLifetime);
            if (expiresAt <= now || expiresAt > now.Add(_options.MaximumGrantLifetime) || expiresAt > session.ExpiresAt)
            {
                return GrantFailure(Error(
                    MauiTestAgentErrorCodes.InvalidRequest,
                    MauiTestAgentErrorCategories.Validation,
                    "The requested grant expiration is outside the allowed session and policy bounds.",
                    retryable: false));
            }

            var grantId = OpaqueSecret("grant");
            var grantDigest = Hash(grantId);
            var grant = new GrantRecord(
                grantDigest,
                session.SessionId,
                CloneTarget(session.Target),
                CloneTargetState(session.TargetState),
                CloneProvenance(session.Actor)!,
                SnapshotCorrelation(session),
                CloneScope(scope),
                expiresAt,
                policyVersion,
                approvalRequestId);
            _grants.Add(grantDigest, grant);
            AppendAuditLocked(session, "grant-issued", null, "approved", grantDigest, null, null, null);

            return new MauiTestAgentGrantIssueResult
            {
                Ok = true,
                GrantId = grantId,
                GrantDigest = grantDigest,
                RemainingActions = grant.RemainingActions,
                ExpiresAt = expiresAt,
            };
    }

    internal MauiTestAgentMutationAuthorizationResult AuthorizeMutation(
        MauiTestAgentMutationAuthorizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            PurgeExpiredLocked();
            var envelope = request.Envelope;
            var error = ValidateEnvelope(envelope, requireSession: true);
            if (error is not null)
                return AuthorizationFailure(error);

            if (!_sessions.TryGetValue(envelope!.Correlation!.AuthoringSessionId!, out var session))
                return AuthorizationFailure(Error(
                    MauiTestAgentErrorCodes.SessionNotFound,
                    MauiTestAgentErrorCategories.State,
                    "The authoring session was not found.",
                    retryable: false));

            if (!TryValidateActiveSessionLocked(session, out error))
                return AuthorizationFailure(error!);

            if (!TargetsMatch(session.Target, envelope.Target) ||
                !TargetsMatch(session.Target, request.CurrentTargetState) ||
                !TargetStatesMatch(session.TargetState, request.CurrentTargetState))
            {
                AppendAuditLocked(session, "mutation-denied", envelope, "denied", null, null, null, MauiTestAgentErrorCodes.TargetStale);
                return AuthorizationFailure(Error(
                    MauiTestAgentErrorCodes.TargetStale,
                    MauiTestAgentErrorCategories.Target,
                    "The target process, app build, or seed is stale. Refresh discovery and obtain a new human approval.",
                    retryable: false));
            }

            if (!CorrelationMatches(session, envelope.Correlation, requireAll: true) &&
                !CorrelationMatchesGrantSequence(session, envelope.Correlation))
            {
                AppendAuditLocked(session, "mutation-denied", envelope, "denied", null, null, null, MauiTestAgentErrorCodes.MutationGrantStale);
                return AuthorizationFailure(Error(
                    MauiTestAgentErrorCodes.MutationGrantStale,
                    MauiTestAgentErrorCategories.State,
                    "The request does not match the current plan or flow revision.",
                    retryable: false));
            }

            if (!ProvenanceMatches(session.Actor, envelope.Provenance))
            {
                AppendAuditLocked(session, "mutation-denied", envelope, "denied", null, null, null, MauiTestAgentErrorCodes.MutationGrantScopeDenied);
                return AuthorizationFailure(Error(
                    MauiTestAgentErrorCodes.MutationGrantScopeDenied,
                    MauiTestAgentErrorCategories.Authorization,
                    "The request actor, channel, or provider does not match the approved authoring session.",
                    retryable: false));
            }

            if (!string.Equals(envelope.PolicyVersion, MauiTestAgentProtocolVersions.PolicyVersion, StringComparison.Ordinal))
                return AuthorizationFailure(Error(
                    MauiTestAgentErrorCodes.MutationGrantScopeDenied,
                    MauiTestAgentErrorCategories.Authorization,
                    "The request policy version is not supported.",
                    retryable: false));

            if (envelope.DeadlineMs is not { } deadlineMs || deadlineMs <= 0 || deadlineMs > 300_000)
            {
                return AuthorizationFailure(Error(
                    MauiTestAgentErrorCodes.DeadlineExpired,
                    MauiTestAgentErrorCategories.Validation,
                    "deadlineMs must be a positive bounded duration.",
                    retryable: false));
            }

            var requestDigest = DigestMutationRequest(request);
            if (_idempotency.TryGetValue(envelope.IdempotencyKey!, out var priorIdempotency))
            {
                if (FixedEquals(priorIdempotency.RequestDigest, requestDigest) &&
                    priorIdempotency.AuthorizationId is { } priorAuthorizationId &&
                    _authorizations.TryGetValue(priorAuthorizationId, out var priorAuthorization))
                {
                    return new MauiTestAgentMutationAuthorizationResult
                    {
                        Ok = true,
                        DispatchAllowed = true,
                        AuthorizationId = priorAuthorization.AuthorizationId,
                        RemainingActions = priorAuthorization.RemainingActions,
                        GrantDigest = priorAuthorization.GrantDigest,
                    };
                }

                AppendAuditLocked(
                    session,
                    "mutation-denied",
                    envelope,
                    "idempotency-reused",
                    null,
                    null,
                    null,
                    MauiTestAgentErrorCodes.IdempotencyReused);
                return AuthorizationFailure(Error(
                    MauiTestAgentErrorCodes.IdempotencyReused,
                    MauiTestAgentErrorCategories.Conflict,
                    "This idempotency key has already been used for a different request.",
                    retryable: false));
            }

            if (string.IsNullOrWhiteSpace(envelope.ApprovalGrantId))
                return AuthorizationFailure(Error(
                    MauiTestAgentErrorCodes.MutationGrantRequired,
                    MauiTestAgentErrorCategories.Authorization,
                    "A human-issued mutation grant is required before dispatch.",
                    retryable: false));

            var grantDigest = Hash(envelope.ApprovalGrantId);
            if (!_grants.TryGetValue(grantDigest, out var grant) ||
                !FixedEquals(grantDigest, grant.GrantDigest))
            {
                if (_expiredGrantDigests.ContainsKey(grantDigest))
                {
                    return AuthorizationFailure(Error(
                        MauiTestAgentErrorCodes.MutationGrantExpired,
                        MauiTestAgentErrorCategories.Authorization,
                        "The human-issued mutation grant has expired.",
                        retryable: false));
                }
                return AuthorizationFailure(Error(
                    MauiTestAgentErrorCodes.MutationGrantRequired,
                    MauiTestAgentErrorCategories.Authorization,
                    "The mutation grant is not valid for this broker.",
                    retryable: false));
            }

            var now = _clock.GetUtcNow();
            if (grant.ExpiresAt <= now)
            {
                AppendAuditLocked(session, "mutation-denied", envelope, "denied", grantDigest, null, null, MauiTestAgentErrorCodes.MutationGrantExpired);
                return AuthorizationFailure(Error(
                    MauiTestAgentErrorCodes.MutationGrantExpired,
                    MauiTestAgentErrorCategories.Authorization,
                    "The human-issued mutation grant has expired.",
                    retryable: false));
            }

            if (grant.RemainingActions <= 0)
            {
                AppendAuditLocked(session, "mutation-denied", envelope, "denied", grantDigest, null, null, MauiTestAgentErrorCodes.MutationGrantReused);
                return AuthorizationFailure(Error(
                    MauiTestAgentErrorCodes.MutationGrantReused,
                    MauiTestAgentErrorCategories.Authorization,
                    "The mutation grant was already consumed.",
                    retryable: false));
            }

            if (!GrantMatchesSession(grant, session))
            {
                AppendAuditLocked(session, "mutation-denied", envelope, "denied", grantDigest, null, null, MauiTestAgentErrorCodes.MutationGrantStale);
                return AuthorizationFailure(Error(
                    MauiTestAgentErrorCodes.MutationGrantStale,
                    MauiTestAgentErrorCategories.State,
                    "The grant is bound to a stale target, revision, or actor.",
                    retryable: false));
            }

            var scopeError = ValidateMutationScope(grant.Scope, request);
            if (scopeError is not null)
            {
                AppendAuditLocked(session, "mutation-denied", envelope, "denied", grantDigest, null, null, scopeError.Code);
                return AuthorizationFailure(scopeError);
            }

            grant.RemainingActions--;
            if (grant.RemainingActions == 0 &&
                grant.ApprovalRequestId is { } approvalRequestId &&
                _approvalRequests.TryGetValue(approvalRequestId, out var approvalRequest) &&
                approvalRequest.State == MauiTestAgentApprovalStates.Approved)
            {
                MarkApprovalLocked(approvalRequest, MauiTestAgentApprovalStates.Consumed, "grant-consumed");
                AppendAuditLocked(
                    session,
                    "approval-consumed",
                    ApprovalEnvelope(approvalRequest),
                    "consumed",
                    grantDigest,
                    null,
                    null,
                    null);
            }
            var authorizationId = OpaqueId("auth");
            _authorizations.Add(authorizationId, new AuthorizationRecord(
                authorizationId,
                session.SessionId,
                grantDigest,
                requestDigest,
                request.Action!,
                DigestAction(request),
                now,
                grant.RemainingActions));
            _idempotency.Add(
                envelope.IdempotencyKey!,
                new IdempotencyRecord(requestDigest, session.SessionId, now, authorizationId: authorizationId));
            AppendAuditLocked(session, "mutation-authorized", envelope, "authorized", grantDigest, DigestAction(request), null, null);

            return new MauiTestAgentMutationAuthorizationResult
            {
                Ok = true,
                DispatchAllowed = true,
                AuthorizationId = authorizationId,
                RemainingActions = grant.RemainingActions,
                GrantDigest = grantDigest,
            };
        }
    }

    internal MauiTestAgentToolResult CompleteMutation(MauiTestAgentMutationCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        lock (_gate)
        {
            PurgeExpiredLocked();
            if (string.IsNullOrWhiteSpace(completion.AuthorizationId) ||
                !_authorizations.TryGetValue(completion.AuthorizationId, out var authorization) ||
                !_sessions.TryGetValue(authorization.SessionId, out var session))
            {
                return ToolFailure(Error(
                    MauiTestAgentErrorCodes.InvalidRequest,
                    MauiTestAgentErrorCategories.Authorization,
                    "The mutation authorization is not valid.",
                    retryable: false));
            }

            if (authorization.Completed)
            {
                return ToolFailure(Error(
                    MauiTestAgentErrorCodes.IdempotencyReused,
                    MauiTestAgentErrorCategories.Conflict,
                    "The mutation completion was already recorded.",
                    retryable: false));
            }

            authorization.Completed = true;
            authorization.Outcome = NormalizeOutcome(completion.Outcome);
            authorization.RunId = Bounded(completion.RunId, 256);
            var actionDigest = BoundedDigest(completion.ActionDigest) ?? authorization.ActionDigest;
            var resultDigest = BoundedDigest(completion.ResultDigest);
            var errorCode = Bounded(completion.ErrorCode, 128);
            AppendAuditLocked(
                session,
                "mutation-completed",
                null,
                authorization.Outcome!,
                authorization.GrantDigest,
                actionDigest,
                resultDigest,
                errorCode,
                authorization.RunId);

            return new MauiTestAgentToolResult { Ok = true };
        }
    }

    internal MauiTestAgentSessionResult AppendAction(
        string? sessionId,
        string? authorizationId,
        MauiTestAgentActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            PurgeExpiredLocked();
            if (!TryGetAuthorizedSessionLocked(sessionId, authorizationId, MauiTestAgentActions.All, out var session, out var error))
                return SessionFailure(error!);
            if (!MatchesActionAuthorization(
                    authorizationId,
                    request.Action,
                    DigestAction(request),
                    out error))
            {
                return SessionFailure(error!);
            }

            if (!TryCreateFlowStep(request, session!, out var step, out error))
                return SessionFailure(error!);
            ConsumeAuthorizationLocked(authorizationId);

            session!.Flow.Steps.Add(step!);
            session.FlowDigest = MauiFlowRunReportSerializer.ComputeFlowDigest(session.Flow);
            AdvanceGrantCorrelationLocked(authorizationId, session);
            InvalidatePendingApprovalsLocked(session, "draft-changed");
            AppendAuditLocked(
                session,
                "draft-action-appended",
                request.Envelope,
                "draft-updated",
                null,
                DigestAction(request),
                null,
                null);
            return new MauiTestAgentSessionResult
            {
                Ok = true,
                Snapshot = CreateSnapshot(session, includeReadCapability: false),
            };
        }
    }

    internal MauiTestAgentSessionResult AddAssertion(
        string? sessionId,
        string? authorizationId,
        MauiTestAgentAssertionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            PurgeExpiredLocked();
            if (!TryGetAuthorizedSessionLocked(
                sessionId,
                authorizationId,
                new HashSet<string>(StringComparer.Ordinal) { MauiTestAgentActions.Assert },
                out var session,
                out var error))
                return SessionFailure(error!);

            var assertion = request.Assertion;
            if (assertion is null || string.IsNullOrWhiteSpace(assertion.Kind))
            {
                return SessionFailure(Error(
                    MauiTestAgentErrorCodes.InvalidRequest,
                    MauiTestAgentErrorCategories.Validation,
                    "A typed assertion is required.",
                    retryable: false));
            }
            if (FlowSecretReference.LooksSensitive(assertion.Name, assertion.Expected, assertion.Note))
            {
                return SessionFailure(Error(
                    MauiTestAgentErrorCodes.UntrustedPolicyInput,
                    MauiTestAgentErrorCategories.Validation,
                    "Sensitive assertion text cannot be retained by the restricted protocol.",
                    retryable: false));
            }
            var assertionDigest = BuildActionDigest(
                MauiTestAgentActions.Assert,
                assertion.Selector,
                route: null,
                valueLength: assertion.Expected is null ? null : Encoding.UTF8.GetByteCount(assertion.Expected),
                valueDigest: assertion.Expected is null ? null : Hash(assertion.Expected));
            if (!MatchesActionAuthorization(
                    authorizationId,
                    MauiTestAgentActions.Assert,
                    assertionDigest,
                    out error))
            {
                return SessionFailure(error!);
            }
            ConsumeAuthorizationLocked(authorizationId);

            var target = request.StepSequence is { } sequence
                ? session!.Flow.Steps.LastOrDefault(step => step.Seq == sequence)
                : session!.Flow.Steps.LastOrDefault();
            if (target is null)
            {
                target = new FlowStep
                {
                    Seq = NextSequence(session.Flow),
                    Action = FlowActions.Assert,
                    Intent = request.Envelope?.Intent,
                };
                session.Flow.Steps.Add(target);
            }
            target.Asserts ??= [];
            target.Asserts.Add(CloneAssertion(assertion));
            session.FlowDigest = MauiFlowRunReportSerializer.ComputeFlowDigest(session.Flow);
            AdvanceGrantCorrelationLocked(authorizationId, session);
            InvalidatePendingApprovalsLocked(session, "draft-changed");
            AppendAuditLocked(session, "draft-assertion-added", request.Envelope, "draft-updated", null, assertionDigest, null, null);

            return new MauiTestAgentSessionResult
            {
                Ok = true,
                Snapshot = CreateSnapshot(session, includeReadCapability: false),
            };
        }
    }

    internal MauiTestAgentSessionResult Commit(
        MauiTestAgentSessionAccessRequest request,
        string? authorizationId)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            PurgeExpiredLocked();
            if (!TryGetAuthorizedSessionLocked(
                request.SessionId,
                authorizationId,
                new HashSet<string>(StringComparer.Ordinal) { MauiTestAgentActions.AuthorCommit },
                out var session,
                out var error))
                return SessionFailure(error!);

            var flowValidation = MauiFlowValidator.Validate(session!.Flow);
            if (!flowValidation.Ok)
            {
                return SessionFailure(Error(
                    MauiTestAgentErrorCodes.InvalidRequest,
                    MauiTestAgentErrorCategories.Validation,
                    "The draft flow is not valid: " + string.Join(" ", flowValidation.Errors.Take(4)),
                    retryable: false));
            }
            ConsumeAuthorizationLocked(authorizationId);

            session.FlowRevision++;
            session.FlowDigest = MauiFlowRunReportSerializer.ComputeFlowDigest(session.Flow);
            session.PlanRevision++;
            session.Plan = NormalizePlan(
                session.Plan,
                new MauiTestAgentRequestEnvelope
                {
                    Correlation = new MauiTestAgentCorrelation
                    {
                        PlanId = session.PlanId,
                        PlanRevision = session.PlanRevision,
                        FlowId = session.FlowId,
                        FlowRevision = session.FlowRevision,
                        FlowDigest = session.FlowDigest,
                    },
                    Provenance = session.Actor,
                    Intent = "Human-approved test-agent draft commit",
                },
                session.FlowId,
                session.Flow,
                session.PlanId,
                session.PlanRevision,
                _clock.GetUtcNow());
            var planValidation = MauiTestPlanValidator.Validate(session.Plan);
            if (!planValidation.IsValid)
            {
                return SessionFailure(Error(
                    MauiTestAgentErrorCodes.InvalidRequest,
                    MauiTestAgentErrorCategories.Validation,
                    "The draft plan is not valid: " + string.Join(" ", planValidation.Errors.Take(4)),
                    retryable: false));
            }

            session.PlanDigest = ComputePlanDigest(session.Plan);
            session.CommittedAt = _clock.GetUtcNow();
            InvalidatePendingApprovalsLocked(session, "draft-committed");
            AppendAuditLocked(session, "draft-commit", request.Envelope, "committed", null, Hash(session.FlowDigest), session.PlanDigest, null);
            return new MauiTestAgentSessionResult
            {
                Ok = true,
                Snapshot = CreateSnapshot(session, includeReadCapability: false),
            };
        }
    }

    internal MauiTestAgentSessionResult MigratePreview(MauiTestAgentSessionAccessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            PurgeExpiredLocked();
            if (!TryGetReadableSessionLocked(request.SessionId, request.ReadCapabilityId, out var session, out var error))
                return SessionFailure(error!);

            var preview = MauiFlowMigration.Preview(session!.Flow);
            var snapshot = CreateSnapshot(session, includeReadCapability: false);
            if (preview.NormalizedFlow is not null)
                snapshot.Flow = preview.NormalizedFlow;
            return new MauiTestAgentSessionResult { Ok = true, Snapshot = snapshot };
        }
    }

    internal MauiTestAgentPatchResult Patch(MauiTestAgentPatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            PurgeExpiredLocked();
            var envelope = request.Envelope;
            if (envelope?.Correlation?.AuthoringSessionId is not { Length: > 0 } sessionId)
            {
                return new MauiTestAgentPatchResult { Error = Error(
                    MauiTestAgentErrorCodes.ReadCapabilityRequired,
                    MauiTestAgentErrorCategories.Authorization,
                    "A read capability is required.",
                    retryable: false) };
            }
            if (!TryGetReadableSessionLocked(sessionId, envelope.ReadCapabilityId, out var session, out var error))
            {
                return new MauiTestAgentPatchResult { Error = error };
            }

            var operation = request.Operation?.Trim().ToLowerInvariant();
            if (operation is "apply" or "approve" or "rollback")
            {
                AppendAuditLocked(session!, "patch-denied", envelope, "denied", null, null, null, MauiTestAgentErrorCodes.PatchApplyForbidden);
                return new MauiTestAgentPatchResult
                {
                    Error = Error(
                        MauiTestAgentErrorCodes.PatchApplyForbidden,
                        MauiTestAgentErrorCategories.Authorization,
                        "Restricted test agents can only propose, preview, or reject inert patches. They cannot approve or apply a patch.",
                        retryable: false),
                };
            }

            switch (operation)
            {
                case "proposal":
                    if (request.Proposal is null)
                    {
                        return new MauiTestAgentPatchResult
                        {
                            Error = Error(
                                MauiTestAgentErrorCodes.InvalidRequest,
                                MauiTestAgentErrorCategories.Validation,
                                "An inert patch proposal is required.",
                                retryable: false),
                        };
                    }

                    var proposalNode = JsonSerializer.SerializeToNode(
                        request.Proposal,
                        MauiTestingJsonContext.Default.MauiFlowRepairProposal)!.AsObject();
                    proposalNode["proposalId"] ??= OpaqueId("proposal");
                    proposalNode.Remove("approval");
                    var proposal = proposalNode.Deserialize(MauiTestingJsonContext.Default.MauiFlowRepairProposal)
                        ?? throw new InvalidOperationException("The inert patch proposal could not be normalized.");
                    var record = new MauiTestAgentPatchRecord
                    {
                        ProposalId = proposal.ProposalId,
                        State = "proposed",
                        Proposal = proposal,
                        RecordedAt = _clock.GetUtcNow(),
                    };
                    session!.Patches[record.ProposalId!] = record;
                    AppendAuditLocked(session, "patch-proposed", envelope, "inert-proposal", null, HashProposal(proposal), null, null);
                    return new MauiTestAgentPatchResult { Ok = true, Record = ClonePatchRecord(record) };

                case "preview":
                    if (string.IsNullOrWhiteSpace(request.ProposalId))
                    {
                        return new MauiTestAgentPatchResult
                        {
                            Error = Error(
                                MauiTestAgentErrorCodes.InvalidRequest,
                                MauiTestAgentErrorCategories.Validation,
                                "proposalId is required for a patch preview.",
                                retryable: false),
                        };
                    }
                    return session!.Patches.TryGetValue(request.ProposalId, out var existing)
                        ? new MauiTestAgentPatchResult { Ok = true, Record = ClonePatchRecord(existing) }
                        : new MauiTestAgentPatchResult
                        {
                            Error = Error(
                                MauiTestAgentErrorCodes.InvalidRequest,
                                MauiTestAgentErrorCategories.State,
                                "The inert patch proposal was not found.",
                                retryable: false),
                        };

                case "reject":
                    if (string.IsNullOrWhiteSpace(request.ProposalId) ||
                        !session!.Patches.TryGetValue(request.ProposalId, out var rejected))
                    {
                        return new MauiTestAgentPatchResult
                        {
                            Error = Error(
                                MauiTestAgentErrorCodes.InvalidRequest,
                                MauiTestAgentErrorCategories.State,
                                "The inert patch proposal was not found.",
                                retryable: false),
                        };
                    }
                    rejected.State = "rejected";
                    rejected.ReasonDigest = Hash(Bounded(request.Reason, 1024) ?? string.Empty);
                    AppendAuditLocked(session, "patch-rejected", envelope, "rejected", null, null, rejected.ReasonDigest, null);
                    return new MauiTestAgentPatchResult { Ok = true, Record = ClonePatchRecord(rejected) };

                default:
                    return new MauiTestAgentPatchResult
                    {
                        Error = Error(
                            MauiTestAgentErrorCodes.UnsupportedOperation,
                            MauiTestAgentErrorCategories.Unsupported,
                            "Supported patch operations are proposal, preview, and reject.",
                            retryable: false),
                    };
            }
        }
    }

    internal MauiTestAgentAuditResult Audit(MauiTestAgentSessionAccessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            PurgeExpiredLocked();
            if (!TryGetReadableSessionLocked(request.SessionId, request.ReadCapabilityId, out var session, out var error))
                return new MauiTestAgentAuditResult { Error = error };

            var entries = _journal
                .Where(entry => string.Equals(entry.SessionId, session!.SessionId, StringComparison.Ordinal))
                .TakeLast(_options.MaxAuditEntries)
                .Select(CloneAudit)
                .ToList();
            return new MauiTestAgentAuditResult
            {
                Ok = true,
                Entries = entries,
                Truncated = _journal.Count(entry => string.Equals(entry.SessionId, session!.SessionId, StringComparison.Ordinal)) > entries.Count,
            };
        }
    }

    internal MauiTestAgentRunBindingResult BindRun(MauiTestAgentRunBindingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            PurgeExpiredLocked();
            if (!TryGetReadableSessionLocked(request.SessionId, request.ReadCapabilityId, out var session, out var error))
                return RunBindingFailure(error!);
            if (string.IsNullOrWhiteSpace(request.RunId) || string.IsNullOrWhiteSpace(request.RunCapabilityToken))
            {
                return RunBindingFailure(Error(
                    MauiTestAgentErrorCodes.InvalidRequest,
                    MauiTestAgentErrorCategories.Validation,
                    "runId and runCapabilityToken are required.",
                    retryable: false));
            }

            session!.Runs[request.RunId] = Hash(request.RunCapabilityToken);
            AppendAuditLocked(session, "run-bound", null, "bound", null, Hash(request.RunId), null, null, request.RunId);
            return new MauiTestAgentRunBindingResult { Ok = true, RunId = request.RunId };
        }
    }

    internal MauiTestAgentRunBindingResult GetRunBinding(MauiTestAgentRunBindingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            PurgeExpiredLocked();
            if (!TryGetReadableSessionLocked(request.SessionId, request.ReadCapabilityId, out var session, out var error))
                return RunBindingFailure(error!);
            if (string.IsNullOrWhiteSpace(request.RunId) || !session!.Runs.TryGetValue(request.RunId, out var capability))
            {
                return RunBindingFailure(Error(
                    MauiTestAgentErrorCodes.InvalidRequest,
                    MauiTestAgentErrorCategories.State,
                    "The requested run is not bound to this authoring session.",
                    retryable: false));
            }

            // The service stores only a hash and therefore cannot reveal a token after it has been
            // bound. The MCP process retains it for its own lifetime; this route remains a safe
            // existence check rather than a credential recovery mechanism.
            return new MauiTestAgentRunBindingResult { Ok = true, RunId = request.RunId };
        }
    }

    internal MauiTestAgentRunBindingResult ValidateRunBinding(MauiTestAgentRunBindingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            PurgeExpiredLocked();
            if (!TryGetReadableSessionLocked(request.SessionId, request.ReadCapabilityId, out var session, out var error))
                return RunBindingFailure(error!);
            if (string.IsNullOrWhiteSpace(request.RunId) ||
                string.IsNullOrWhiteSpace(request.RunCapabilityToken) ||
                !session!.Runs.TryGetValue(request.RunId, out var capability) ||
                !FixedEquals(capability, Hash(request.RunCapabilityToken)))
            {
                return RunBindingFailure(Error(
                    MauiTestAgentErrorCodes.ReadCapabilityRequired,
                    MauiTestAgentErrorCategories.Authorization,
                    "The supplied run capability is not bound to this authoring session and run.",
                    retryable: false));
            }

            return new MauiTestAgentRunBindingResult { Ok = true, RunId = request.RunId };
        }
    }

    internal MauiTestAgentSessionResult GetSnapshotForRead(string? sessionId, string? readCapabilityId)
        => Status(new MauiTestAgentSessionAccessRequest { SessionId = sessionId, ReadCapabilityId = readCapabilityId });

    private static MauiTestAgentSessionResult SessionFailure(MauiTestAgentError error)
        => new() { Error = error };

    private static MauiTestAgentGrantIssueResult GrantFailure(MauiTestAgentError error)
        => new() { Error = error };

    private static MauiTestAgentApprovalResult ApprovalFailure(MauiTestAgentError error)
        => new() { Error = error };

    private static MauiTestAgentMutationAuthorizationResult AuthorizationFailure(MauiTestAgentError error)
        => new() { Error = error, DispatchAllowed = false };

    private static MauiTestAgentRunBindingResult RunBindingFailure(MauiTestAgentError error)
        => new() { Error = error };

    private static MauiTestAgentToolResult ToolFailure(MauiTestAgentError error)
        => new() { Error = error };

    private MauiTestAgentError? ValidateEnvelope(MauiTestAgentRequestEnvelope? envelope, bool requireSession)
    {
        if (envelope is null ||
            string.IsNullOrWhiteSpace(envelope.RequestId) ||
            string.IsNullOrWhiteSpace(envelope.IdempotencyKey) ||
            envelope.Target is null ||
            string.IsNullOrWhiteSpace(envelope.Target.AgentId) ||
            string.IsNullOrWhiteSpace(envelope.Target.AgentInstanceId) ||
            envelope.Provenance is null ||
            string.IsNullOrWhiteSpace(envelope.Provenance.ActorKind) ||
            string.IsNullOrWhiteSpace(envelope.Provenance.ActorId) ||
            string.IsNullOrWhiteSpace(envelope.Provenance.Channel) ||
            string.IsNullOrWhiteSpace(envelope.Provenance.Provider) ||
            string.IsNullOrWhiteSpace(envelope.Intent) ||
            string.IsNullOrWhiteSpace(envelope.PolicyVersion))
        {
            return Error(
                MauiTestAgentErrorCodes.InvalidRequest,
                MauiTestAgentErrorCategories.Validation,
                "The request envelope requires request/idempotency IDs, an explicit target, provenance, intent, and policy version.",
                retryable: false);
        }

        if (requireSession &&
            (envelope.Correlation is null || string.IsNullOrWhiteSpace(envelope.Correlation.AuthoringSessionId)))
        {
            return Error(
                MauiTestAgentErrorCodes.SessionNotFound,
                MauiTestAgentErrorCategories.Validation,
                "An authoringSessionId is required for this request.",
                retryable: false);
        }

        if (envelope.RequestId.Length > 256 ||
            envelope.IdempotencyKey.Length > 256 ||
            envelope.Intent.Length > 1024 ||
            envelope.Provenance.ActorId!.Length > 256 ||
            envelope.Provenance.Channel!.Length > 128 ||
            envelope.Provenance.Provider!.Length > 256)
        {
            return Error(
                MauiTestAgentErrorCodes.InvalidRequest,
                MauiTestAgentErrorCategories.Validation,
                "A request-envelope field exceeds its bounded protocol length.",
                retryable: false);
        }

        return null;
    }

    private bool TryGetReadableSessionLocked(
        string? sessionId,
        string? readCapabilityId,
        out SessionRecord? session,
        out MauiTestAgentError? error)
    {
        session = null;
        error = null;
        if (string.IsNullOrWhiteSpace(sessionId) || !_sessions.TryGetValue(sessionId, out var found))
        {
            error = Error(
                MauiTestAgentErrorCodes.SessionNotFound,
                MauiTestAgentErrorCategories.State,
                "The authoring session was not found.",
                retryable: false);
            return false;
        }

        if (!TryValidateActiveSessionLocked(found, out error))
            return false;

        if (string.IsNullOrWhiteSpace(readCapabilityId) || !FixedEquals(found.ReadCapabilityDigest, Hash(readCapabilityId)))
        {
            error = Error(
                MauiTestAgentErrorCodes.ReadCapabilityRequired,
                MauiTestAgentErrorCategories.Authorization,
                "A valid session read capability is required.",
                retryable: false);
            return false;
        }

        session = found;
        return true;
    }

    private bool TryGetAuthorizedSessionLocked(
        string? sessionId,
        string? authorizationId,
        IReadOnlySet<string> allowedActions,
        out SessionRecord? session,
        out MauiTestAgentError? error)
    {
        session = null;
        error = null;
        if (string.IsNullOrWhiteSpace(sessionId) || !_sessions.TryGetValue(sessionId, out var found))
        {
            error = Error(
                MauiTestAgentErrorCodes.SessionNotFound,
                MauiTestAgentErrorCategories.State,
                "The authoring session was not found.",
                retryable: false);
            return false;
        }

        if (!TryValidateActiveSessionLocked(found, out error))
            return false;

        if (string.IsNullOrWhiteSpace(authorizationId) ||
            !_authorizations.TryGetValue(authorizationId, out var authorization) ||
            !string.Equals(authorization.SessionId, found.SessionId, StringComparison.Ordinal) ||
            !allowedActions.Contains(authorization.Action) ||
            authorization.Consumed ||
            authorization.Completed)
        {
            error = Error(
                MauiTestAgentErrorCodes.MutationGrantRequired,
                MauiTestAgentErrorCategories.Authorization,
                "The requested authoring mutation was not authorized by a valid human-issued grant.",
                retryable: false);
            return false;
        }

        session = found;
        return true;
    }

    private void ConsumeAuthorizationLocked(string? authorizationId)
    {
        if (string.IsNullOrWhiteSpace(authorizationId) ||
            !_authorizations.TryGetValue(authorizationId, out var authorization) ||
            authorization.Consumed)
        {
            throw new InvalidOperationException("The mutation authorization was not available for atomic consumption.");
        }

        authorization.Consumed = true;
    }

    private bool TryGetPendingApprovalLocked(
        string? approvalRequestId,
        out ApprovalRequestRecord? request,
        out SessionRecord? session,
        out MauiTestAgentError? error)
    {
        request = null;
        session = null;
        error = null;
        if (string.IsNullOrWhiteSpace(approvalRequestId) ||
            !_approvalRequests.TryGetValue(approvalRequestId, out var found))
        {
            error = Error(
                MauiTestAgentErrorCodes.ApprovalRequestNotFound,
                MauiTestAgentErrorCategories.State,
                "The approval request was not found.",
                retryable: false);
            return false;
        }

        if (found.State == MauiTestAgentApprovalStates.Expired)
        {
            error = Error(
                MauiTestAgentErrorCodes.ApprovalRequestExpired,
                MauiTestAgentErrorCategories.State,
                "The approval request expired before a human decision.",
                retryable: false);
            return false;
        }
        if (found.State != MauiTestAgentApprovalStates.Pending)
        {
            error = Error(
                MauiTestAgentErrorCodes.ApprovalRequestDecided,
                MauiTestAgentErrorCategories.Conflict,
                "The approval request already has a terminal decision.",
                retryable: false);
            return false;
        }

        if (!_sessions.TryGetValue(found.SessionId, out var owner) ||
            !TryValidateActiveSessionLocked(owner, out error))
        {
            MarkApprovalLocked(found, MauiTestAgentApprovalStates.Stale, "authoring-session-unavailable");
            return false;
        }

        request = found;
        session = owner;
        return true;
    }

    private bool MatchesActionAuthorization(
        string? authorizationId,
        string? action,
        string actionDigest,
        out MauiTestAgentError? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(authorizationId) ||
            !_authorizations.TryGetValue(authorizationId, out var authorization) ||
            !string.Equals(authorization.Action, action, StringComparison.Ordinal) ||
            !FixedEquals(authorization.ActionDigest, actionDigest))
        {
            error = Error(
                MauiTestAgentErrorCodes.MutationGrantScopeDenied,
                MauiTestAgentErrorCategories.Authorization,
                "The draft mutation does not match the action, selector, route, or bounded value authorized by the human grant.",
                retryable: false);
            return false;
        }
        return true;
    }

    private void AdvanceGrantCorrelationLocked(string? authorizationId, SessionRecord session)
    {
        if (string.IsNullOrWhiteSpace(authorizationId) ||
            !_authorizations.TryGetValue(authorizationId, out var authorization) ||
            !_grants.TryGetValue(authorization.GrantDigest, out var grant))
        {
            return;
        }

        grant.Correlation = SnapshotCorrelation(session);
    }

    private void InvalidatePendingApprovalsLocked(SessionRecord session, string reasonCode)
    {
        foreach (var request in _approvalRequests.Values.Where(request =>
                     string.Equals(request.SessionId, session.SessionId, StringComparison.Ordinal) &&
                     request.State == MauiTestAgentApprovalStates.Pending &&
                     !CorrelationMatches(session, request.Correlation, requireAll: true)))
        {
            MarkApprovalLocked(request, MauiTestAgentApprovalStates.Stale, reasonCode);
            AppendAuditLocked(
                session,
                "approval-stale",
                ApprovalEnvelope(request),
                "stale",
                null,
                null,
                null,
                MauiTestAgentErrorCodes.MutationGrantStale);
        }
    }

    private bool TryValidateActiveSessionLocked(SessionRecord session, out MauiTestAgentError? error)
    {
        error = null;
        if (session.State == SessionState.Abandoned)
        {
            error = Error(
                MauiTestAgentErrorCodes.SessionAbandoned,
                MauiTestAgentErrorCategories.State,
                "The authoring session was abandoned.",
                retryable: false);
            return false;
        }
        if (session.State == SessionState.Expired || session.ExpiresAt <= _clock.GetUtcNow())
        {
            session.State = SessionState.Expired;
            error = Error(
                MauiTestAgentErrorCodes.SessionExpired,
                MauiTestAgentErrorCategories.State,
                "The authoring session has expired.",
                retryable: false);
            return false;
        }
        return true;
    }

    private MauiTestAgentError? ValidateScope(MauiTestAgentMutationScope? scope)
    {
        if (scope is null || scope.AllowedActions.Count == 0 || scope.MaxActionCount is not > 0)
        {
            return Error(
                MauiTestAgentErrorCodes.InvalidRequest,
                MauiTestAgentErrorCategories.Validation,
                "A mutation grant must have explicitly allowed actions and a bounded action count.",
                retryable: false);
        }
        if (scope.MaxActionCount > _options.MaxActionsPerGrant ||
            scope.MaxValueBytes is < 0 or > 65_536 ||
            scope.AllowedActions.Count > 32 ||
            scope.AllowedSelectors.Count > 64 ||
            scope.AllowedRoutes.Count > 64 ||
            scope.AllowedSideEffectClasses.Count > 16)
        {
            return Error(
                MauiTestAgentErrorCodes.InvalidRequest,
                MauiTestAgentErrorCategories.Validation,
                "The requested grant scope exceeds the bounded test-agent policy.",
                retryable: false);
        }
        if (scope.AllowedActions.Any(action => !MauiTestAgentActions.All.Contains(action)) ||
            scope.AllowedActions.Any(action => action == "*") ||
            scope.AllowedSelectors.Any(selector => string.IsNullOrWhiteSpace(selector) || selector.Length > 512 || selector == "*") ||
            scope.AllowedRoutes.Any(route => string.IsNullOrWhiteSpace(route) || route.Length > 512 || route == "*") ||
            scope.AllowedSideEffectClasses.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 128 || value == "*"))
        {
            return Error(
                MauiTestAgentErrorCodes.InvalidRequest,
                MauiTestAgentErrorCategories.Validation,
                "Grant scopes must use explicit bounded actions, selectors, routes, and side-effect classes.",
                retryable: false);
        }
        if (scope.AllowedActions.Contains(MauiTestAgentActions.AuthorCommit, StringComparer.Ordinal) &&
            (scope.AllowedActions.Count != 1 || scope.MaxActionCount != 1))
        {
            return Error(
                MauiTestAgentErrorCodes.InvalidRequest,
                MauiTestAgentErrorCategories.Validation,
                "An author-commit grant must be single-purpose and single-use because commit advances the plan and flow revisions.",
                retryable: false);
        }
        return null;
    }

    private static bool ApprovalKindAllowsScope(string kind, MauiTestAgentMutationScope scope)
        => kind switch
        {
            MauiTestAgentApprovalKinds.Exploration =>
                scope.AllowedActions.All(static action =>
                    action is not MauiTestAgentActions.Run and
                        not MauiTestAgentActions.Cancel and
                        not MauiTestAgentActions.AuthorCommit),
            MauiTestAgentApprovalKinds.DraftChange =>
                scope.AllowedActions.All(static action =>
                    action is not MauiTestAgentActions.Run and
                        not MauiTestAgentActions.Cancel and
                        not MauiTestAgentActions.AuthorCommit),
            MauiTestAgentApprovalKinds.Assertion =>
                scope.AllowedActions.All(static action => action == MauiTestAgentActions.Assert),
            MauiTestAgentApprovalKinds.Commit =>
                scope.AllowedActions.All(static action => action == MauiTestAgentActions.AuthorCommit),
            MauiTestAgentApprovalKinds.Run =>
                scope.AllowedActions.All(static action =>
                    action is MauiTestAgentActions.Run or MauiTestAgentActions.Cancel),
            _ => false,
        };

    private static string? NormalizeApprovalKind(string? kind)
        => kind?.Trim().ToLowerInvariant() switch
        {
            MauiTestAgentApprovalKinds.Exploration or "exploration-request" => MauiTestAgentApprovalKinds.Exploration,
            MauiTestAgentApprovalKinds.DraftChange or "draft" or "draft-action" or "draft-actions" or
                "authoring" or "authoring-action" or "authoring-actions" => MauiTestAgentApprovalKinds.DraftChange,
            MauiTestAgentApprovalKinds.Assertion or "assertions" or "assertion-add" => MauiTestAgentApprovalKinds.Assertion,
            MauiTestAgentApprovalKinds.Commit or "authoring-commit" or "draft-commit" or "author-commit" =>
                MauiTestAgentApprovalKinds.Commit,
            MauiTestAgentApprovalKinds.Run or "test-run" or "execution" => MauiTestAgentApprovalKinds.Run,
            _ => null,
        };

    private static void NormalizeApprovalScope(string kind, MauiTestAgentMutationScope? scope)
    {
        if (scope is null)
            return;

        for (var index = 0; index < scope.AllowedActions.Count; index++)
        {
            scope.AllowedActions[index] = (kind, scope.AllowedActions[index].Trim().ToLowerInvariant()) switch
            {
                (MauiTestAgentApprovalKinds.Commit, "commit") => MauiTestAgentActions.AuthorCommit,
                (MauiTestAgentApprovalKinds.Run, "start") or
                (MauiTestAgentApprovalKinds.Run, "start-run") or
                (MauiTestAgentApprovalKinds.Run, "test-run") => MauiTestAgentActions.Run,
                (MauiTestAgentApprovalKinds.Run, "stop") or
                (MauiTestAgentApprovalKinds.Run, "cancel-run") => MauiTestAgentActions.Cancel,
                (_, var action) => action,
            };
        }
        scope.AllowedActions = scope.AllowedActions
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (kind == MauiTestAgentApprovalKinds.Run)
        {
            // The restricted run tool always authorizes the fixed "run" side-effect class.
            // Normalize model/host vocabulary rather than forcing trial-and-error approvals.
            scope.AllowedSideEffectClasses = ["run"];
        }
        else if (kind is MauiTestAgentApprovalKinds.DraftChange or
                 MauiTestAgentApprovalKinds.Assertion or
                 MauiTestAgentApprovalKinds.Commit)
        {
            // Draft edits and commit mutate only broker-owned authoring state. Plan side-effect
            // vocabulary such as "non-replayable" belongs to run admission, not approval scope.
            scope.AllowedSideEffectClasses = ["authoring"];
        }
    }

    private static bool ScopeIsSubset(
        MauiTestAgentMutationScope candidate,
        MauiTestAgentMutationScope requested)
        => candidate.AllowedActions.All(requested.AllowedActions.Contains) &&
           candidate.AllowedSelectors.All(requested.AllowedSelectors.Contains) &&
           candidate.AllowedRoutes.All(requested.AllowedRoutes.Contains) &&
           candidate.AllowedSideEffectClasses.All(requested.AllowedSideEffectClasses.Contains) &&
           candidate.MaxActionCount is > 0 &&
           requested.MaxActionCount is > 0 &&
           candidate.MaxActionCount <= requested.MaxActionCount &&
           (candidate.MaxValueBytes is null ||
            (requested.MaxValueBytes is null
                ? candidate.MaxValueBytes == 0
                : candidate.MaxValueBytes <= requested.MaxValueBytes));

    private static MauiTestAgentError? ValidateMutationScope(
        MauiTestAgentMutationScope scope,
        MauiTestAgentMutationAuthorizationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Action) || !scope.AllowedActions.Contains(request.Action, StringComparer.Ordinal))
        {
            return Error(
                MauiTestAgentErrorCodes.MutationGrantScopeDenied,
                MauiTestAgentErrorCategories.Authorization,
                "The requested semantic action is not in the human-approved grant scope.",
                retryable: false);
        }

        if (request.Selector is not null)
        {
            var selectorKey = SelectorScopeKey(request.Selector);
            if (selectorKey is null || !scope.AllowedSelectors.Contains(selectorKey, StringComparer.Ordinal))
            {
                return Error(
                    MauiTestAgentErrorCodes.MutationGrantScopeDenied,
                    MauiTestAgentErrorCategories.Authorization,
                    "The requested selector is not an explicit durable selector in the human-approved grant scope.",
                    retryable: false);
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Route) &&
            !scope.AllowedRoutes.Contains(request.Route, StringComparer.Ordinal))
        {
            return Error(
                MauiTestAgentErrorCodes.MutationGrantScopeDenied,
                MauiTestAgentErrorCategories.Authorization,
                "The requested route is not in the human-approved grant scope.",
                retryable: false);
        }

        if (!string.IsNullOrWhiteSpace(request.SideEffectClass) &&
            !scope.AllowedSideEffectClasses.Contains(request.SideEffectClass, StringComparer.Ordinal))
        {
            return Error(
                MauiTestAgentErrorCodes.MutationGrantScopeDenied,
                MauiTestAgentErrorCategories.Authorization,
                "The requested side-effect class is not in the human-approved grant scope.",
                retryable: false);
        }

        if (request.ValueLength is { } valueLength &&
            (valueLength < 0 || scope.MaxValueBytes is null || valueLength > scope.MaxValueBytes))
        {
            return Error(
                MauiTestAgentErrorCodes.ValueLimitExceeded,
                MauiTestAgentErrorCategories.Authorization,
                "The requested value exceeds the human-approved grant value limit.",
                retryable: false);
        }
        return null;
    }

    private static bool IsHumanHostApproval(MauiTestAgentHumanApproval? approval)
        => approval?.Approved == true &&
           approval.Actor is { ActorKind: "human" or "host", ActorId: { Length: > 0 }, Channel: { Length: > 0 }, Provider: { Length: > 0 } } &&
           approval.ApprovalChannel is "workbench" or "host";

    private static bool IsHumanHostDecision(MauiTestAgentHumanApproval? decision)
        => decision?.Actor is { ActorKind: "human" or "host", ActorId: { Length: > 0 }, Channel: { Length: > 0 }, Provider: { Length: > 0 } } &&
           decision.ApprovalChannel is "workbench" or "host";

    private static bool GrantMatchesSession(GrantRecord grant, SessionRecord session)
        => string.Equals(grant.SessionId, session.SessionId, StringComparison.Ordinal) &&
           TargetsMatch(grant.Target, session.Target) &&
           TargetStatesMatch(grant.TargetState, session.TargetState) &&
           ProvenanceMatches(grant.Actor, session.Actor) &&
           CorrelationMatches(session, grant.Correlation, requireAll: true) &&
           string.Equals(grant.PolicyVersion, MauiTestAgentProtocolVersions.PolicyVersion, StringComparison.Ordinal);

    private static bool CorrelationMatches(
        SessionRecord session,
        MauiTestAgentCorrelation? correlation,
        bool requireAll)
    {
        if (correlation is null)
            return !requireAll;
        return Matches(correlation.AuthoringSessionId, session.SessionId, requireAll) &&
               Matches(correlation.PlanId, session.PlanId, requireAll) &&
               Matches(correlation.PlanRevision, session.PlanRevision, requireAll) &&
               Matches(correlation.PlanDigest, session.PlanDigest, requireAll) &&
               Matches(correlation.FlowId, session.FlowId, requireAll) &&
               Matches(correlation.FlowRevision, session.FlowRevision, requireAll) &&
               Matches(correlation.FlowDigest, session.FlowDigest, requireAll);
    }

    private static bool CorrelationMatchesGrantSequence(
        SessionRecord session,
        MauiTestAgentCorrelation? correlation)
        => correlation is not null &&
           Matches(correlation.AuthoringSessionId, session.SessionId, required: true) &&
           Matches(correlation.PlanId, session.PlanId, required: true) &&
           Matches(correlation.PlanRevision, session.PlanRevision, required: true) &&
           Matches(correlation.PlanDigest, session.PlanDigest, required: true) &&
           Matches(correlation.FlowId, session.FlowId, required: true) &&
           Matches(correlation.FlowRevision, session.FlowRevision, required: true);

    private static bool Matches(string? supplied, string? expected, bool required)
        => required
            ? !string.IsNullOrWhiteSpace(supplied) && string.Equals(supplied, expected, StringComparison.Ordinal)
            : string.IsNullOrWhiteSpace(supplied) || string.Equals(supplied, expected, StringComparison.Ordinal);

    private static bool Matches(int? supplied, int expected, bool required)
        => required ? supplied == expected : supplied is null || supplied == expected;

    private static bool TargetsMatch(MauiTestAgentTarget? expected, MauiTestAgentTarget? actual)
        => expected is not null &&
           actual is not null &&
           string.Equals(expected.AgentId, actual.AgentId, StringComparison.Ordinal) &&
           string.Equals(expected.AgentInstanceId, actual.AgentInstanceId, StringComparison.Ordinal);

    private static bool TargetsMatch(MauiTestAgentTarget? expected, MauiTestAgentTargetState? actual)
        => expected is not null &&
           actual is not null &&
           string.Equals(expected.AgentId, actual.AgentId, StringComparison.Ordinal) &&
           string.Equals(expected.AgentInstanceId, actual.AgentInstanceId, StringComparison.Ordinal);

    private static bool TargetStatesMatch(MauiTestAgentTargetState? expected, MauiTestAgentTargetState? actual)
        => expected is not null &&
           actual is not null &&
           string.Equals(expected.AgentId, actual.AgentId, StringComparison.Ordinal) &&
           string.Equals(expected.AgentInstanceId, actual.AgentInstanceId, StringComparison.Ordinal) &&
           MatchesOptional(expected.AppBuildFingerprint, actual.AppBuildFingerprint) &&
           MatchesOptional(expected.SeedFingerprint, actual.SeedFingerprint) &&
           MatchesOptional(expected.BackendStateFingerprint, actual.BackendStateFingerprint) &&
           MatchesOptional(expected.Route, actual.Route) &&
           MatchesOptional(expected.Window, actual.Window);

    private static bool MatchesOptional(string? expected, string? actual)
        => IsUnspecifiedFingerprint(expected) || string.Equals(expected, actual, StringComparison.Ordinal);

    private static bool IsUnspecifiedFingerprint(string? value)
        => string.IsNullOrWhiteSpace(value) ||
           string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "unknown:unknown", StringComparison.OrdinalIgnoreCase);

    private static bool ProvenanceMatches(MauiActorProvenance? expected, MauiActorProvenance? actual)
        => expected is not null &&
           actual is not null &&
           string.Equals(expected.ActorKind, actual.ActorKind, StringComparison.Ordinal) &&
           string.Equals(expected.ActorId, actual.ActorId, StringComparison.Ordinal) &&
           string.Equals(expected.Channel, actual.Channel, StringComparison.Ordinal) &&
           string.Equals(expected.Provider, actual.Provider, StringComparison.Ordinal);

    private static bool TryCreateFlowStep(
        MauiTestAgentActionRequest request,
        SessionRecord session,
        out FlowStep? step,
        out MauiTestAgentError? error)
    {
        step = null;
        error = null;
        if (string.IsNullOrWhiteSpace(request.Action) ||
            request.Action is not (MauiTestAgentActions.Tap or MauiTestAgentActions.Fill or MauiTestAgentActions.Scroll or MauiTestAgentActions.Navigate or MauiTestAgentActions.Back))
        {
            error = Error(
                MauiTestAgentErrorCodes.UnsupportedOperation,
                MauiTestAgentErrorCategories.Unsupported,
                "Only tap, fill, scroll, navigate, and back are typed semantic test actions.",
                retryable: false);
            return false;
        }
        if (FlowSecretReference.LooksSensitive(request.Selector?.AutomationId, request.Value, request.Route))
        {
            error = Error(
                MauiTestAgentErrorCodes.UntrustedPolicyInput,
                MauiTestAgentErrorCategories.Validation,
                "Sensitive values cannot be recorded in a restricted test-agent draft.",
                retryable: false);
            return false;
        }

        var selector = CloneSelector(request.Selector);
        step = new FlowStep
        {
            Seq = NextSequence(session.Flow),
            Action = request.Action,
            Intent = request.Envelope?.Intent,
            Target = selector,
            Value = request.Action == MauiTestAgentActions.Navigate ? request.Route : request.Value,
            Args = request.Action switch
            {
                MauiTestAgentActions.Fill => new FlowStepArgs { Selector = selector, Text = request.Value },
                MauiTestAgentActions.Scroll => new FlowStepArgs
                {
                    Selector = selector,
                    Dx = request.DeltaX,
                    Dy = request.DeltaY,
                    ItemIndex = request.ItemIndex,
                },
                MauiTestAgentActions.Navigate => new FlowStepArgs { Route = request.Route },
                _ => selector is null ? null : new FlowStepArgs { Selector = selector },
            },
            Fragile = FlowSelector.IsFragile(selector),
        };
        return true;
    }

    private static string? SelectorScopeKey(FlowSelector selector)
        => MauiTestAgentSelectorScopeKey.FromSelector(selector);

    private static int NextSequence(MauiFlow flow)
        => flow.Steps.Count == 0 ? 1 : flow.Steps.Max(step => step.Seq) + 1;

    private static string NormalizeOutcome(string? outcome)
        => outcome is "completed" or "queued" or "rejected" or "unknown-completion"
            ? outcome
            : "completed";

    private static MauiTestAgentError Error(
        string code,
        string category,
        string message,
        bool retryable)
        => new()
        {
            Code = code,
            Category = category,
            Message = message,
            Retryable = retryable,
        };

    private void PurgeExpiredLocked()
    {
        var now = _clock.GetUtcNow();
        foreach (var session in _sessions.Values)
        {
            if (session.State == SessionState.Active && session.ExpiresAt <= now)
            {
                session.State = SessionState.Expired;
                AppendAuditLocked(session, "session-expired", null, "expired", null, null, null, MauiTestAgentErrorCodes.SessionExpired);
            }
        }

        foreach (var approval in _approvalRequests.Values)
        {
            if (approval.State != MauiTestAgentApprovalStates.Pending)
                continue;

            if (approval.ExpiresAt <= now)
            {
                MarkApprovalLocked(approval, MauiTestAgentApprovalStates.Expired, "decision-window-expired");
                if (_sessions.TryGetValue(approval.SessionId, out var session))
                {
                    AppendAuditLocked(
                        session,
                        "approval-expired",
                        ApprovalEnvelope(approval),
                        "expired",
                        null,
                        null,
                        null,
                        MauiTestAgentErrorCodes.ApprovalRequestExpired);
                }
                continue;
            }

            if (!_sessions.TryGetValue(approval.SessionId, out var owner) ||
                owner.State != SessionState.Active ||
                !CorrelationMatches(owner, approval.Correlation, requireAll: true) ||
                !TargetsMatch(owner.Target, approval.Target) ||
                !ProvenanceMatches(owner.Actor, approval.Provenance))
            {
                MarkApprovalLocked(approval, MauiTestAgentApprovalStates.Stale, "target-or-revision-stale");
                if (owner is not null)
                {
                    AppendAuditLocked(
                        owner,
                        "approval-stale",
                        ApprovalEnvelope(approval),
                        "stale",
                        null,
                        null,
                        null,
                        MauiTestAgentErrorCodes.MutationGrantStale);
                }
            }
        }

        foreach (var key in _grants
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            ExpireGrantLocked(key, now);
        }
        foreach (var key in _expiredGrantDigests
                     .Where(pair => pair.Value < now - _options.AuditRetention)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _expiredGrantDigests.Remove(key);
        }

        foreach (var approval in _approvalRequests.Values)
        {
            if (approval.State == MauiTestAgentApprovalStates.Approved &&
                approval.GrantExpiresAt <= now)
            {
                MarkApprovalLocked(approval, MauiTestAgentApprovalStates.Expired, "grant-expired");
            }
        }
        foreach (var key in _approvalRequests
                     .Where(pair =>
                         pair.Value.State != MauiTestAgentApprovalStates.Pending &&
                         (pair.Value.DecidedAt ?? pair.Value.ExpiresAt) < now - _options.ApprovalRetention)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _approvalRequests.Remove(key);
        }

        while (_journal.First is { Value.At: var at } && at < now - _options.AuditRetention)
            _journal.RemoveFirst();
        while (_journal.Count > _options.MaxAuditEntries)
            _journal.RemoveFirst();

        var staleIdempotency = _idempotency
            .Where(pair => pair.Value.CreatedAt < now - _options.IdempotencyRetention)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var key in staleIdempotency)
            _idempotency.Remove(key);
    }

    private void EvictExpiredGrantsLocked()
    {
        var now = _clock.GetUtcNow();
        foreach (var key in _grants.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
            ExpireGrantLocked(key, now);
    }

    private void EvictOldestApprovalRequestLocked()
    {
        var removable = _approvalRequests.Values
            .Where(static request => request.State != MauiTestAgentApprovalStates.Pending)
            .OrderBy(static request => request.DecidedAt ?? request.ExpiresAt)
            .FirstOrDefault();
        if (removable is not null)
            _approvalRequests.Remove(removable.ApprovalRequestId);
    }

    private void ExpireGrantLocked(string grantDigest, DateTimeOffset now)
    {
        if (_grants.Remove(grantDigest))
            _expiredGrantDigests[grantDigest] = now;
        while (_expiredGrantDigests.Count > _options.MaxGrants)
        {
            var oldest = _expiredGrantDigests.OrderBy(pair => pair.Value).First();
            _expiredGrantDigests.Remove(oldest.Key);
        }
    }

    private void EvictOldestTerminalSessionLocked()
    {
        var removable = _sessions.Values
            .Where(session => session.State is SessionState.Abandoned or SessionState.Expired)
            .OrderBy(session => session.ExpiresAt)
            .FirstOrDefault();
        if (removable is not null)
        {
            _sessions.Remove(removable.SessionId);
            foreach (var key in _approvalRequests
                         .Where(pair => string.Equals(pair.Value.SessionId, removable.SessionId, StringComparison.Ordinal))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _approvalRequests.Remove(key);
            }
        }
    }

    private void AppendAuditLocked(
        SessionRecord session,
        string kind,
        MauiTestAgentRequestEnvelope? envelope,
        string decision,
        string? grantDigest,
        string? actionDigest,
        string? resultDigest,
        string? errorCode,
        string? runId = null)
    {
        _journal.AddLast(new MauiTestAgentAuditEntry
        {
            Sequence = ++_auditSequence,
            At = _clock.GetUtcNow(),
            Kind = kind,
            RequestId = Bounded(envelope?.RequestId, 256),
            SessionId = session.SessionId,
            AgentId = session.Target.AgentId,
            AgentInstanceId = session.Target.AgentInstanceId,
            PlanRevision = session.PlanRevision,
            FlowRevision = session.FlowRevision,
            RunId = Bounded(runId ?? envelope?.Correlation?.RunId, 256),
            PolicyDecision = Bounded(decision, 128),
            IntentDigest = string.IsNullOrWhiteSpace(envelope?.Intent) ? null : Hash(envelope.Intent),
            GrantDigest = BoundedDigest(grantDigest),
            ActionDigest = BoundedDigest(actionDigest),
            ResultDigest = BoundedDigest(resultDigest),
            ErrorCode = Bounded(errorCode, 128),
        });
        while (_journal.Count > _options.MaxAuditEntries)
            _journal.RemoveFirst();
    }

    private MauiTestAgentAuthoringSnapshot CreateSnapshot(SessionRecord session, bool includeReadCapability)
        => new()
        {
            SessionId = session.SessionId,
            State = session.State.ToWireValue(),
            ExpiresAt = session.ExpiresAt,
            Target = CloneTarget(session.Target),
            TargetState = CloneTargetState(session.TargetState),
            Plan = ClonePlan(session.Plan),
            PlanDigest = session.PlanDigest,
            Flow = CloneFlow(session.Flow),
            FlowDigest = session.FlowDigest,
            FlowRevision = session.FlowRevision,
            CommittedAt = session.CommittedAt,
            ApprovalRequests = _approvalRequests.Values
                .Where(request => string.Equals(request.SessionId, session.SessionId, StringComparison.Ordinal))
                .OrderByDescending(static request => request.CreatedAt)
                .Select(request => CloneApprovalRequest(request, includeGrant: true))
                .ToList(),
            ReadCapabilityId = includeReadCapability ? string.Empty : null,
        };

    private static MauiTestPlan NormalizePlan(
        MauiTestPlan? source,
        MauiTestAgentRequestEnvelope envelope,
        string flowId,
        MauiFlow flow,
        string planId,
        int revision,
        DateTimeOffset now)
    {
        var plan = ClonePlan(source) ?? new MauiTestPlan
        {
            Title = "Restricted test-agent draft",
            Goal = envelope.Intent,
            Reset = new MauiTestResetRequirement { Required = false, Strategy = "host-owned" },
            SideEffectPolicy = MauiFlowSideEffectPolicies.None,
            Provenance = CloneProvenance(envelope.Provenance!)!,
        };
        var node = JsonSerializer.SerializeToNode(plan, MauiTestingJsonContext.Default.MauiTestPlan)!.AsObject();
        node["schema"] = 1;
        node["planId"] = planId;
        node["revision"] = revision;
        node["flow"] = JsonSerializer.SerializeToNode(new MauiFlowReference
        {
            FlowId = flowId,
            Revision = Math.Max(1, envelope.Correlation?.FlowRevision ?? 1),
            Digest = MauiFlowRunReportSerializer.ComputeFlowDigest(flow),
        }, MauiTestingJsonContext.Default.MauiFlowReference);
        node["goal"] ??= Bounded(envelope.Intent, 512);
        node["sideEffectPolicy"] ??= MauiFlowSideEffectPolicies.None;
        node["reset"] ??= JsonSerializer.SerializeToNode(new MauiTestResetRequirement
        {
            Required = false,
            Strategy = "host-owned",
        }, MauiTestingJsonContext.Default.MauiTestResetRequirement);
        node["provenance"] ??= JsonSerializer.SerializeToNode(CloneProvenance(envelope.Provenance!), MauiTestingJsonContext.Default.MauiActorProvenance);
        return node.Deserialize(MauiTestingJsonContext.Default.MauiTestPlan)
            ?? throw new InvalidOperationException("The authoring plan could not be normalized.");
    }

    private static MauiTestAgentCorrelation SnapshotCorrelation(SessionRecord session)
        => new()
        {
            AuthoringSessionId = session.SessionId,
            PlanId = session.PlanId,
            PlanRevision = session.PlanRevision,
            PlanDigest = session.PlanDigest,
            FlowId = session.FlowId,
            FlowRevision = session.FlowRevision,
            FlowDigest = session.FlowDigest,
        };

    internal static string ComputePlanDigest(MauiTestPlan plan)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(plan, MauiTestingJsonContext.Default.MauiTestPlan);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string DigestEnvelope(MauiTestAgentRequestEnvelope envelope)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, MauiTestingJsonContext.Default.MauiTestAgentRequestEnvelope);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string DigestMutationRequest(MauiTestAgentMutationAuthorizationRequest request)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, MauiTestingJsonContext.Default.MauiTestAgentMutationAuthorizationRequest);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string DigestApprovalRequest(MauiTestAgentApprovalSubmitRequest request)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            request,
            MauiTestingJsonContext.Default.MauiTestAgentApprovalSubmitRequest);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string DigestAction(MauiTestAgentMutationAuthorizationRequest request)
        => BuildActionDigest(request.Action, request.Selector, request.Route, request.ValueLength, request.ValueDigest);

    private static string DigestAction(MauiTestAgentActionRequest request)
        => BuildActionDigest(
            request.Action,
            request.Selector,
            request.Route,
            request.Value is null ? null : Encoding.UTF8.GetByteCount(request.Value),
            request.Value is null ? null : Hash(request.Value));

    private static string BuildActionDigest(
        string? action,
        FlowSelector? selector,
        string? route,
        int? valueLength,
        string? valueDigest)
        => Hash(string.Join(
            "\n",
            action,
            SelectorScopeKey(selector ?? new FlowSelector()),
            route,
            valueLength,
            valueDigest));

    private static string HashProposal(MauiFlowRepairProposal proposal)
        => Hash(JsonSerializer.Serialize(proposal, MauiTestingJsonContext.Default.MauiFlowRepairProposal));

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty))).ToLowerInvariant();

    private static string OpaqueId(string prefix)
        => $"{prefix}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}";

    private static string OpaqueSecret(string prefix)
        => $"{prefix}_{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";

    private static bool FixedEquals(string left, string right)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    private static string? Bounded(string? value, int maximum)
        => string.IsNullOrWhiteSpace(value) ? null : value.Length <= maximum ? value : value[..maximum];

    private static string? BoundedDigest(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Length <= 128 ? value : value[..128];

    private static MauiTestAgentTarget CloneTarget(MauiTestAgentTarget source) => new()
    {
        AgentId = source.AgentId,
        AgentInstanceId = source.AgentInstanceId,
        AppBuildFingerprint = source.AppBuildFingerprint,
        SeedFingerprint = source.SeedFingerprint,
        BackendStateFingerprint = source.BackendStateFingerprint,
    };

    private static MauiTestAgentTargetState CloneTargetState(MauiTestAgentTargetState source) => new()
    {
        AgentId = source.AgentId,
        AgentInstanceId = source.AgentInstanceId,
        AppBuildFingerprint = source.AppBuildFingerprint,
        SeedFingerprint = source.SeedFingerprint,
        BackendStateFingerprint = source.BackendStateFingerprint,
        Route = source.Route,
        Window = source.Window,
        ObservedAt = source.ObservedAt,
    };

    private static MauiActorProvenance? CloneProvenance(MauiActorProvenance? source) => source is null
        ? null
        : new MauiActorProvenance
        {
            ActorKind = source.ActorKind,
            ActorId = source.ActorId,
            Channel = source.Channel,
            Provider = source.Provider,
            Intent = source.Intent,
            RecordedAt = source.RecordedAt,
        };

    private static MauiTestAgentMutationScope CloneScope(MauiTestAgentMutationScope source) => new()
    {
        AllowedActions = source.AllowedActions.ToList(),
        AllowedSelectors = source.AllowedSelectors.ToList(),
        AllowedRoutes = source.AllowedRoutes.ToList(),
        AllowedSideEffectClasses = source.AllowedSideEffectClasses.ToList(),
        MaxActionCount = source.MaxActionCount,
        MaxValueBytes = source.MaxValueBytes,
    };

    private static MauiTestAgentApprovalRecord CloneApprovalRequest(
        ApprovalRequestRecord source,
        bool includeGrant)
        => new()
        {
            ApprovalRequestId = source.ApprovalRequestId,
            Kind = source.Kind,
            State = source.State,
            SessionId = source.SessionId,
            Intent = source.Intent,
            Provenance = CloneProvenance(source.Provenance),
            Target = CloneTarget(source.Target),
            TargetState = CloneTargetState(source.TargetState),
            Correlation = new MauiTestAgentCorrelation
            {
                AuthoringSessionId = source.Correlation.AuthoringSessionId,
                PlanId = source.Correlation.PlanId,
                PlanRevision = source.Correlation.PlanRevision,
                PlanDigest = source.Correlation.PlanDigest,
                FlowId = source.Correlation.FlowId,
                FlowRevision = source.Correlation.FlowRevision,
                FlowDigest = source.Correlation.FlowDigest,
                RunId = source.Correlation.RunId,
            },
            RequestedScope = CloneScope(source.RequestedScope),
            ApprovedScope = source.ApprovedScope is null ? null : CloneScope(source.ApprovedScope),
            CreatedAt = source.CreatedAt,
            ExpiresAt = source.ExpiresAt,
            DecidedAt = source.DecidedAt,
            ReasonCode = source.ReasonCode,
            GrantId = includeGrant && source.State == MauiTestAgentApprovalStates.Approved
                ? source.GrantId
                : null,
            GrantExpiresAt = source.GrantExpiresAt,
        };

    private static MauiTestAgentRequestEnvelope ApprovalEnvelope(ApprovalRequestRecord source)
        => new()
        {
            RequestId = source.RequestId,
            Target = CloneTarget(source.Target),
            Correlation = new MauiTestAgentCorrelation
            {
                AuthoringSessionId = source.Correlation.AuthoringSessionId,
                PlanId = source.Correlation.PlanId,
                PlanRevision = source.Correlation.PlanRevision,
                PlanDigest = source.Correlation.PlanDigest,
                FlowId = source.Correlation.FlowId,
                FlowRevision = source.Correlation.FlowRevision,
                FlowDigest = source.Correlation.FlowDigest,
                RunId = source.Correlation.RunId,
            },
            Provenance = CloneProvenance(source.Provenance),
            Intent = source.Intent,
            PolicyVersion = MauiTestAgentProtocolVersions.PolicyVersion,
        };

    private void MarkApprovalLocked(ApprovalRequestRecord request, string state, string reasonCode)
    {
        request.State = state;
        request.ReasonCode = Bounded(reasonCode, 128);
        request.DecidedAt ??= _clock.GetUtcNow();
    }

    private static MauiFlow? CloneFlow(MauiFlow? source)
    {
        if (source is null)
            return null;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(source, MauiFlowJsonContext.Default.MauiFlow);
        return JsonSerializer.Deserialize(bytes, MauiFlowJsonContext.Default.MauiFlow);
    }

    private static MauiTestPlan? ClonePlan(MauiTestPlan? source)
    {
        if (source is null)
            return null;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(source, MauiTestingJsonContext.Default.MauiTestPlan);
        return JsonSerializer.Deserialize(bytes, MauiTestingJsonContext.Default.MauiTestPlan);
    }

    private static FlowSelector? CloneSelector(FlowSelector? source)
    {
        if (source is null)
            return null;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(source, MauiFlowJsonContext.Default.FlowSelector);
        return JsonSerializer.Deserialize(bytes, MauiFlowJsonContext.Default.FlowSelector);
    }

    private static FlowAssert CloneAssertion(FlowAssert source)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(source, MauiFlowJsonContext.Default.FlowAssert);
        return JsonSerializer.Deserialize(bytes, MauiFlowJsonContext.Default.FlowAssert)
            ?? throw new InvalidOperationException("The assertion could not be cloned.");
    }

    private static MauiFlowRepairProposal CloneProposal(MauiFlowRepairProposal source)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(source, MauiTestingJsonContext.Default.MauiFlowRepairProposal);
        return JsonSerializer.Deserialize(bytes, MauiTestingJsonContext.Default.MauiFlowRepairProposal)
            ?? throw new InvalidOperationException("The patch proposal could not be cloned.");
    }

    private static MauiTestAgentPatchRecord ClonePatchRecord(MauiTestAgentPatchRecord source) => new()
    {
        ProposalId = source.ProposalId,
        State = source.State,
        Proposal = source.Proposal is null ? null : CloneProposal(source.Proposal),
        ReasonDigest = source.ReasonDigest,
        RecordedAt = source.RecordedAt,
    };

    private static MauiTestAgentAuditEntry CloneAudit(MauiTestAgentAuditEntry source) => new()
    {
        Sequence = source.Sequence,
        At = source.At,
        Kind = source.Kind,
        RequestId = source.RequestId,
        SessionId = source.SessionId,
        AgentId = source.AgentId,
        AgentInstanceId = source.AgentInstanceId,
        PlanRevision = source.PlanRevision,
        FlowRevision = source.FlowRevision,
        RunId = source.RunId,
        PolicyDecision = source.PolicyDecision,
        IntentDigest = source.IntentDigest,
        GrantDigest = source.GrantDigest,
        ActionDigest = source.ActionDigest,
        ResultDigest = source.ResultDigest,
        ErrorCode = source.ErrorCode,
    };

    private sealed class SessionRecord
    {
        public SessionRecord(
            string sessionId,
            string readCapabilityDigest,
            MauiTestAgentTarget target,
            MauiTestAgentTargetState targetState,
            MauiActorProvenance actor,
            string planId,
            int planRevision,
            string flowId,
            int flowRevision,
            MauiTestPlan plan,
            MauiFlow flow,
            string flowDigest,
            DateTimeOffset createdAt,
            DateTimeOffset expiresAt)
        {
            SessionId = sessionId;
            ReadCapabilityDigest = readCapabilityDigest;
            Target = CloneTarget(target);
            TargetState = CloneTargetState(targetState);
            Actor = CloneProvenance(actor)!;
            PlanId = planId;
            PlanRevision = planRevision;
            FlowId = flowId;
            FlowRevision = flowRevision;
            Plan = ClonePlan(plan)!;
            PlanDigest = ComputePlanDigest(Plan);
            Flow = CloneFlow(flow)!;
            FlowDigest = flowDigest;
            CreatedAt = createdAt;
            ExpiresAt = expiresAt;
        }

        public string SessionId { get; }
        public string ReadCapabilityDigest { get; }
        public MauiTestAgentTarget Target { get; }
        public MauiTestAgentTargetState TargetState { get; }
        public MauiActorProvenance Actor { get; }
        public string PlanId { get; }
        public int PlanRevision { get; set; }
        public string FlowId { get; }
        public int FlowRevision { get; set; }
        public MauiTestPlan Plan { get; set; }
        public string PlanDigest { get; set; }
        public MauiFlow Flow { get; }
        public string FlowDigest { get; set; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset ExpiresAt { get; }
        public DateTimeOffset? CommittedAt { get; set; }
        public SessionState State { get; set; } = SessionState.Active;
        public Dictionary<string, MauiTestAgentPatchRecord> Patches { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Runs { get; } = new(StringComparer.Ordinal);
    }

    private sealed class ApprovalRequestRecord
    {
        public ApprovalRequestRecord(
            string approvalRequestId,
            string kind,
            string sessionId,
            string requestId,
            string intent,
            MauiTestAgentTarget target,
            MauiTestAgentTargetState targetState,
            MauiActorProvenance provenance,
            MauiTestAgentCorrelation correlation,
            MauiTestAgentMutationScope requestedScope,
            DateTimeOffset createdAt,
            DateTimeOffset expiresAt)
        {
            ApprovalRequestId = approvalRequestId;
            Kind = kind;
            SessionId = sessionId;
            RequestId = requestId;
            Intent = intent;
            Target = CloneTarget(target);
            TargetState = CloneTargetState(targetState);
            Provenance = CloneProvenance(provenance)!;
            Correlation = correlation;
            RequestedScope = CloneScope(requestedScope);
            CreatedAt = createdAt;
            ExpiresAt = expiresAt;
        }

        public string ApprovalRequestId { get; }
        public string Kind { get; }
        public string SessionId { get; }
        public string RequestId { get; }
        public string Intent { get; }
        public MauiTestAgentTarget Target { get; }
        public MauiTestAgentTargetState TargetState { get; }
        public MauiActorProvenance Provenance { get; }
        public MauiTestAgentCorrelation Correlation { get; set; }
        public MauiTestAgentMutationScope RequestedScope { get; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset ExpiresAt { get; }
        public string State { get; set; } = MauiTestAgentApprovalStates.Pending;
        public MauiTestAgentMutationScope? ApprovedScope { get; set; }
        public DateTimeOffset? DecidedAt { get; set; }
        public string? ReasonCode { get; set; }
        public string? GrantId { get; set; }
        public string? GrantDigest { get; set; }
        public DateTimeOffset? GrantExpiresAt { get; set; }
    }

    private sealed class GrantRecord
    {
        public GrantRecord(
            string grantDigest,
            string sessionId,
            MauiTestAgentTarget target,
            MauiTestAgentTargetState targetState,
            MauiActorProvenance actor,
            MauiTestAgentCorrelation correlation,
            MauiTestAgentMutationScope scope,
            DateTimeOffset expiresAt,
            string policyVersion,
            string? approvalRequestId)
        {
            GrantDigest = grantDigest;
            SessionId = sessionId;
            Target = target;
            TargetState = targetState;
            Actor = actor;
            Correlation = correlation;
            Scope = scope;
            RemainingActions = scope.MaxActionCount!.Value;
            ExpiresAt = expiresAt;
            PolicyVersion = policyVersion;
            ApprovalRequestId = approvalRequestId;
        }

        public string GrantDigest { get; }
        public string SessionId { get; }
        public MauiTestAgentTarget Target { get; }
        public MauiTestAgentTargetState TargetState { get; }
        public MauiActorProvenance Actor { get; }
        public MauiTestAgentCorrelation Correlation { get; set; }
        public MauiTestAgentMutationScope Scope { get; }
        public int RemainingActions { get; set; }
        public DateTimeOffset ExpiresAt { get; }
        public string PolicyVersion { get; }
        public string? ApprovalRequestId { get; }
    }

    private sealed class AuthorizationRecord
    {
        public AuthorizationRecord(
            string authorizationId,
            string sessionId,
            string grantDigest,
            string requestDigest,
            string action,
            string actionDigest,
            DateTimeOffset createdAt,
            int remainingActions)
        {
            AuthorizationId = authorizationId;
            SessionId = sessionId;
            GrantDigest = grantDigest;
            RequestDigest = requestDigest;
            Action = action;
            CreatedAt = createdAt;
            ActionDigest = actionDigest;
            RemainingActions = remainingActions;
        }

        public string AuthorizationId { get; }
        public string SessionId { get; }
        public string GrantDigest { get; }
        public string RequestDigest { get; }
        public string Action { get; }
        public string ActionDigest { get; }
        public DateTimeOffset CreatedAt { get; }
        public int RemainingActions { get; }
        public bool Consumed { get; set; }
        public bool Completed { get; set; }
        public string? Outcome { get; set; }
        public string? RunId { get; set; }
    }

    private sealed class IdempotencyRecord
    {
        public IdempotencyRecord(
            string requestDigest,
            string? sessionId,
            DateTimeOffset createdAt,
            string? approvalRequestId = null,
            string? authorizationId = null)
        {
            RequestDigest = requestDigest;
            SessionId = sessionId;
            CreatedAt = createdAt;
            ApprovalRequestId = approvalRequestId;
            AuthorizationId = authorizationId;
        }

        public string RequestDigest { get; }
        public string? SessionId { get; }
        public DateTimeOffset CreatedAt { get; }
        public string? ApprovalRequestId { get; }
        public string? AuthorizationId { get; }
    }

    private enum SessionState
    {
        Active,
        Abandoned,
        Expired,
    }
}

internal static class TestAgentSessionStateExtensions
{
    internal static string ToWireValue(this object state) => state.ToString()!.ToLowerInvariant();
}

internal sealed class TestAgentSessionServiceOptions
{
    public int MaxSessions { get; init; } = 32;
    public int MaxGrants { get; init; } = 128;
    public int MaxApprovalRequests { get; init; } = 128;
    public int MaxAuditEntries { get; init; } = 512;
    public int MaxActionsPerGrant { get; init; } = 64;
    public TimeSpan SessionLifetime { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan ApprovalRequestLifetime { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan ApprovalRetention { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan DefaultGrantLifetime { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan MaximumGrantLifetime { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan AuditRetention { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan IdempotencyRetention { get; init; } = TimeSpan.FromHours(1);
}
