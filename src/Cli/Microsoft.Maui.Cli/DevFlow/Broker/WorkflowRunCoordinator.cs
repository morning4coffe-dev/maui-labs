using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Maui.DevFlow.Driver;
using Testing = Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.Cli.DevFlow.Broker;

/// <summary>
/// Coordinates one bounded, mutating flow replay for a connected agent instance. It owns broker
/// lifecycle state, idempotency, the mutation-lease transaction, and retained reports; the public
/// Testing package remains the only replay implementation.
/// </summary>
internal sealed class WorkflowRunCoordinator : IDisposable
{
    private const int MaxLifecycleEvents = 128;

    private readonly object _gate = new();
    private readonly IWorkflowMutationLeaseRegistry _leases;
    private readonly Func<WorkflowRunExecution, CancellationToken, Task<Testing.FlowReplayReport>> _execute;
    private readonly Func<WorkflowRunLedgerControl, CancellationToken, Task<WorkflowRunLedgerControlResult>>? _controlLedger;
    private readonly WorkflowRunCoordinatorOptions _options;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, RunRecord> _runs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IdempotencyEntry> _idempotency = new(StringComparer.Ordinal);
    private readonly Dictionary<WorkflowRunTargetKey, string> _activeTargets = [];
    private long _terminalOrder;
    private bool _disposed;

    public WorkflowRunCoordinator(
        IWorkflowMutationLeaseRegistry leases,
        Func<WorkflowRunExecution, CancellationToken, Task<Testing.FlowReplayReport>> execute,
        WorkflowRunCoordinatorOptions? options = null,
        TimeProvider? clock = null,
        Func<WorkflowRunLedgerControl, CancellationToken, Task<WorkflowRunLedgerControlResult>>? controlLedger = null)
    {
        _leases = leases ?? throw new ArgumentNullException(nameof(leases));
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _controlLedger = controlLedger;
        _options = options ?? new WorkflowRunCoordinatorOptions();
        _clock = clock ?? TimeProvider.System;

        if (_options.MaxRetainedTerminalRuns < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "At least one terminal run must be retained.");
        if (_options.DefaultTimeout <= TimeSpan.Zero || _options.MaximumTimeout < _options.DefaultTimeout)
            throw new ArgumentOutOfRangeException(nameof(options), "Workflow run timeout bounds are invalid.");
        if (_options.HeartbeatInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Workflow run heartbeat interval must be positive.");
    }

    public WorkflowRunStartResult Start(
        WorkflowRunStartRequest request,
        WorkflowRunTarget target,
        Func<bool> isTargetCurrent,
        WorkflowRunExecutionOptions? executionOptions = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(isTargetCurrent);

        var prepared = Prepare(request, target);
        if (!prepared.Ok)
            return WorkflowRunStartResult.Rejected(
                prepared.StatusCode,
                prepared.Error!,
                prepared.ValidationErrors,
                prepared.ValidationWarnings,
                prepared.Admission);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_idempotency.TryGetValue(request.IdempotencyKey!, out var previous))
            {
                if (!CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(previous.RequestDigest),
                        Encoding.UTF8.GetBytes(prepared.RequestDigest!)))
                {
                    return WorkflowRunStartResult.Conflict(
                        "The idempotency key was already used with a different request digest.");
                }

                if (_runs.TryGetValue(previous.RunId, out var existing))
                    return WorkflowRunStartResult.FromExisting(CreateSnapshotLocked(existing), existing.CapabilityToken);

                _idempotency.Remove(request.IdempotencyKey!);
            }

            if (!isTargetCurrent())
            {
                return WorkflowRunStartResult.Conflict(
                    "The requested agent instance is no longer connected. Refresh agent discovery and retry.");
            }

            var targetKey = new WorkflowRunTargetKey(target.AgentId, target.AgentInstanceId);
            if (_activeTargets.TryGetValue(targetKey, out var activeRunId) &&
                _runs.TryGetValue(activeRunId, out var active) &&
                !WorkflowRunStates.IsTerminal(active.State))
            {
                return WorkflowRunStartResult.Conflict(
                    $"Agent instance '{target.AgentId}' already has mutating workflow run '{active.RunId}'.");
            }

            _activeTargets.Remove(targetKey);

            var now = _clock.GetUtcNow();
            var run = new RunRecord(
                CreateOpaqueId("run"),
                CreateCapabilityToken(),
                request.IdempotencyKey!,
                prepared.RequestDigest!,
                prepared.FlowDigest!,
                prepared.Flow!,
                prepared.SafetyRequest!,
                prepared.Admission!,
                target,
                prepared.Timeout,
                isTargetCurrent,
                executionOptions ?? new WorkflowRunExecutionOptions(),
                now);
            AddEventLocked(run, "queued", "Run accepted and queued.");
            AddEventLocked(
                run,
                "admission",
                $"Replay admission accepted with side-effect policy '{prepared.Admission!.SideEffectPolicy}'.");

            _runs.Add(run.RunId, run);
            _idempotency.Add(run.IdempotencyKey, new IdempotencyEntry(run.RequestDigest, run.RunId));
            _activeTargets[targetKey] = run.RunId;
            run.ExecutionTask = Task.Run(() => ExecuteAsync(run));

