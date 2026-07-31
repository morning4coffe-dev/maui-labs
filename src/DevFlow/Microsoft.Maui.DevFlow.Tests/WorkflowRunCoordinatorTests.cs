using System.Collections.Concurrent;
using Microsoft.Maui.Cli.DevFlow.Broker;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.DevFlow.Tests;

public class WorkflowRunCoordinatorTests
{
    [Fact]
    public async Task Start_PassingAndFailingRuns_PreservesTerminalReportsAndFirstDivergence()
    {
        var leases = new RecordingLeaseRegistry();
        var coordinator = new WorkflowRunCoordinator(
            leases,
            (execution, _) => Task.FromResult(
                execution.Flow.Name == "pass"
                    ? PassingReport(execution.Flow)
                    : FailingReport(execution.Flow)));

        var passed = coordinator.Start(Request("pass", "pass-key"), Target(), static () => true);
        var passedSnapshot = await WaitForTerminalAsync(coordinator, passed);
        var failed = coordinator.Start(Request("fail", "fail-key"), Target(), static () => true);
        var failedSnapshot = await WaitForTerminalAsync(coordinator, failed);

        Assert.True(passed.Ok);
        Assert.Equal("passed", passedSnapshot.State);
        Assert.True(passedSnapshot.Terminal);
        Assert.NotNull(passedSnapshot.Report);
        Assert.Equal("passed", passedSnapshot.Report!.Outcome!.Status);

        Assert.True(failed.Ok);
        Assert.Equal("failed", failedSnapshot.State);
        Assert.Equal(1, failedSnapshot.FirstDivergence);
        Assert.NotNull(failedSnapshot.Report);
        Assert.Equal("1", failedSnapshot.Report!.DivergenceStepId);
        Assert.Equal(MauiFlowFailureClasses.AssertionFailed, failedSnapshot.Report.Failure!.Class);
    }

    [Fact]
    public void Start_InvalidFlow_RejectsBeforeAcquiringLease()
    {
        var leases = new RecordingLeaseRegistry();
        var coordinator = new WorkflowRunCoordinator(
            leases,
            static (_, _) => Task.FromResult(PassingReport()));
        var invalid = new MauiFlow
        {
            Name = "invalid",
            Steps =
            {
                new FlowStep
                {
                    Seq = 1,
                    Action = FlowActions.SetProperty,
                    Args = new FlowStepArgs
                    {
                        Selector = new FlowSelector { AutomationId = "label" },
                        Value = "hello"
                    }
                }
            }
        };

        var result = coordinator.Start(
            new WorkflowRunStartRequest
            {
                AgentId = "agent",
                AgentInstanceId = "instance",
                IdempotencyKey = "invalid-key",
                Flow = invalid
            },
            Target(),
            static () => true);

        Assert.False(result.Ok);
        Assert.Equal(400, result.StatusCode);
        Assert.Contains(result.Errors!, error => error.Contains("setProperty requires a property name", StringComparison.Ordinal));
        Assert.Empty(leases.Actions);
    }

    [Fact]
    public void Start_SideEffectAdmissionDeniedBeforeLeaseOrRunnerInvocation()
    {
        var leases = new RecordingLeaseRegistry();
        var executed = false;
        var coordinator = new WorkflowRunCoordinator(
            leases,
            (_, _) =>
            {
                executed = true;
                return Task.FromResult(PassingReport());
            });
        var request = Request("unsafe", "unsafe-key");
        request.Plan = new MauiTestPlan
        {
            SideEffectPolicy = MauiFlowSideEffectPolicies.TestTenantResettable,
            Checkpoint = new MauiFlowCheckpointRequirements
            {
                AppBuildFingerprint = "build-1",
                Route = "/home",
            },
        };
        request.Context = new MauiFlowRunContext
        {
            Preconditions = new MauiFlowReplayPreconditions
            {
                Expected = new MauiFlowCheckpoint
                {
                    AppBuildFingerprint = "build-1",
                    Route = "/home",
                },
                Observed = new MauiFlowCheckpoint
                {
                    AppBuildFingerprint = "build-1",
                    Route = "/home",
                },
            },
        };

        var result = coordinator.Start(request, Target(), static () => true);

        Assert.False(result.Ok);
        Assert.Equal(409, result.StatusCode);
        Assert.NotNull(result.Admission);
        Assert.False(result.Admission!.OrdinaryReplayAllowed);
        Assert.Contains(result.Admission.Reasons, reason => reason.Code == "reset-proof-required");
        Assert.False(executed);
        Assert.Empty(leases.Actions);
    }

