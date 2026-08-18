using Microsoft.Maui.DevFlow.Testing;
using Xunit;

namespace Microsoft.Maui.DevFlow.Tests;

/// <summary>
/// A real emulator run of `drifted-assert-after-commit` produced every fact the repair gate asks
/// for — locator-not-found on an action, resolution phase, no dispatch receipt, matching
/// checkpoints, independent oracle verified — and still reported `repairEligible: false`. These pin
/// the classifier against those exact observed facts so the gate can be trusted either way.
/// </summary>
public class RepairEligibilityFromObservedRunTests
{
    private static MauiFlowCheckpoint Expected() => new()
    {
        // What `flow run` now seals onto the expected checkpoint after preconditions verify: the
        // plan's declared route/window plus the run-identity facts a plan author cannot know.
        AppBuildFingerprint = "1.0:1",
        AgentInstanceId = "930a7a61f72fadf3e9e7cebcafd27f39",
        Route = "//native",
        Window = "window-0",
        Locale = "en-US",
        Theme = "Light",
        Orientation = "Portrait",
    };

    private static MauiFlowCheckpoint Observed() => new()
    {
        AppBuildFingerprint = "1.0:1",
        AgentInstanceId = "930a7a61f72fadf3e9e7cebcafd27f39",
        Route = "//native",
        Window = "window-0",
        Locale = "en-US",
        Theme = "Light",
        Orientation = "Portrait",
    };

    /// <summary>The exact facts the runner built for step 3 of the observed emulator run.</summary>
    private static MauiFlowFailureFacts ObservedRunFacts() => new()
    {
        LegacyFailureKind = FlowFailureKinds.NotFound,
        // Step 3 is a tap with no assertions, so no assertion selector drifted.
        AssertionTargetResolution = null,
        BeforeDispatch = true,
        CheckpointVerified = true,
        CheckpointMatches = true,
        RouteMatches = true,
    };

    [Fact]
    public void ActionSelectorDriftAfterCommit_IsRepairEligible()
    {
        var classification = MauiFlowFailureClassifier.Classify(ObservedRunFacts());

        Assert.Equal(MauiFlowFailureClasses.LocatorNotFound, classification.FailureClass);
        Assert.True(
            classification.RepairEligible,
            "An action selector that drifted after the business outcome verified is the one shape " +
            "selector repair exists for.");
    }

    [Fact]
    public void AssertionSelectorDrift_IsNeverRepairEligible()
    {
        var facts = ObservedRunFacts();
        facts.AssertionTargetResolution = FlowFailureKinds.NotFound;

        var classification = MauiFlowFailureClassifier.Classify(facts);

        // Re-pointing an assertion would change what the test checks. That must never be automatic.
        Assert.False(classification.RepairEligible);
    }

    [Fact]
    public void CheckpointDrift_IsNeverRepairEligible()
    {
        var facts = ObservedRunFacts();
        facts.CheckpointMatches = false;

        var classification = MauiFlowFailureClassifier.Classify(facts);

        Assert.False(classification.RepairEligible);
    }

    [Fact]
    public void CheckpointsFromTheObservedRun_Match()
    {
        // `flow run` seals the run-identity facts onto the expected checkpoint after preconditions
        // verify, so the checkpoint comparison no longer reports every one of them as missing.
        var input = new MauiFlowRepairEligibilityInput
        {
            Run = new MauiFlowRunReport
            {
                Outcome = new MauiFlowRunOutcome { Status = MauiFlowRunOutcomes.Failed },
                Failure = new MauiFlowFailure
                {
                    Class = MauiFlowFailureClasses.LocatorNotFound,
                    StepId = "3",
                },
                Steps =
                [
                    new MauiFlowStepAttempt
                    {
                        StepId = "3",
                        ExpectedCheckpoint = Expected(),
                        ObservedCheckpoint = Observed(),
                    },
                ],
            },
            ExpectedCheckpoint = Expected(),
            CurrentCheckpoint = Observed(),
        };

        var decision = MauiFlowRepairEligibilityEvaluator.Evaluate(input);

        // Seed and backend facts stay legitimately absent on a plan that declares no reset, so this
        // asserts only that the facts the run *can* establish are no longer reported missing.
        Assert.DoesNotContain(
            decision.Reasons,
            reason => reason.Code is "checkpoint-app-build-expected-missing"
                or "checkpoint-agent-instance-expected-missing"
                or "checkpoint-locale-expected-missing"
                or "checkpoint-theme-expected-missing"
                or "checkpoint-orientation-expected-missing");
    }

    /// <summary>
    /// The observed run reported `replayEligibility.repairEligibility: true` and
    /// `failure.repairEligible: false` in the same artifact. The cause was ordering, not
    /// classification: the runner materialised the conjunction while the run's required independent
    /// oracle had not been evaluated yet, and the post-run decision that lifted replay eligibility
    /// never revisited the failure.
    /// </summary>
    private static MauiFlowRunReport ReportWithClassifiedFailure(bool repairEligibility) => new()
    {
        RunId = "run-1",
        Outcome = new MauiFlowRunOutcome { Status = MauiFlowRunOutcomes.Failed },
        ReplayEligibility = new MauiFlowReplayEligibilityDecision
        {
            SideEffectPolicy = MauiFlowSideEffectPolicies.None,
            RepairValidationAllowed = repairEligibility,
            RepairEligibility = repairEligibility,
        },
        Failure = MauiFlowFailureClassifier.ToFailure(
            MauiFlowFailureClassifier.Classify(ObservedRunFacts()),
            "failure-run-1",
            FlowFailureKinds.NotFound,
            "3",
            DateTimeOffset.UnixEpoch),
    };