            return WorkflowRunStartResult.Started(CreateSnapshotLocked(run), run.CapabilityToken);
        }
    }

    public WorkflowRunCapabilitiesResponse GetCapabilities() => new()
    {
        MaxTimeoutMs = (long)_options.MaximumTimeout.TotalMilliseconds,
        MaxSteps = _options.MaximumSteps,
        WorkflowCommandLedger = _controlLedger is not null
    };

    public WorkflowRunAccessResult GetStatus(string runId, string? capabilityToken)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var run))
                return WorkflowRunAccessResult.NotFound();
            if (!HasCapability(run, capabilityToken))
                return WorkflowRunAccessResult.Unauthorized();
            return WorkflowRunAccessResult.Success(CreateSnapshotLocked(run));
        }
    }

    public WorkflowRunCancelResult Cancel(string runId, string? capabilityToken)
    {
        RunRecord? cancellationTarget = null;
        WorkflowRunSnapshot? snapshot;
        var alreadyTerminal = false;

        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var run))
                return WorkflowRunCancelResult.NotFound();
            if (!HasCapability(run, capabilityToken))
                return WorkflowRunCancelResult.Unauthorized();

            if (WorkflowRunStates.IsTerminal(run.State))
            {
                alreadyTerminal = true;
                snapshot = CreateSnapshotLocked(run);
            }
            else if (run.State == WorkflowRunState.Queued)
            {
                run.CancellationRequested = true;
                AddEventLocked(run, "cancellation-requested", "Cancellation requested before lease acquisition.");
                CompleteTerminalLocked(
                    run,
                    WorkflowRunState.Cancelled,
                    "Run cancelled before lease acquisition.",
                    compatibilityReport: null,
                    failureClass: Testing.MauiFlowFailureClasses.Cancelled);
                snapshot = CreateSnapshotLocked(run);
            }
            else
            {
                run.CancellationRequested = true;
                AddEventLocked(run, "cancellation-requested", "Cancellation requested; no future flow steps will be started.");
                cancellationTarget = run;
                snapshot = CreateSnapshotLocked(run);
            }
        }

        cancellationTarget?.Cancellation.Cancel();
        return WorkflowRunCancelResult.FromAccepted(snapshot!, alreadyTerminal);
    }

    public async Task<WorkflowRunSnapshot> WaitForTerminalAsync(
        string runId,
        string capabilityToken,
        CancellationToken cancellationToken = default)
    {
        RunRecord run;
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out run!))
                throw new WorkflowRunNotFoundException(runId);
            if (!HasCapability(run, capabilityToken))
                throw new WorkflowRunCapabilityException();
        }

        return await run.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Called by the broker when a WebSocket connection is superseded or disappears. A previous
    /// process instance must never continue as the target of a workflow run.
    /// </summary>
    public void MarkAgentInstanceUnavailable(string agentId, string agentInstanceId, string message)
    {
        RunRecord? affected = null;
        lock (_gate)
        {
            var key = new WorkflowRunTargetKey(agentId, agentInstanceId);
            if (!_activeTargets.TryGetValue(key, out var runId) ||
                !_runs.TryGetValue(runId, out var run) ||
                WorkflowRunStates.IsTerminal(run.State))
            {
                return;
            }

            run.LeaseLostRequested = true;
            run.AgentInstanceUnavailable = true;
            run.OverrideMessage = message;
            AddEventLocked(run, "lease-lost", message);
            affected = run;
        }

        affected.Cancellation.Cancel();
    }

    public void Dispose()
    {
        List<RunRecord> active;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            active = _runs.Values.Where(run => !WorkflowRunStates.IsTerminal(run.State)).ToList();
            foreach (var run in active)
            {
                run.LeaseLostRequested = true;
                run.OverrideMessage = "Broker is shutting down.";
                AddEventLocked(run, "lease-lost", run.OverrideMessage);
            }
        }

        foreach (var run in active)
            run.Cancellation.Cancel();
    }

    private async Task ExecuteAsync(RunRecord run)
    {
        Testing.FlowReplayReport? compatibilityReport = null;
        var terminalState = WorkflowRunState.InfrastructureError;
        var message = "Workflow runner stopped unexpectedly.";
        var failureClass = Testing.MauiFlowFailureClasses.Infrastructure;

        try
        {
            lock (_gate)
            {
                if (WorkflowRunStates.IsTerminal(run.State))
                    return;
                run.StartedAt = _clock.GetUtcNow();
                TransitionLocked(run, WorkflowRunState.AcquiringLease, "Acquiring the broker mutation lease.");
            }

            if (!run.IsTargetCurrent())
            {
                terminalState = WorkflowRunState.LeaseLost;
                message = "The requested agent instance is no longer connected.";
                failureClass = Testing.MauiFlowFailureClasses.LeaseLost;
                return;
            }

            if (run.Cancellation.IsCancellationRequested)
            {
                terminalState = WorkflowRunState.Cancelled;
                message = "Run cancelled before lease acquisition.";
                failureClass = Testing.MauiFlowFailureClasses.Cancelled;
                return;
            }

            var claim = _leases.Control(
                run.Target.AgentId,
                "claim",
                run.LeaseId,
                "workflow-run",
                run.RunId,
                force: false,
                transactionId: null);
            if (!claim.Allowed)
            {
                terminalState = WorkflowRunState.Failed;
                message = "The target agent is already held by another mutation lease.";
                failureClass = Testing.MauiFlowFailureClasses.LeaseConflict;
                return;
            }
            run.LeaseClaimed = true;

            if (run.Cancellation.IsCancellationRequested)
            {
                terminalState = WorkflowRunState.Cancelled;
                message = "Run cancelled while acquiring the mutation lease.";
                failureClass = Testing.MauiFlowFailureClasses.Cancelled;
                return;
            }

            var transaction = _leases.Control(
                run.Target.AgentId,
                "begin",
                run.LeaseId,
                "workflow-run",
                run.RunId,
                force: false,
                transactionId: run.TransactionId);
            if (!transaction.Allowed)
            {
                terminalState = WorkflowRunState.Failed;
                message = "The broker could not begin the mutation-lease transaction.";
                failureClass = Testing.MauiFlowFailureClasses.LeaseConflict;
                return;
            }
            run.TransactionBegun = true;
            run.AuthorityEpoch = transaction.AuthorityEpoch;

            if (_controlLedger is not null)
            {
                if (run.AuthorityEpoch <= 0)
                {
                    terminalState = WorkflowRunState.InfrastructureError;
                    message = "The broker did not provide a workflow authority epoch.";
                    failureClass = Testing.MauiFlowFailureClasses.Infrastructure;
                    return;
                }

                run.LedgerBeginAttempted = true;
                var began = await ControlLedgerAsync(run, "begin", reason: null).ConfigureAwait(false);
                if (!began.Ok)
                {
                    (terminalState, message, failureClass) = MapLedgerControlFailure(
                        began,
                        "The agent rejected the workflow ledger begin request.");
                    return;
                }

                run.LedgerBegun = true;
            }

            lock (_gate)
            {
                if (WorkflowRunStates.IsTerminal(run.State))
                    return;
                TransitionLocked(run, WorkflowRunState.Preparing, "Preparing canonical flow replay.");
            }

            run.HeartbeatTask = HeartbeatAsync(run);

            if (!run.IsTargetCurrent())
            {
                terminalState = WorkflowRunState.LeaseLost;
                message = "The requested agent instance reconnected before replay began.";
                failureClass = Testing.MauiFlowFailureClasses.LeaseLost;
                return;
            }

            if (run.Cancellation.IsCancellationRequested)
            {
                terminalState = WorkflowRunState.Cancelled;
                message = "Run cancelled before replay began.";
                failureClass = Testing.MauiFlowFailureClasses.Cancelled;
                return;
            }

            lock (_gate)
            {
                if (WorkflowRunStates.IsTerminal(run.State))
                    return;
                TransitionLocked(run, WorkflowRunState.Running, "Running canonical flow replay.");
            }

            run.TimeoutCancellation.CancelAfter(run.Timeout);
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                run.Cancellation.Token,
                run.TimeoutCancellation.Token);
            compatibilityReport = await _execute(
                new WorkflowRunExecution(
                    run.RunId,
                    run.Flow,
                    run.SafetyRequest,
                    run.Admission,
                    run.Target,
                    run.LeaseId,
                    run.TransactionId,
                    run.AuthorityEpoch,
                    run.ExecutionOptions),
                linkedCancellation.Token).ConfigureAwait(false);

            terminalState = compatibilityReport.Ok ? WorkflowRunState.Passed : WorkflowRunState.Failed;
            message = compatibilityReport.Ok ? "Flow replay passed." : "Flow replay reported a divergence.";
            failureClass = compatibilityReport.Ok
                ? string.Empty
                : ClassifyFailure(compatibilityReport);
            if (string.Equals(failureClass, Testing.MauiFlowFailureClasses.UnknownCompletion, StringComparison.Ordinal))
            {
                terminalState = WorkflowRunState.UnknownCompletion;
                message = "Flow replay has a command with unknown completion.";
            }
        }
        catch (OperationCanceledException)
        {
            terminalState = WorkflowRunState.Cancelled;
            message = "Workflow run cancelled.";
            failureClass = Testing.MauiFlowFailureClasses.Cancelled;
        }
        catch (Exception ex)
        {
            terminalState = WorkflowRunState.InfrastructureError;
            message = $"Workflow runner infrastructure error: {ex.Message}";
            failureClass = Testing.MauiFlowFailureClasses.Infrastructure;
        }
        finally
        {
            if (run.HeartbeatTask is not null)
            {
                run.HeartbeatCancellation.Cancel();
                try
                {
                    await run.HeartbeatTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            var shouldComplete = false;
            lock (_gate)
            {
                if (WorkflowRunStates.IsTerminal(run.State))
                {
                    // A queued cancellation completed before this task reached lease acquisition.
                }
                else
                {
                    if (run.AgentInstanceUnavailable)
                    {
                        terminalState = WorkflowRunState.Orphaned;
                        message = run.OverrideMessage ?? "The target agent instance is no longer available.";
                        failureClass = Testing.MauiFlowFailureClasses.AgentDisconnected;
                    }
                    else if (run.LeaseLostRequested)
                    {
                        terminalState = WorkflowRunState.LeaseLost;
                        message = run.OverrideMessage ?? "The workflow mutation lease was lost.";
                        failureClass = Testing.MauiFlowFailureClasses.LeaseLost;
                    }
                    else if (run.TimeoutCancellation.IsCancellationRequested)
                    {
                        terminalState = WorkflowRunState.TimedOut;
                        message = "Workflow run timed out.";
                        failureClass = Testing.MauiFlowFailureClasses.Timeout;
                    }
                    else if (run.CancellationRequested || run.Cancellation.IsCancellationRequested)
                    {
                        terminalState = WorkflowRunState.Cancelled;
                        message = "Workflow run cancelled.";
                        failureClass = Testing.MauiFlowFailureClasses.Cancelled;
                    }
                    shouldComplete = true;
                }
            }

            if (shouldComplete && run.LedgerBeginAttempted)
            {
                var action = run.LedgerBegun &&
                    (terminalState is WorkflowRunState.Passed or WorkflowRunState.Failed)
                    ? "end"
                    : "abandon";
                var ledgerResult = await ControlLedgerAsync(
                    run,
                    action,
                    action == "abandon" ? WorkflowRunStates.ToWireValue(terminalState) : null)
                    .ConfigureAwait(false);
                if (!ledgerResult.Ok)
                {
                    (terminalState, message, failureClass) = MapLedgerControlFailure(
                        ledgerResult,
                        "The agent did not confirm workflow ledger cleanup.");
                }
            }

            EndLease(run);

            if (shouldComplete)
            {
                lock (_gate)
                {
                    if (!WorkflowRunStates.IsTerminal(run.State))
                        CompleteTerminalLocked(run, terminalState, message, compatibilityReport, failureClass);
                }
            }
        }
    }

    private async Task HeartbeatAsync(RunRecord run)
    {
        try
        {
            using var timer = new PeriodicTimer(_options.HeartbeatInterval);
            while (await timer.WaitForNextTickAsync(run.HeartbeatCancellation.Token).ConfigureAwait(false))
            {
                var heartbeat = _leases.Control(
                    run.Target.AgentId,
                    "heartbeat",
                    run.LeaseId,
                    "workflow-run",
                    run.RunId,
                    force: false,
                    transactionId: run.TransactionId);
                if (heartbeat.Allowed)
                    continue;

                lock (_gate)
                {
                    if (!WorkflowRunStates.IsTerminal(run.State))
                    {
                        run.LeaseLostRequested = true;
                        run.OverrideMessage = "The broker mutation lease could not be heartbeated.";
                        AddEventLocked(run, "lease-lost", run.OverrideMessage);
                    }
                }
                run.Cancellation.Cancel();
                return;
            }
        }
        catch (OperationCanceledException) when (run.HeartbeatCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (!WorkflowRunStates.IsTerminal(run.State))
                {
                    run.LeaseLostRequested = true;
                    run.OverrideMessage = $"The broker mutation-lease heartbeat failed: {ex.Message}";
                    AddEventLocked(run, "lease-lost", run.OverrideMessage);
                }
            }
            run.Cancellation.Cancel();
        }
    }

    private async Task<WorkflowRunLedgerControlResult> ControlLedgerAsync(
        RunRecord run,
        string action,
        string? reason)
    {
        if (_controlLedger is null)
            return WorkflowRunLedgerControlResult.Success();

        try
        {
            return await _controlLedger(
                new WorkflowRunLedgerControl(
                    action,
                    run.RunId,
                    run.Target,
                    run.LeaseId,
                    run.AuthorityEpoch,
                    ApprovalDigest: null,
                    Reason: reason),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return WorkflowRunLedgerControlResult.Failure(
                "workflow-transport",
                $"The agent workflow ledger control failed: {ex.Message}");
        }
    }

    private static (WorkflowRunState State, string Message, string FailureClass) MapLedgerControlFailure(
        WorkflowRunLedgerControlResult result,
        string fallbackMessage)
    {
        var message = string.IsNullOrWhiteSpace(result.Error)
            ? fallbackMessage
            : $"{fallbackMessage} {result.Error}";
        return result.Reason switch
        {
            "workflow-unknown-completion" => (
                WorkflowRunState.UnknownCompletion,
                message,
                Testing.MauiFlowFailureClasses.UnknownCompletion),
            "workflow-agent-instance" or "workflow-broker-unavailable" or "workflow-transport" => (
                WorkflowRunState.Orphaned,
                message,
                Testing.MauiFlowFailureClasses.AgentDisconnected),
            _ => (
                WorkflowRunState.Failed,
                message,
                Testing.MauiFlowFailureClasses.WorkflowCommandConflict)
        };
    }

    private void EndLease(RunRecord run)
    {
        if (Interlocked.Exchange(ref run.LeaseEnded, 1) != 0)
            return;

        if (run.TransactionBegun)
        {
            _leases.Control(
                run.Target.AgentId,
                "end",
                run.LeaseId,
                "workflow-run",
                run.RunId,
                force: false,
                transactionId: run.TransactionId);
        }

        if (run.LeaseClaimed)
        {
            _leases.Control(
                run.Target.AgentId,
                "release",
                run.LeaseId,
                "workflow-run",
                run.RunId,
                force: false,
                transactionId: null);
        }
    }

    private void CompleteTerminalLocked(
        RunRecord run,
        WorkflowRunState terminalState,
        string message,
        Testing.FlowReplayReport? compatibilityReport,
        string failureClass)
    {
        if (!WorkflowRunStates.IsTerminal(terminalState))
            throw new ArgumentOutOfRangeException(nameof(terminalState), "A terminal state is required.");
        if (WorkflowRunStates.IsTerminal(run.State))
            return;

        run.CompatibilityReport = compatibilityReport;
        run.FirstDivergence = compatibilityReport?.DivergencePoint;
        run.Message = message;
        run.EndedAt = _clock.GetUtcNow();
        TransitionLocked(run, terminalState, message);
        run.StructuredReport = BuildStructuredReport(run, terminalState, message, compatibilityReport, failureClass);
        PersistStructuredReportLocked(run);
        if (run.CompatibilityReport is not null)
        {
            run.CompatibilityReport.StructuredReport = run.StructuredReport;
            run.CompatibilityReport.ReportPath = run.StructuredReport.ReportPath;
            run.CompatibilityReport.ReportDigest = run.StructuredReport.ReportDigest;
        }

        _activeTargets.Remove(new WorkflowRunTargetKey(run.Target.AgentId, run.Target.AgentInstanceId));
        run.TerminalOrder = ++_terminalOrder;
        run.Completion.TrySetResult(CreateSnapshotLocked(run));
        EvictTerminalRunsLocked();
    }

    private Testing.MauiFlowRunReport BuildStructuredReport(
        RunRecord run,
        WorkflowRunState state,
        string message,
        Testing.FlowReplayReport? replay,
        string failureClass)
    {
        if (replay?.StructuredReport is { } canonical)
        {
            canonical.RunId = run.RunId;
            canonical.FlowId ??= $"sha256:{run.FlowDigest}";
            canonical.FlowDigest = run.FlowDigest;
            canonical.LegacyFlowIdentity ??= run.Flow.Name;
            canonical.Target = MergeTarget(canonical.Target, run.Target);
            canonical.StartedAt ??= run.StartedAt ?? run.CreatedAt;
            canonical.EndedAt = run.EndedAt;
            canonical.Outcome = new Testing.MauiFlowRunOutcome
            {
                Status = WorkflowRunStates.ToWireValue(state),
                Summary = message,
                Terminal = true,
            };
            canonical.DivergenceStepId ??= run.FirstDivergence?.ToString(System.Globalization.CultureInfo.InvariantCulture);
            canonical.Events = MergeEvents(canonical.Events, run.Events);
            if (run.LifecycleEventsTruncated)
            {
                canonical.Truncated = true;
                canonical.TruncationReason ??= "The broker lifecycle-event limit was reached.";
                canonical.Omissions.Add(new Testing.MauiFlowReportOmission
                {
                    Kind = "broker-events",
                    Reason = "The broker lifecycle-event limit was reached.",
                });
            }
            if (state == WorkflowRunState.Passed)
            {
                canonical.Failure = null;
            }
            else if (state == WorkflowRunState.Failed && canonical.Failure is not null)
            {
                // The canonical runner already recorded the precise step failure. Keep its
                // redacted message and detailed classifier instead of replacing it with a
                // broker-level "divergence" summary.
            }
            else
            {
                var classification = Testing.MauiFlowFailureClassifier.Classify(new Testing.MauiFlowFailureFacts
                {
                    TerminalOutcome = WorkflowRunStates.ToWireValue(state),
                    FailureClass = failureClass,
                    LegacyFailureKind = replay.Results.FirstOrDefault(result => !result.Ok)?.FailureKind,
                });
                canonical.Failure = Testing.MauiFlowFailureClassifier.ToFailure(
                    classification,
                    canonical.Failure?.FailureId ?? $"failure-{run.RunId}",
                    canonical.Failure?.LegacyKind ?? replay.Results.FirstOrDefault(result => !result.Ok)?.FailureKind,
                    canonical.DivergenceStepId,
                    run.EndedAt ?? _clock.GetUtcNow(),
                    message);
            }
            ApplyAdmissionFacts(run, canonical, state);
            Testing.MauiFlowRunReportSerializer.ApplyLimits(canonical, _options.ReportLimits);
            return canonical;
        }

        var steps = replay?.Results.Select(result => new Testing.MauiFlowStepAttempt
        {
            StepId = result.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Sequence = result.Seq,
            Action = result.Action,
            Intent = result.Label,
            FailureClass = result.FailureKind is null ? null : MapFailureKind(result.FailureKind),
            CommandId = result.CommandId,
            CommandSequence = null,
            ActionDigest = result.ActionDigest,
            AuthorityEpoch = result.AuthorityEpoch ?? (run.AuthorityEpoch > 0 ? run.AuthorityEpoch : null),
            AcknowledgementState = result.AcknowledgementState,
            Dispatch = result.CommandId is null ? null : new Testing.MauiFlowDispatchReceipt
            {
                CommandId = result.CommandId,
                ActionDigest = result.ActionDigest,
                AuthorityEpoch = result.AuthorityEpoch,
                AcknowledgementState = result.AcknowledgementState,
                CompletionCertainty = result.AcknowledgementState == "unknown-completion"
                    ? "unknown"
                    : "completed"
            },
            TargetResolution = result.MatchCount is null ? null : new Testing.MauiFlowTargetResolution
            {
                MatchCount = result.MatchCount,
                Status = result.Ok ? "resolved" : "failed",
                Message = result.Error
            },
            Assertions = result.Asserts.Select(assertion => new Testing.MauiFlowAssertionResult
            {
                Kind = assertion.Kind,
                Passed = assertion.Ok,
                Skipped = assertion.Skipped,
                Expected = Testing.MauiFlowReportRedactor.DescribeValue(assertion.Expected).Value,
                Actual = Testing.MauiFlowReportRedactor.DescribeValue(assertion.Actual).Value,
                ExpectedDisclosure = Testing.MauiFlowReportRedactor.DescribeValue(assertion.Expected),
                ActualDisclosure = Testing.MauiFlowReportRedactor.DescribeValue(assertion.Actual)
            }).ToList()
        }).ToList() ?? [];

        var events = run.Events.Select((item, index) => new Testing.MauiFlowRunEvent
        {
            Sequence = index + 1,
            At = item.At,
            Kind = item.Kind,
            Message = item.Message
        }).ToList();

        var succeeded = state == WorkflowRunState.Passed;
        var fallback = new Testing.MauiFlowRunReport
        {
            RunId = run.RunId,
            FlowId = $"sha256:{run.FlowDigest}",
            FlowDigest = run.FlowDigest,
            LegacyFlowIdentity = run.Flow.Name,
            Target = new Testing.MauiFlowRunTarget
            {
                TargetId = run.Target.AgentId,
                AgentId = run.Target.AgentId,
                AgentInstanceId = run.Target.AgentInstanceId,
                Platform = run.Target.Platform,
                AppId = run.Target.AppName
            },
            StartedAt = run.StartedAt ?? run.CreatedAt,
            EndedAt = run.EndedAt,
            Outcome = new Testing.MauiFlowRunOutcome
            {
                Status = WorkflowRunStates.ToWireValue(state),
                Summary = message,
                Terminal = true
            },
            DivergenceStepId = run.FirstDivergence?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Events = events,
            Steps = steps,
            Failure = succeeded ? null : new Testing.MauiFlowFailure
            {
                FailureId = $"failure-{run.RunId}",
                Class = failureClass,
                Code = failureClass,
                Category = Testing.MauiFlowFailureClassifier.Classify(new Testing.MauiFlowFailureFacts
                {
                    FailureClass = failureClass
                }).Category,
                Phase = Testing.MauiFlowFailureClassifier.Classify(new Testing.MauiFlowFailureFacts
                {
                    FailureClass = failureClass
                }).Phase,
                Retryable = Testing.MauiFlowFailureClassifier.Classify(new Testing.MauiFlowFailureFacts
                {
                    FailureClass = failureClass
                }).Retryable,
                RepairEligible = false,
                LegacyKind = replay?.Results.FirstOrDefault(result => !result.Ok)?.FailureKind,
                Message = message,
                StepId = run.FirstDivergence?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                At = run.EndedAt
            }
        };
        if (run.LifecycleEventsTruncated)
        {
            fallback.Truncated = true;
            fallback.TruncationReason = "The broker lifecycle-event limit was reached.";
            fallback.Omissions.Add(new Testing.MauiFlowReportOmission
            {
                Kind = "broker-events",
                Reason = "The broker lifecycle-event limit was reached.",
            });
        }
        ApplyAdmissionFacts(run, fallback, state);
        Testing.MauiFlowRunReportSerializer.ApplyLimits(fallback, _options.ReportLimits);
        return fallback;
    }

    private static void ApplyAdmissionFacts(
        RunRecord run,
        Testing.MauiFlowRunReport report,
        WorkflowRunState state)
    {
        var context = run.SafetyRequest.Context;
        report.SideEffectPolicy = run.Admission.SideEffectPolicy;
        report.ReplayEligibility = run.Admission;
        report.Preconditions ??= context?.Preconditions;
        report.Reset ??= context?.Reset;
        report.Compensator ??= context?.Compensator;
        if (report.BusinessOracles.Count == 0 && context is not null)
            report.BusinessOracles = context.BusinessOracles.ToList();

        report.Outcome ??= new Testing.MauiFlowRunOutcome();
        var verified = state == WorkflowRunState.Passed && run.Admission.RunVerificationAllowed;
        report.Outcome.Verified = verified;
        report.Outcome.VerificationReason = verified
            ? "Required independent business oracles verified the run."
            : "The run is not verified because required independent business-oracle evidence is absent, failed, or the run did not pass.";
        report.Verification = new Testing.MauiFlowRunVerification
        {
            Verified = verified,
            Reason = report.Outcome.VerificationReason,
            CheckedAt = report.EndedAt,
        };
        if (report.Failure is not null && !run.Admission.RepairEligibility)
            report.Failure.RepairEligible = false;
    }

    private void PersistStructuredReportLocked(RunRecord run)
    {
        if (run.StructuredReport is null || string.IsNullOrWhiteSpace(_options.ArtifactRoot))
            return;

        var write = Testing.MauiFlowRunReportSerializer.WriteAtomic(
            run.StructuredReport,
            _options.ArtifactRoot!,
            _options.ReportLimits);
        if (write.Ok)
        {
            run.ReportPath = write.Path;
            run.ReportDigest = write.Digest;
            run.StructuredReport.ReportPath = write.Path;
            run.StructuredReport.ReportDigest = write.Digest;
            return;
        }

        run.StructuredReport.Truncated = true;
        run.StructuredReport.Omissions.Add(new Testing.MauiFlowReportOmission
        {
            Kind = "report-artifact",
            Reason = write.Error ?? "The report artifact could not be written.",
        });
    }

    private static Testing.MauiFlowRunTarget MergeTarget(
        Testing.MauiFlowRunTarget? existing,
        WorkflowRunTarget target)
    {
        existing ??= new Testing.MauiFlowRunTarget();
        existing.TargetId ??= target.AgentId;
        existing.AgentId ??= target.AgentId;
        existing.AgentInstanceId ??= target.AgentInstanceId;
        existing.Platform ??= target.Platform;
        existing.AppId ??= target.AppName;
        return existing;
    }

    private static List<Testing.MauiFlowRunEvent> MergeEvents(
        IReadOnlyList<Testing.MauiFlowRunEvent> runnerEvents,
        IReadOnlyList<WorkflowRunLifecycleEvent> brokerEvents)
    {
        var merged = runnerEvents
            .Concat(brokerEvents.Select(item => new Testing.MauiFlowRunEvent
            {
                At = item.At,
                Kind = item.Kind,
                Message = item.Message,
            }))
            .OrderBy(item => item.At ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.Sequence ?? int.MaxValue)
            .Take(128)
            .Select((item, index) => new Testing.MauiFlowRunEvent
            {
                Sequence = index + 1,
                At = item.At,
                Kind = item.Kind,
                Message = item.Message,
                StepId = item.StepId,
                Data = item.Data,
            })
            .ToList();
        return merged;
    }

    private void EvictTerminalRunsLocked()
    {
        while (_runs.Values.Count(run => WorkflowRunStates.IsTerminal(run.State)) >
            _options.MaxRetainedTerminalRuns)
        {
            var candidate = _runs.Values
                .Where(run => WorkflowRunStates.IsTerminal(run.State))
                .OrderBy(run => run.TerminalOrder)
                .ThenBy(run => run.RunId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (candidate is null)
                return;

            _runs.Remove(candidate.RunId);
            if (_idempotency.TryGetValue(candidate.IdempotencyKey, out var entry) &&
                string.Equals(entry.RunId, candidate.RunId, StringComparison.Ordinal))
            {
                _idempotency.Remove(candidate.IdempotencyKey);
            }

            // A queued cancellation can complete before its scheduled execution task observes the
            // terminal state. Let its cancellation sources be collected with the record instead of
            // disposing a token that that task may still read.
        }
    }

    private WorkflowRunSnapshot CreateSnapshotLocked(RunRecord run) => new()
    {
        Schema = 1,
        RunId = run.RunId,
        State = WorkflowRunStates.ToWireValue(run.State),
        Terminal = WorkflowRunStates.IsTerminal(run.State),
        FlowDigest = run.FlowDigest,
        AuthorityEpoch = run.AuthorityEpoch > 0 ? run.AuthorityEpoch : null,
        Target = run.Target.ToSnapshot(),
        CreatedAt = run.CreatedAt,
        StartedAt = run.StartedAt,
        EndedAt = run.EndedAt,
        FirstDivergence = run.FirstDivergence,
        CancellationRequested = run.CancellationRequested,
        Message = run.Message,
        Events = run.Events.ToList(),
        Report = run.StructuredReport,
        ReportPath = run.ReportPath ?? run.StructuredReport?.ReportPath,
        ReportDigest = run.ReportDigest ?? run.StructuredReport?.ReportDigest,
        Admission = run.Admission,
        CompatibilityReport = run.CompatibilityReport
    };

    private static bool HasCapability(RunRecord run, string? suppliedToken)
    {
        if (string.IsNullOrWhiteSpace(suppliedToken) || suppliedToken.Length > 128)
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(run.CapabilityToken),
            Encoding.UTF8.GetBytes(suppliedToken));
    }

    private void TransitionLocked(RunRecord run, WorkflowRunState next, string message)
    {
        if (run.State == next)
            return;
        if (!WorkflowRunStates.CanTransition(run.State, next))
            throw new InvalidOperationException(
                $"Invalid workflow run transition from '{WorkflowRunStates.ToWireValue(run.State)}' to '{WorkflowRunStates.ToWireValue(next)}'.");

        run.State = next;
        AddEventLocked(run, WorkflowRunStates.ToWireValue(next), message);
    }

    private void AddEventLocked(RunRecord run, string kind, string message)
    {
        if (run.Events.Count == MaxLifecycleEvents)
        {
            run.Events.RemoveAt(0);
            run.LifecycleEventsTruncated = true;
        }
        run.Events.Add(new WorkflowRunLifecycleEvent
        {
            At = _clock.GetUtcNow(),
            Kind = kind,
            Message = message
        });
    }

    private PreparedStart Prepare(WorkflowRunStartRequest request, WorkflowRunTarget target)
    {
        if (string.IsNullOrWhiteSpace(request.AgentId) ||
            string.IsNullOrWhiteSpace(request.AgentInstanceId) ||
            !string.Equals(request.AgentId, target.AgentId, StringComparison.Ordinal) ||
            !string.Equals(request.AgentInstanceId, target.AgentInstanceId, StringComparison.Ordinal))
        {
            return PreparedStart.Invalid(409, "The request must target the exact connected agent ID and instance ID.");
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 256)
            return PreparedStart.Invalid(400, "idempotencyKey is required and must be 256 characters or fewer.");

        if (request.Markdown is not null && request.Flow is not null)
            return PreparedStart.Invalid(400, "Specify exactly one of markdown or flow.");

        Testing.MauiFlow? flow = request.Flow;
        if (request.Markdown is not null)
        {
            var parsed = Testing.FlowMarkdown.Parse(request.Markdown);
            if (!parsed.Ok || parsed.Flow is null)
                return PreparedStart.Invalid(400, parsed.Error ?? "Could not parse flow Markdown.");
            flow = parsed.Flow;
        }
        if (flow is null)
            return PreparedStart.Invalid(400, "Either markdown or flow is required.");
        if (flow.Steps is null)
            return PreparedStart.Invalid(400, "The flow must contain a steps array.");
        if (HasNullFlowMembers(flow))
            return PreparedStart.Invalid(400, "The flow contains an invalid null step or assertion.");

        var validation = Testing.FlowValidator.Validate(flow);
        if (!validation.Ok)
        {
            return PreparedStart.Invalid(
                400,
                "Flow failed validation.",
                validation.Errors,
                validation.Warnings);
        }

        if (flow.Steps.Count > _options.MaximumSteps)
            return PreparedStart.Invalid(400, $"Flow has too many steps (max {_options.MaximumSteps}).");

        TimeSpan timeout;
        if (request.TimeoutMs is null)
        {
            timeout = _options.DefaultTimeout;
        }
        else if (request.TimeoutMs <= 0 || request.TimeoutMs > _options.MaximumTimeout.TotalMilliseconds)
        {
            return PreparedStart.Invalid(
                400,
                $"timeoutMs must be greater than zero and no more than {(long)_options.MaximumTimeout.TotalMilliseconds}.");
        }
        else
        {
            timeout = TimeSpan.FromMilliseconds(request.TimeoutMs.Value);
        }

        var canonicalFlow = CanonicalizeFlow(flow);
        var clonedFlow = JsonSerializer.Deserialize(canonicalFlow, Testing.MauiFlowJsonContext.Default.MauiFlow);
        if (clonedFlow is null)
            return PreparedStart.Invalid(400, "The flow could not be normalized.");

        var safetyRequest = CloneSafetyRequest(request.Plan, request.Context);
        var admission = Testing.MauiFlowReplaySafetyEvaluator.Evaluate(safetyRequest);
        if (!admission.IsAllowedForIntent(safetyRequest.Context?.Intent))
        {
            return PreparedStart.Invalid(
                409,
                "Flow replay admission was denied before mutation.",
                admission.Reasons
                    .Where(static reason => reason.Blocking == true)
                    .Select(static reason => reason.Message ?? reason.Code ?? "Replay admission was denied.")
                    .ToArray(),
                admission.Reasons
                    .Where(static reason => reason.Blocking != true)
                    .Select(static reason => reason.Message ?? reason.Code ?? "Replay admission warning.")
                    .ToArray(),
                admission);
        }

        var flowDigest = Convert.ToHexString(SHA256.HashData(canonicalFlow)).ToLowerInvariant();
        var requestDigest = ComputeRequestDigest(target, flowDigest, timeout, ComputeSafetyDigest(safetyRequest));
        var warnings = validation.Warnings
            .Concat(admission.Reasons
                .Where(static reason => reason.Blocking != true)
                .Select(static reason => reason.Message ?? reason.Code ?? "Replay admission warning."))
            .ToArray();
        return PreparedStart.Success(clonedFlow, flowDigest, requestDigest, timeout, warnings, safetyRequest, admission);
    }

    private static Testing.MauiFlowRunRequest CloneSafetyRequest(
        Testing.MauiTestPlan? plan,
        Testing.MauiFlowRunContext? context)
    {
        var source = new Testing.MauiFlowRunRequest
        {
            Plan = plan,
            Context = context,
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(source, Testing.MauiTestingJsonContext.Default.MauiFlowRunRequest);
        return JsonSerializer.Deserialize(bytes, Testing.MauiTestingJsonContext.Default.MauiFlowRunRequest) ??
            new Testing.MauiFlowRunRequest();
    }

    private static string ComputeSafetyDigest(Testing.MauiFlowRunRequest request)
    {
        var element = JsonSerializer.SerializeToElement(request, Testing.MauiTestingJsonContext.Default.MauiFlowRunRequest);
        using var output = new MemoryStream();
        using var writer = new Utf8JsonWriter(output);
        WriteCanonicalJson(writer, element);
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(output.ToArray())).ToLowerInvariant();
    }

    private static bool HasNullFlowMembers(Testing.MauiFlow flow)
    {
        foreach (var step in flow.Steps)
        {
            if (step is null || step.Asserts?.Any(assertion => assertion is null) == true)
                return true;
        }
        return false;
    }

    private static byte[] CanonicalizeFlow(Testing.MauiFlow flow)
    {
        var element = JsonSerializer.SerializeToElement(flow, Testing.MauiFlowJsonContext.Default.MauiFlow);
        using var output = new MemoryStream();
        using var writer = new Utf8JsonWriter(output);
        WriteCanonicalJson(writer, element);
        writer.Flush();
        return output.ToArray();
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteCanonicalJson(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static string ComputeRequestDigest(
        WorkflowRunTarget target,
        string flowDigest,
        TimeSpan timeout,
        string safetyDigest)
    {
        var material = string.Join(
            "\n",
            target.AgentId,
            target.AgentInstanceId,
            target.AgentPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            flowDigest,
            ((long)timeout.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
            safetyDigest);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private static string ClassifyFailure(Testing.FlowReplayReport report)
        => Testing.MauiFlowFailureClassifier.Classify(new Testing.MauiFlowFailureFacts
        {
            LegacyFailureKind = report.Results.FirstOrDefault(result => !result.Ok)?.FailureKind,
        }).FailureClass;

    private static string MapFailureKind(string kind)
        => Testing.MauiFlowFailureClassifier.FromLegacyFailureKind(kind)
            ?? Testing.MauiFlowFailureClasses.Infrastructure;

    private static string CreateOpaqueId(string prefix)
        => $"{prefix}_{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}";

    private static string CreateCapabilityToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed class RunRecord
    {
        public RunRecord(
            string runId,
            string capabilityToken,
            string idempotencyKey,
            string requestDigest,
            string flowDigest,
            Testing.MauiFlow flow,
            Testing.MauiFlowRunRequest safetyRequest,
            Testing.MauiFlowReplayEligibilityDecision admission,
            WorkflowRunTarget target,
            TimeSpan timeout,
            Func<bool> isTargetCurrent,
            WorkflowRunExecutionOptions executionOptions,
            DateTimeOffset createdAt)
        {
            RunId = runId;
            CapabilityToken = capabilityToken;
            IdempotencyKey = idempotencyKey;
            RequestDigest = requestDigest;
            FlowDigest = flowDigest;
            Flow = flow;
            SafetyRequest = safetyRequest;
            Admission = admission;
            Target = target;
            Timeout = timeout;
            IsTargetCurrent = isTargetCurrent;
            ExecutionOptions = executionOptions;
            CreatedAt = createdAt;
            LeaseId = CreateOpaqueId("lease");
            TransactionId = CreateOpaqueId("transaction");
        }

        public string RunId { get; }
        public string CapabilityToken { get; }
        public string IdempotencyKey { get; }
        public string RequestDigest { get; }
        public string FlowDigest { get; }
        public Testing.MauiFlow Flow { get; }
        public Testing.MauiFlowRunRequest SafetyRequest { get; }
        public Testing.MauiFlowReplayEligibilityDecision Admission { get; }
        public WorkflowRunTarget Target { get; }
        public TimeSpan Timeout { get; }
        public Func<bool> IsTargetCurrent { get; }
        public WorkflowRunExecutionOptions ExecutionOptions { get; }
        public string LeaseId { get; }
        public string TransactionId { get; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? EndedAt { get; set; }
        public WorkflowRunState State { get; set; } = WorkflowRunState.Queued;
        public List<WorkflowRunLifecycleEvent> Events { get; } = [];
        public CancellationTokenSource Cancellation { get; } = new();
        public CancellationTokenSource TimeoutCancellation { get; } = new();
        public CancellationTokenSource HeartbeatCancellation { get; } = new();
        public TaskCompletionSource<WorkflowRunSnapshot> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? ExecutionTask { get; set; }
        public Task? HeartbeatTask { get; set; }
        public bool CancellationRequested { get; set; }
        public bool LeaseLostRequested { get; set; }
        public bool AgentInstanceUnavailable { get; set; }
        public string? OverrideMessage { get; set; }
        public bool LeaseClaimed { get; set; }
        public bool TransactionBegun { get; set; }
        public bool LedgerBeginAttempted { get; set; }
        public bool LedgerBegun { get; set; }
        public long AuthorityEpoch { get; set; }
        public int LeaseEnded;
        public int? FirstDivergence { get; set; }
        public string? Message { get; set; }
        public Testing.FlowReplayReport? CompatibilityReport { get; set; }
        public Testing.MauiFlowRunReport? StructuredReport { get; set; }
        public string? ReportPath { get; set; }
        public string? ReportDigest { get; set; }
        public bool LifecycleEventsTruncated { get; set; }
        public long TerminalOrder { get; set; }
    }

    private sealed record IdempotencyEntry(string RequestDigest, string RunId);

    private sealed class PreparedStart
    {
        public bool Ok { get; private init; }
        public int StatusCode { get; private init; }
        public string? Error { get; private init; }
        public Testing.MauiFlow? Flow { get; private init; }
        public Testing.MauiFlowRunRequest? SafetyRequest { get; private init; }
        public Testing.MauiFlowReplayEligibilityDecision? Admission { get; private init; }
        public string? FlowDigest { get; private init; }
        public string? RequestDigest { get; private init; }
        public TimeSpan Timeout { get; private init; }
        public IReadOnlyList<string> ValidationErrors { get; private init; } = [];
        public IReadOnlyList<string> ValidationWarnings { get; private init; } = [];

        public static PreparedStart Success(
            Testing.MauiFlow flow,
            string flowDigest,
            string requestDigest,
            TimeSpan timeout,
            IReadOnlyList<string> warnings,
            Testing.MauiFlowRunRequest safetyRequest,
            Testing.MauiFlowReplayEligibilityDecision admission)
            => new()
            {
                Ok = true,
                Flow = flow,
                FlowDigest = flowDigest,
                RequestDigest = requestDigest,
                Timeout = timeout,
                ValidationWarnings = warnings.ToArray(),
                SafetyRequest = safetyRequest,
                Admission = admission,
            };

        public static PreparedStart Invalid(
            int statusCode,
            string error,
            IReadOnlyList<string>? errors = null,
            IReadOnlyList<string>? warnings = null,
            Testing.MauiFlowReplayEligibilityDecision? admission = null)
            => new()
            {
                StatusCode = statusCode,
                Error = error,
                ValidationErrors = errors?.ToArray() ?? [],
                ValidationWarnings = warnings?.ToArray() ?? [],
                Admission = admission,
            };
    }
}

internal interface IWorkflowMutationLeaseRegistry
{
    MutationLeaseSnapshot Control(
        string agentId,
        string action,
        string? leaseId,
        string? holderKind,
        string? label,
        bool force,
        string? transactionId);
}

internal sealed record WorkflowRunLedgerControl(
    string Action,
    string RunId,
    WorkflowRunTarget Target,
    string LeaseId,
    long AuthorityEpoch,
    string? ApprovalDigest,
    string? Reason);

internal sealed class WorkflowRunLedgerControlResult
{
    public bool Ok { get; private init; }
    public string? Reason { get; private init; }
    public string? Error { get; private init; }

    public static WorkflowRunLedgerControlResult Success() => new() { Ok = true };

    public static WorkflowRunLedgerControlResult Failure(string? reason, string? error)
        => new()
        {
            Reason = reason,
            Error = error
        };
}

internal sealed class WorkflowRunCoordinatorOptions
{
    public int MaxRetainedTerminalRuns { get; init; } = 128;
    public int MaximumSteps { get; init; } = 2_000;
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan MaximumTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>Optional trusted root for atomic &lt;runId&gt;/flow-run.json artifacts.</summary>
    public string? ArtifactRoot { get; init; }
    public Testing.MauiFlowRunReportLimits ReportLimits { get; init; } = new();
}

internal enum WorkflowRunState
{
    Queued,
    AcquiringLease,
    Preparing,
    Running,
    Passed,
    Failed,
    Cancelled,
    TimedOut,
    LeaseLost,
    InfrastructureError,
    UnknownCompletion,
    Orphaned
}

internal static class WorkflowRunStates
{
    public static bool IsTerminal(WorkflowRunState state) => state is
        WorkflowRunState.Passed or
        WorkflowRunState.Failed or
        WorkflowRunState.Cancelled or
        WorkflowRunState.TimedOut or
        WorkflowRunState.LeaseLost or
        WorkflowRunState.InfrastructureError or
        WorkflowRunState.UnknownCompletion or
        WorkflowRunState.Orphaned;

    public static bool CanTransition(WorkflowRunState current, WorkflowRunState next) => current switch
    {
        WorkflowRunState.Queued => next is WorkflowRunState.AcquiringLease or WorkflowRunState.Cancelled,
        WorkflowRunState.AcquiringLease => next is WorkflowRunState.Preparing or
            WorkflowRunState.Failed or WorkflowRunState.Cancelled or WorkflowRunState.LeaseLost or
            WorkflowRunState.TimedOut or WorkflowRunState.InfrastructureError or
            WorkflowRunState.UnknownCompletion or WorkflowRunState.Orphaned,
        WorkflowRunState.Preparing => next is WorkflowRunState.Running or WorkflowRunState.Failed or
            WorkflowRunState.Cancelled or WorkflowRunState.LeaseLost or WorkflowRunState.TimedOut or
            WorkflowRunState.InfrastructureError or WorkflowRunState.UnknownCompletion or
            WorkflowRunState.Orphaned,
        WorkflowRunState.Running => IsTerminal(next),
        _ => false
    };

    public static string ToWireValue(WorkflowRunState state) => state switch
    {
        WorkflowRunState.Queued => "queued",
        WorkflowRunState.AcquiringLease => "acquiring-lease",
        WorkflowRunState.Preparing => "preparing",
        WorkflowRunState.Running => "running",
        WorkflowRunState.Passed => "passed",
        WorkflowRunState.Failed => "failed",
        WorkflowRunState.Cancelled => "cancelled",
        WorkflowRunState.TimedOut => "timed-out",
        WorkflowRunState.LeaseLost => "lease-lost",
        WorkflowRunState.InfrastructureError => "infrastructure-error",
        WorkflowRunState.UnknownCompletion => "unknown-completion",
        WorkflowRunState.Orphaned => "orphaned",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };
}

internal sealed class WorkflowRunStartRequest
{
    [JsonPropertyName("agentId")] public string? AgentId { get; set; }
    [JsonPropertyName("agentInstanceId")] public string? AgentInstanceId { get; set; }
    [JsonPropertyName("idempotencyKey")] public string? IdempotencyKey { get; set; }
    [JsonPropertyName("markdown")] public string? Markdown { get; set; }
    [JsonPropertyName("flow")] public Testing.MauiFlow? Flow { get; set; }
    [JsonPropertyName("timeoutMs")] public int? TimeoutMs { get; set; }
    [JsonPropertyName("plan")] public Testing.MauiTestPlan? Plan { get; set; }
    [JsonPropertyName("context")] public Testing.MauiFlowRunContext? Context { get; set; }
}

internal sealed class WorkflowRunAccessRequest
{
    [JsonPropertyName("capabilityToken")] public string? CapabilityToken { get; set; }
}

internal sealed class WorkflowRunCapabilitiesResponse
{
    [JsonPropertyName("schema")] public int Schema { get; init; } = 1;
    [JsonPropertyName("supported")] public bool Supported { get; init; } = true;
    [JsonPropertyName("requiresExplicitAgentInstance")] public bool RequiresExplicitAgentInstance { get; init; } = true;
    [JsonPropertyName("requiresIdempotencyKey")] public bool RequiresIdempotencyKey { get; init; } = true;
    [JsonPropertyName("capabilityTokenRequired")] public bool CapabilityTokenRequired { get; init; } = true;
    [JsonPropertyName("states")] public string[] States { get; init; } =
        Enum.GetValues<WorkflowRunState>().Select(WorkflowRunStates.ToWireValue).ToArray();
    [JsonPropertyName("maxTimeoutMs")] public long MaxTimeoutMs { get; init; }
    [JsonPropertyName("maxSteps")] public int MaxSteps { get; init; }
    [JsonPropertyName("workflowCommandLedger")] public bool WorkflowCommandLedger { get; init; }
}

internal sealed class WorkflowRunStartResult
{
    [JsonPropertyName("ok")] public bool Ok { get; private init; }
    [JsonPropertyName("existing")] public bool Existing { get; private init; }
    [JsonPropertyName("run")] public WorkflowRunSnapshot? Run { get; private init; }
    [JsonPropertyName("capabilityToken")] public string? CapabilityToken { get; private init; }
    [JsonPropertyName("error")] public string? Error { get; private init; }
    [JsonPropertyName("errors")] public IReadOnlyList<string>? Errors { get; private init; }
    [JsonPropertyName("warnings")] public IReadOnlyList<string>? Warnings { get; private init; }
    [JsonPropertyName("admission")] public Testing.MauiFlowReplayEligibilityDecision? Admission { get; private init; }
    [JsonIgnore] public int StatusCode { get; private init; }

    public static WorkflowRunStartResult Started(WorkflowRunSnapshot run, string capabilityToken) => new()
    {
        Ok = true,
        Run = run,
        CapabilityToken = capabilityToken,
        StatusCode = 202
    };

    public static WorkflowRunStartResult FromExisting(WorkflowRunSnapshot run, string capabilityToken) => new()
    {
        Ok = true,
        Existing = true,
        Run = run,
        CapabilityToken = capabilityToken,
        StatusCode = 200
    };

    public static WorkflowRunStartResult Conflict(string error) => new()
    {
        Error = error,
        StatusCode = 409
    };

    public static WorkflowRunStartResult Rejected(
        int statusCode,
        string error,
        IReadOnlyList<string>? errors,
        IReadOnlyList<string>? warnings,
        Testing.MauiFlowReplayEligibilityDecision? admission = null) => new()
    {
        Error = error,
        Errors = errors is { Count: > 0 } ? errors : null,
        Warnings = warnings is { Count: > 0 } ? warnings : null,
        Admission = admission,
        StatusCode = statusCode
    };
}

internal sealed class WorkflowRunAccessResult
{
    public WorkflowRunSnapshot? Run { get; private init; }
    public int StatusCode { get; private init; }
    public string? Error { get; private init; }

    public static WorkflowRunAccessResult Success(WorkflowRunSnapshot run) => new() { Run = run, StatusCode = 200 };
    public static WorkflowRunAccessResult NotFound() => new() { StatusCode = 404, Error = "Workflow run was not found." };
    public static WorkflowRunAccessResult Unauthorized() => new() { StatusCode = 403, Error = "A valid workflow run capability token is required." };
}

internal sealed class WorkflowRunStatusResponse
{
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("run")] public WorkflowRunSnapshot? Run { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }

    public static WorkflowRunStatusResponse Success(WorkflowRunSnapshot run) => new() { Ok = true, Run = run };
    public static WorkflowRunStatusResponse Failure(string error) => new() { Error = error };
}

internal sealed class WorkflowRunCancelResult
{
    [JsonPropertyName("ok")] public bool Ok { get; private init; }
    [JsonPropertyName("accepted")] public bool Accepted { get; private init; }
    [JsonPropertyName("alreadyTerminal")] public bool AlreadyTerminal { get; private init; }
    [JsonPropertyName("run")] public WorkflowRunSnapshot? Run { get; private init; }
    [JsonPropertyName("error")] public string? Error { get; private init; }
    [JsonIgnore] public int StatusCode { get; private init; }

    public static WorkflowRunCancelResult FromAccepted(WorkflowRunSnapshot run, bool alreadyTerminal) => new()
    {
        Ok = true,
        Accepted = !alreadyTerminal,
        AlreadyTerminal = alreadyTerminal,
        Run = run,
        StatusCode = 200
    };

    public static WorkflowRunCancelResult NotFound() => new()
    {
        StatusCode = 404,
        Error = "Workflow run was not found."
    };

    public static WorkflowRunCancelResult Unauthorized() => new()
    {
        StatusCode = 403,
        Error = "A valid workflow run capability token is required."
    };
}

internal sealed class WorkflowRunSnapshot
{
    [JsonPropertyName("schema")] public int Schema { get; init; }
    [JsonPropertyName("runId")] public string RunId { get; init; } = "";
    [JsonPropertyName("state")] public string State { get; init; } = "";
    [JsonPropertyName("terminal")] public bool Terminal { get; init; }
    [JsonPropertyName("flowDigest")] public string FlowDigest { get; init; } = "";
    [JsonPropertyName("authorityEpoch")] public long? AuthorityEpoch { get; init; }
    [JsonPropertyName("target")] public WorkflowRunTargetSnapshot Target { get; init; } = new();
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("startedAt")] public DateTimeOffset? StartedAt { get; init; }
    [JsonPropertyName("endedAt")] public DateTimeOffset? EndedAt { get; init; }
    [JsonPropertyName("firstDivergence")] public int? FirstDivergence { get; init; }
    [JsonPropertyName("cancellationRequested")] public bool CancellationRequested { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
    [JsonPropertyName("events")] public IReadOnlyList<WorkflowRunLifecycleEvent> Events { get; init; } = [];
    [JsonPropertyName("report")] public Testing.MauiFlowRunReport? Report { get; init; }
    [JsonPropertyName("reportPath")] public string? ReportPath { get; init; }
    [JsonPropertyName("reportDigest")] public string? ReportDigest { get; init; }
    [JsonPropertyName("admission")] public Testing.MauiFlowReplayEligibilityDecision? Admission { get; init; }
    [JsonIgnore] public Testing.FlowReplayReport? CompatibilityReport { get; init; }
}

internal sealed class WorkflowRunLifecycleEvent
{
    [JsonPropertyName("at")] public DateTimeOffset At { get; init; }
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("message")] public string Message { get; init; } = "";
}

internal sealed record WorkflowRunTarget(
    string AgentId,
    string AgentInstanceId,
    int AgentPort,
    string? Platform = null,
    string? AppName = null)
{
    public WorkflowRunTargetSnapshot ToSnapshot() => new()
    {
        AgentId = AgentId,
        AgentInstanceId = AgentInstanceId,
        Platform = Platform,
        AppName = AppName
    };
}

internal sealed class WorkflowRunTargetSnapshot
{
    [JsonPropertyName("agentId")] public string AgentId { get; init; } = "";
    [JsonPropertyName("agentInstanceId")] public string AgentInstanceId { get; init; } = "";
    [JsonPropertyName("platform")] public string? Platform { get; init; }
    [JsonPropertyName("appName")] public string? AppName { get; init; }
}

internal sealed class WorkflowRunExecutionOptions
{
    public Func<AgentClient, Testing.IFlowReplayEvidenceCapture?>? EvidenceCaptureFactory { get; init; }
}

internal sealed record WorkflowRunExecution(
    string RunId,
    Testing.MauiFlow Flow,
    Testing.MauiFlowRunRequest SafetyRequest,
    Testing.MauiFlowReplayEligibilityDecision Admission,
    WorkflowRunTarget Target,
    string LeaseId,
    string TransactionId,
    long AuthorityEpoch,
    WorkflowRunExecutionOptions Options);

internal sealed class WorkflowRunNotFoundException : Exception
{
    public WorkflowRunNotFoundException(string runId)
        : base($"Workflow run '{runId}' was not found.")
    {
    }
}

internal sealed class WorkflowRunCapabilityException : Exception
{
    public WorkflowRunCapabilityException()
        : base("A valid workflow run capability token is required.")
    {
    }
}

internal sealed class WorkflowRunRejectedException : Exception
{
    public WorkflowRunRejectedException(int statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}

internal readonly record struct WorkflowRunTargetKey(string AgentId, string AgentInstanceId);