    [Fact]
    public async Task Start_ActiveRunForTarget_RejectsSecondMutatingRun()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leases = new RecordingLeaseRegistry();
        var coordinator = new WorkflowRunCoordinator(
            leases,
            async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return PassingReport();
            });

        var first = coordinator.Start(Request("first", "first-key"), Target(), static () => true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = coordinator.Start(Request("second", "second-key"), Target(), static () => true);

        Assert.True(first.Ok);
        Assert.False(second.Ok);
        Assert.Equal(409, second.StatusCode);

        var cancelled = coordinator.Cancel(first.Run!.RunId, first.CapabilityToken);
        Assert.True(cancelled.Ok);
        Assert.Equal("cancelled", (await WaitForTerminalAsync(coordinator, first)).State);
    }

    [Fact]
    public async Task Start_SameIdempotencyKeyAndDigest_ReturnsOriginalRun_AndDifferentDigestConflicts()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new WorkflowRunCoordinator(
            new RecordingLeaseRegistry(),
            async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return PassingReport();
            });
        var request = Request("first", "shared-key");

        var original = coordinator.Start(request, Target(), static () => true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duplicate = coordinator.Start(Request("first", "shared-key"), Target(), static () => true);
        var conflict = coordinator.Start(Request("different", "shared-key"), Target(), static () => true);

        Assert.True(original.Ok);
        Assert.True(duplicate.Ok);
        Assert.True(duplicate.Existing);
        Assert.Equal(original.Run!.RunId, duplicate.Run!.RunId);
        Assert.Equal(original.CapabilityToken, duplicate.CapabilityToken);
        Assert.False(conflict.Ok);
        Assert.Equal(409, conflict.StatusCode);

        coordinator.Cancel(original.Run.RunId, original.CapabilityToken);
        await WaitForTerminalAsync(coordinator, original);
    }

    [Fact]
    public async Task Cancel_RunningRun_TerminatesAsCancelled()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new WorkflowRunCoordinator(
            new RecordingLeaseRegistry(),
            async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return PassingReport();
            });

        var start = coordinator.Start(Request("cancel", "cancel-key"), Target(), static () => true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancel = coordinator.Cancel(start.Run!.RunId, start.CapabilityToken);
        var terminal = await WaitForTerminalAsync(coordinator, start);

        Assert.True(cancel.Ok);
        Assert.True(cancel.Accepted);
        Assert.Equal("cancelled", terminal.State);
        Assert.Equal(MauiFlowFailureClasses.Cancelled, terminal.Report!.Failure!.Class);
    }

    [Fact]
    public async Task Start_TimedOutRun_TerminatesAsTimedOut()
    {
        var coordinator = new WorkflowRunCoordinator(
            new RecordingLeaseRegistry(),
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return PassingReport();
            },
            new WorkflowRunCoordinatorOptions
            {
                DefaultTimeout = TimeSpan.FromMilliseconds(25),
                MaximumTimeout = TimeSpan.FromSeconds(1),
                HeartbeatInterval = TimeSpan.FromMilliseconds(5)
            });

        var start = coordinator.Start(Request("timeout", "timeout-key", timeoutMs: 25), Target(), static () => true);
        var terminal = await WaitForTerminalAsync(coordinator, start);

        Assert.Equal("timed-out", terminal.State);
        Assert.Equal(MauiFlowFailureClasses.Timeout, terminal.Report!.Failure!.Class);
    }

    [Fact]
    public async Task Run_LeaseTransactionHeartbeatsAndAlwaysCleansUpAfterRunnerException()
    {
        var passingLeases = new RecordingLeaseRegistry();
        var passingCoordinator = new WorkflowRunCoordinator(
            passingLeases,
            async (_, cancellationToken) =>
            {
                await Task.Delay(35, cancellationToken);
                return PassingReport();
            },
            new WorkflowRunCoordinatorOptions
            {
                HeartbeatInterval = TimeSpan.FromMilliseconds(5)
            });

        var passing = passingCoordinator.Start(Request("lease", "lease-key"), Target(), static () => true);
        Assert.Equal("passed", (await WaitForTerminalAsync(passingCoordinator, passing)).State);
        Assert.Contains("claim", passingLeases.Actions);
        Assert.Contains("begin", passingLeases.Actions);
        Assert.Contains("heartbeat", passingLeases.Actions);
        Assert.Contains("end", passingLeases.Actions);
        Assert.Contains("release", passingLeases.Actions);

        var failingLeases = new RecordingLeaseRegistry();
        var failingCoordinator = new WorkflowRunCoordinator(
            failingLeases,
            static (_, _) => throw new InvalidOperationException("runner exploded"));

        var failed = failingCoordinator.Start(Request("exception", "exception-key"), Target(), static () => true);
        var failedTerminal = await WaitForTerminalAsync(failingCoordinator, failed);

        Assert.Equal("infrastructure-error", failedTerminal.State);
        Assert.Contains("end", failingLeases.Actions);
        Assert.Contains("release", failingLeases.Actions);
    }

    [Fact]
    public async Task Start_StaleInstanceIsRejected_AndReconnectOrphansActiveRun()
    {
        var staleCoordinator = new WorkflowRunCoordinator(
            new RecordingLeaseRegistry(),
            static (_, _) => Task.FromResult(PassingReport()));

        var stale = staleCoordinator.Start(Request("stale", "stale-key"), Target(), static () => false);
        Assert.False(stale.Ok);
        Assert.Equal(409, stale.StatusCode);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new WorkflowRunCoordinator(
            new RecordingLeaseRegistry(),
            async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return PassingReport();
            });
        var start = coordinator.Start(Request("reconnect", "reconnect-key"), Target(), static () => true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        coordinator.MarkAgentInstanceUnavailable("agent", "instance", "Agent reconnected.");
        var terminal = await WaitForTerminalAsync(coordinator, start);

        Assert.Equal("orphaned", terminal.State);
        Assert.Equal(MauiFlowFailureClasses.AgentDisconnected, terminal.Report!.Failure!.Class);
    }

    [Fact]
    public async Task Run_AgentLedgerBeginsEndsAndAbandonsOnRunnerException()
    {
        var controls = new RecordingLedgerController();
        var coordinator = new WorkflowRunCoordinator(
            new RecordingLeaseRegistry(),
            static (_, _) => Task.FromResult(PassingReport()),
            controlLedger: controls.ControlAsync);

        var passed = coordinator.Start(Request("ledger-pass", "ledger-pass-key"), Target(), static () => true);
        Assert.Equal("passed", (await WaitForTerminalAsync(coordinator, passed)).State);
        Assert.Equal(new[] { "begin", "end" }, controls.Actions.ToArray());

        var failingControls = new RecordingLedgerController();
        var failingCoordinator = new WorkflowRunCoordinator(
            new RecordingLeaseRegistry(),
            static (_, _) => throw new InvalidOperationException("runner exploded"),
            controlLedger: failingControls.ControlAsync);
        var failed = failingCoordinator.Start(Request("ledger-fail", "ledger-fail-key"), Target(), static () => true);

        Assert.Equal("infrastructure-error", (await WaitForTerminalAsync(failingCoordinator, failed)).State);
        Assert.Equal(new[] { "begin", "abandon" }, failingControls.Actions.ToArray());

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellingControls = new RecordingLedgerController();
        var cancellingCoordinator = new WorkflowRunCoordinator(
            new RecordingLeaseRegistry(),
            async (_, cancellationToken) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return PassingReport();
            },
            controlLedger: cancellingControls.ControlAsync);
        var cancelled = cancellingCoordinator.Start(Request("ledger-cancel", "ledger-cancel-key"), Target(), static () => true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellingCoordinator.Cancel(cancelled.Run!.RunId, cancelled.CapabilityToken);

        Assert.Equal("cancelled", (await WaitForTerminalAsync(cancellingCoordinator, cancelled)).State);
        Assert.Equal(new[] { "begin", "abandon" }, cancellingControls.Actions.ToArray());
    }

    [Fact]
    public async Task Run_UnknownLedgerCompletion_MapsToExplicitTerminalFailure()
    {
        var controls = new RecordingLedgerController
        {
            ResultFactory = control => control.Action == "end"
                ? WorkflowRunLedgerControlResult.Success()
                : WorkflowRunLedgerControlResult.Success()
        };
        var coordinator = new WorkflowRunCoordinator(
            new RecordingLeaseRegistry(),
            static (execution, _) => Task.FromResult(new FlowReplayReport
            {
                Ok = false,
                Name = execution.Flow.Name,
                Total = 1,
                Failed = 1,
                DivergencePoint = 1,
                Results =
                {
                    new FlowStepResult
                    {
                        Seq = 1,
                        Action = FlowActions.Tap,
                        Label = "Tap",
                        Ok = false,
                        FailureKind = FlowFailureKinds.UnknownCompletion,
                        Error = "The command receipt was not returned."
                    }
                }
            }),
            controlLedger: controls.ControlAsync);

        var start = coordinator.Start(Request("unknown", "unknown-key"), Target(), static () => true);
        var terminal = await WaitForTerminalAsync(coordinator, start);

        Assert.Equal("unknown-completion", terminal.State);
        Assert.Equal(MauiFlowFailureClasses.UnknownCompletion, terminal.Report!.Failure!.Class);
        Assert.Equal(new[] { "begin", "abandon" }, controls.Actions.ToArray());
    }

    [Fact]
    public async Task Run_LedgerControlConflict_MapsToExplicitFailure()
    {
        var controls = new RecordingLedgerController
        {
            ResultFactory = control => control.Action == "begin"
                ? WorkflowRunLedgerControlResult.Failure("workflow-command-conflict", "Command sequence conflict.")
                : WorkflowRunLedgerControlResult.Success()
        };
        var coordinator = new WorkflowRunCoordinator(
            new RecordingLeaseRegistry(),
            static (_, _) => Task.FromResult(PassingReport()),
            controlLedger: controls.ControlAsync);

        var start = coordinator.Start(Request("conflict", "conflict-key"), Target(), static () => true);
        var terminal = await WaitForTerminalAsync(coordinator, start);

        Assert.Equal("failed", terminal.State);
        Assert.Equal(MauiFlowFailureClasses.WorkflowCommandConflict, terminal.Report!.Failure!.Class);
        Assert.Equal(new[] { "begin", "abandon" }, controls.Actions.ToArray());
    }

    [Fact]
    public async Task Retention_EvictsOldTerminalRunsDeterministicallyAndNeverActiveRuns()
    {
        var coordinator = new WorkflowRunCoordinator(
            new RecordingLeaseRegistry(),
            static (execution, _) => Task.FromResult(PassingReport(execution.Flow)),
            new WorkflowRunCoordinatorOptions
            {
                MaxRetainedTerminalRuns = 2
            });

        var first = coordinator.Start(Request("one", "one-key"), Target(), static () => true);
        await WaitForTerminalAsync(coordinator, first);
        var second = coordinator.Start(Request("two", "two-key"), Target(), static () => true);
        await WaitForTerminalAsync(coordinator, second);
        var third = coordinator.Start(Request("three", "three-key"), Target(), static () => true);
        await WaitForTerminalAsync(coordinator, third);

        Assert.Equal(404, coordinator.GetStatus(first.Run!.RunId, first.CapabilityToken).StatusCode);
        Assert.Equal(200, coordinator.GetStatus(second.Run!.RunId, second.CapabilityToken).StatusCode);
        Assert.Equal(200, coordinator.GetStatus(third.Run!.RunId, third.CapabilityToken).StatusCode);

        var activeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeCoordinator = new WorkflowRunCoordinator(
            new RecordingLeaseRegistry(),
            async (execution, cancellationToken) =>
            {
                if (execution.Flow.Name == "active")
                {
                    activeEntered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                return PassingReport(execution.Flow);
            },
            new WorkflowRunCoordinatorOptions
            {
                MaxRetainedTerminalRuns = 1
            });
        var active = activeCoordinator.Start(Request("active", "active-key"), Target(), static () => true);
        await activeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var completed = activeCoordinator.Start(
            Request("completed", "completed-key", agentId: "other-agent", instanceId: "other-instance"),
            Target("other-agent", "other-instance"),
            static () => true);
        await WaitForTerminalAsync(activeCoordinator, completed);

        Assert.Equal(200, activeCoordinator.GetStatus(active.Run!.RunId, active.CapabilityToken).StatusCode);
        Assert.Equal(200, activeCoordinator.GetStatus(completed.Run!.RunId, completed.CapabilityToken).StatusCode);

        activeCoordinator.Cancel(active.Run.RunId, active.CapabilityToken);
        await WaitForTerminalAsync(activeCoordinator, active);
        Assert.Equal(404, activeCoordinator.GetStatus(completed.Run!.RunId, completed.CapabilityToken).StatusCode);
    }

    [Fact]
    public async Task TerminalRun_WritesAtomicStructuredReportWhenArtifactRootConfigured()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "workflow-run-artifacts",
            Guid.NewGuid().ToString("N"));
        try
        {
            var coordinator = new WorkflowRunCoordinator(
                new RecordingLeaseRegistry(),
                static (execution, _) => Task.FromResult(FailingReport(execution.Flow)),
                new WorkflowRunCoordinatorOptions { ArtifactRoot = root });

            var start = coordinator.Start(Request("artifact", "artifact-key"), Target(), static () => true);
            var terminal = await WaitForTerminalAsync(coordinator, start);

            Assert.NotNull(terminal.ReportPath);
            Assert.NotNull(terminal.ReportDigest);
            Assert.True(File.Exists(terminal.ReportPath));
            Assert.Equal(terminal.ReportPath, terminal.Report!.ReportPath);
            Assert.Equal(terminal.ReportDigest, terminal.Report.ReportDigest);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(terminal.ReportPath!)!, "*.tmp"));
            var json = await File.ReadAllTextAsync(terminal.ReportPath!);
            Assert.Contains("\"runId\"", json, StringComparison.Ordinal);
            Assert.Contains("\"failure\"", json, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static WorkflowRunStartRequest Request(
        string name,
        string idempotencyKey,
        int? timeoutMs = null,
        string agentId = "agent",
        string instanceId = "instance") => new()
    {
        AgentId = agentId,
        AgentInstanceId = instanceId,
        IdempotencyKey = idempotencyKey,
        TimeoutMs = timeoutMs,
        Flow = new MauiFlow
        {
            Name = name,
            Steps =
            {
                new FlowStep
                {
                    Seq = 1,
                    Action = FlowActions.Assert,
                    Asserts = new()
                    {
                        new FlowAssert
                        {
                            Kind = "exists",
                            Verify = false,
                            Selector = new FlowSelector { AutomationId = "label" }
                        }
                    }
                }
            }
        }
    };

    private static WorkflowRunTarget Target(string agentId = "agent", string instanceId = "instance")
        => new(agentId, instanceId, 12345, "test", "Test app");

    private static async Task<WorkflowRunSnapshot> WaitForTerminalAsync(
        WorkflowRunCoordinator coordinator,
        WorkflowRunStartResult start)
    {
        Assert.True(start.Ok);
        Assert.NotNull(start.Run);
        Assert.NotNull(start.CapabilityToken);
        return await coordinator.WaitForTerminalAsync(
            start.Run!.RunId,
            start.CapabilityToken!,
            new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
    }

    private static FlowReplayReport PassingReport(MauiFlow? flow = null) => new()
    {
        Ok = true,
        Name = flow?.Name ?? "pass",
        Total = flow?.Steps.Count ?? 1,
        Passed = flow?.Steps.Count ?? 1
    };

    private static FlowReplayReport FailingReport(MauiFlow flow) => new()
    {
        Ok = false,
        Name = flow.Name,
        Total = flow.Steps.Count,
        Failed = 1,
        DivergencePoint = 1,
        StoppedEarly = true,
        Results =
        {
            new FlowStepResult
            {
                Seq = 1,
                Action = FlowActions.Assert,
                Label = "Assert",
                Ok = false,
                Error = "Expected element was absent.",
                FailureKind = FlowFailureKinds.Assertion
            }
        }
    };

    private sealed class RecordingLeaseRegistry : IWorkflowMutationLeaseRegistry
    {
        private readonly ConcurrentQueue<string> _actions = new();

        public IReadOnlyCollection<string> Actions => _actions.ToArray();

        public MutationLeaseSnapshot Control(
            string agentId,
            string action,
            string? leaseId,
            string? holderKind,
            string? label,
            bool force,
            string? transactionId)
        {
            _actions.Enqueue(action);
            return new MutationLeaseSnapshot(
                Allowed: true,
                YouHold: true,
                HeldByOther: false,
                LeaseId: leaseId,
                TransactionId: transactionId,
                HolderKind: holderKind,
                Label: label,
                ExpiresInMs: 10_000)
            {
                AuthorityEpoch = 1
            };
        }
    }

    private sealed class RecordingLedgerController
    {
        private readonly ConcurrentQueue<string> _actions = new();

        public IReadOnlyCollection<string> Actions => _actions.ToArray();
        public Func<WorkflowRunLedgerControl, WorkflowRunLedgerControlResult>? ResultFactory { get; init; }

        public Task<WorkflowRunLedgerControlResult> ControlAsync(
            WorkflowRunLedgerControl control,
            CancellationToken cancellationToken)
        {
            _actions.Enqueue(control.Action);
            return Task.FromResult(ResultFactory?.Invoke(control) ?? WorkflowRunLedgerControlResult.Success());
        }
    }
}
