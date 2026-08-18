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
}