    [Fact]
    public void RepairEligibilityGate_RestoresTheClassifierVerdictWhenOraclesLaterSucceed()
    {
        // The run starts with a required oracle that has not been evaluated, so replay safety is
        // provisionally false and the gate suppresses the classifier's verdict.
        var report = ReportWithClassifiedFailure(repairEligibility: false);
        MauiFlowFailureClassifier.ApplyRepairEligibilityGate(report);

        Assert.False(report.Failure!.RepairEligible);
        Assert.True(
            report.Failure.ClassifierRepairEligible,
            "Suppressing the conjunction must not erase what the classifier concluded.");

        // The oracle then succeeds and the host installs the real decision. Re-applying the gate
        // must restore eligibility rather than leave the report contradicting itself.
        report.ReplayEligibility!.RepairEligibility = true;
        MauiFlowFailureClassifier.ApplyRepairEligibilityGate(report);

        Assert.True(report.Failure.RepairEligible);
        Assert.DoesNotContain(
            MauiFlowRunReportSerializer.Validate(report).Errors,
            error => error.Contains("repair", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RepairEligibilityGate_NeverManufacturesEligibilityTheClassifierRefused()
    {
        var facts = ObservedRunFacts();
        facts.AssertionTargetResolution = FlowFailureKinds.NotFound;
        var report = ReportWithClassifiedFailure(repairEligibility: true);
        report.Failure = MauiFlowFailureClassifier.ToFailure(
            MauiFlowFailureClassifier.Classify(facts),
            "failure-run-1",
            FlowFailureKinds.NotFound,
            "3",
            DateTimeOffset.UnixEpoch);

        MauiFlowFailureClassifier.ApplyRepairEligibilityGate(report);

        // Replay safety permits repairing from this run, but the symptom is assertion drift, which
        // is never automatically repairable. The permissive half must not win.
        Assert.False(report.Failure.RepairEligible);
        Assert.False(report.Failure.ClassifierRepairEligible);
    }

    [Fact]
    public void RepairEligibilityGate_IsIdempotentForReportsWithoutTheClassifierVerdict()
    {
        // A report produced before the classifier verdict was recorded separately carries only the
        // conjunction. Re-gating it must not promote an unknown verdict to eligible.
        var report = ReportWithClassifiedFailure(repairEligibility: true);
        report.Failure!.ClassifierRepairEligible = null;
        report.Failure.RepairEligible = false;

        MauiFlowFailureClassifier.ApplyRepairEligibilityGate(report);
        MauiFlowFailureClassifier.ApplyRepairEligibilityGate(report);

        Assert.False(report.Failure.RepairEligible);
    }

    private static MauiFlowRepairEligibilityInput PlanlessInput(MauiFlowRunReport run) => new()
    {
        Run = run,
        ReplayEligibility = run.ReplayEligibility,
        ExpectedCheckpoint = Expected(),
        CurrentCheckpoint = Observed(),
    };

    /// <summary>
    /// `maui devflow flow triage` is given only the manifest and the report, never the plan. It
    /// reported `side-effect-policy-repair-prohibited` and `independent-oracle-absent` for a run
    /// whose own report recorded policy `none` and a succeeded independent oracle, which reads as
    /// two hard defects in a flow that has neither.
    /// </summary>
    [Fact]
    public void PlanlessEvaluation_ReadsPolicyAndOracleFactsFromTheRunReport()
    {
        var run = ReportWithClassifiedFailure(repairEligibility: true);
        run.SideEffectPolicy = MauiFlowSideEffectPolicies.None;
        run.BusinessOracles =
        [
            new MauiIndependentBusinessOracleResult
            {
                OracleId = "todo-ledger-record",
                Independent = true,
                Succeeded = true,
            },
        ];
        run.ReplayEligibility!.RepairValidationAllowed = true;

        var codes = MauiFlowRepairEligibilityEvaluator.Evaluate(PlanlessInput(run))
            .Reasons.Select(static reason => reason.Code).ToList();

        Assert.DoesNotContain("side-effect-policy-repair-prohibited", codes);
        Assert.DoesNotContain("independent-oracle-absent", codes);
    }

    [Fact]
    public void PlanlessEvaluation_StillBlocksWhenTheReportItselfCarriesNoSuchEvidence()
    {
        // The fallback must read evidence, not assume it. A run with no recorded policy and an
        // oracle that did not succeed stays blocked for exactly those two reasons.
        var run = ReportWithClassifiedFailure(repairEligibility: false);
        run.SideEffectPolicy = null;
        run.BusinessOracles =
        [
            new MauiIndependentBusinessOracleResult
            {
                OracleId = "todo-ledger-record",
                Independent = true,
                Succeeded = false,
            },
        ];

        var codes = MauiFlowRepairEligibilityEvaluator.Evaluate(PlanlessInput(run))
            .Reasons.Select(static reason => reason.Code).ToList();

        Assert.Contains("side-effect-policy-repair-prohibited", codes);
        Assert.Contains("independent-oracle-absent", codes);
    }
}
