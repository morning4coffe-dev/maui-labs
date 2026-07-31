using System.Text;
using System.Text.Json;
using Microsoft.Maui.DevFlow.Driver;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.DevFlow.Tests;

public sealed class MauiFlowReplaySafetyEvaluatorTests
{
    [Fact]
    public void Evaluate_NonePolicy_WithMatchingPreconditions_AllowsReplayAndVerifiedRepairEligibility()
    {
        var decision = MauiFlowReplaySafetyEvaluator.Evaluate(Request(
            Plan(MauiFlowSideEffectPolicies.None),
            Context(includeReset: false, includeOracle: true)));

        Assert.True(decision.OrdinaryReplayAllowed);
        Assert.True(decision.DiagnosticReplayAllowed);
        Assert.True(decision.DownstreamContinuationAllowed);
        Assert.True(decision.RepairValidationAllowed);
        Assert.True(decision.RepairEligibility);
        Assert.True(decision.RunVerificationAllowed);
        Assert.Equal(MauiFlowSideEffectPolicies.None, decision.SideEffectPolicy);
    }

    [Fact]
    public void Evaluate_TestTenantResettable_WithoutBackendResetProof_DeniesBeforeReplay()
    {
        var context = Context(includeReset: true, includeOracle: true);
        context.Reset!.BackendTestDataSucceeded = false;
        context.Reset.Outcome!.BackendTestDataSucceeded = false;

        var decision = MauiFlowReplaySafetyEvaluator.Evaluate(Request(
            Plan(MauiFlowSideEffectPolicies.TestTenantResettable),
            context));

        Assert.False(decision.OrdinaryReplayAllowed);
        Assert.False(decision.DiagnosticReplayAllowed);
        Assert.False(decision.RepairValidationAllowed);
        Assert.Contains(decision.Reasons, reason => reason.Code == "backend-test-data-reset-not-proven");
    }

    [Fact]
    public void Evaluate_Compensated_WithDeclaredSuccessfulCompensator_AllowsReplayWithoutReset()
    {
        var plan = Plan(MauiFlowSideEffectPolicies.Compensated);
        plan.Compensator = new MauiFlowCompensatorReference
        {
            CompensatorId = "delete-test-order",
            Scope = "test-tenant",
        };
        var context = Context(includeReset: false, includeOracle: true);
        context.Compensator = new MauiFlowCompensatorOutcome
        {
            Compensator = new MauiFlowCompensatorReference { CompensatorId = "delete-test-order" },
            Succeeded = true,
            EvidenceReference = "compensation-evidence",
        };

        var decision = MauiFlowReplaySafetyEvaluator.Evaluate(Request(plan, context));

        Assert.True(decision.OrdinaryReplayAllowed);
        Assert.True(decision.DiagnosticReplayAllowed);
        Assert.True(decision.RepairEligibility);
    }

    [Fact]
    public void Evaluate_NonReplayable_RequiresDistinctManualAuthorizationAndNeverAllowsRepair()
    {
        var denied = MauiFlowReplaySafetyEvaluator.Evaluate(Request(
            Plan(MauiFlowSideEffectPolicies.NonReplayable),
            Context(includeReset: false, includeOracle: true)));
        Assert.False(denied.OrdinaryReplayAllowed);
        Assert.Contains(denied.Reasons, reason => reason.Code == "manual-one-shot-authorization-required");

        var authorizedContext = Context(includeReset: false, includeOracle: true);
        authorizedContext.ManualOneShotAuthorization = true;
        var authorized = MauiFlowReplaySafetyEvaluator.Evaluate(Request(
            Plan(MauiFlowSideEffectPolicies.NonReplayable),
            authorizedContext));

        Assert.True(authorized.OrdinaryReplayAllowed);
        Assert.False(authorized.DiagnosticReplayAllowed);
        Assert.False(authorized.DownstreamContinuationAllowed);
        Assert.False(authorized.RepairValidationAllowed);
        Assert.False(authorized.RepairEligibility);
        Assert.True(authorized.RunVerificationAllowed);
    }

    [Fact]
    public void Evaluate_MissingOrFailedIndependentOracle_PreventsVerification()
    {
        var missing = MauiFlowReplaySafetyEvaluator.Evaluate(Request(
            Plan(MauiFlowSideEffectPolicies.None),
            Context(includeReset: false, includeOracle: false)));

        Assert.True(missing.OrdinaryReplayAllowed);
        Assert.False(missing.RunVerificationAllowed);
        Assert.False(missing.RepairEligibility);
        Assert.Contains(missing.Reasons, reason => reason.Code == "independent-oracle-missing");

        var failedContext = Context(includeReset: false, includeOracle: true);
        failedContext.BusinessOracles[0].Succeeded = false;
        var failed = MauiFlowReplaySafetyEvaluator.Evaluate(Request(
            Plan(MauiFlowSideEffectPolicies.None),
            failedContext));

        Assert.False(failed.RunVerificationAllowed);
        Assert.False(failed.RepairValidationAllowed);
        Assert.False(failed.RepairEligibility);
        Assert.Contains(failed.Reasons, reason => reason.Code == "independent-oracle-failed");
    }

    [Fact]
    public void Evaluate_DeclaredCheckpointMismatchForEverySafetyField_DeniesAdmission()
    {
        var fieldMutations = new (string Code, Action<MauiFlowCheckpoint> Mutate)[]
        {
            ("app-build", checkpoint => checkpoint.AppBuildFingerprint = "other-build"),
            ("app-state-seed", checkpoint => checkpoint.SeedFingerprint = "other-app-seed"),
            ("backend-test-data-seed", checkpoint => checkpoint.BackendStateFingerprint = "other-backend-seed"),
            ("route", checkpoint => checkpoint.Route = "/other"),
            ("window", checkpoint => checkpoint.Window = "other-window"),
            ("modal", checkpoint => checkpoint.Modal = "other-modal"),
            ("locale", checkpoint => checkpoint.Locale = "fr-FR"),
            ("theme", checkpoint => checkpoint.Theme = "dark"),
            ("orientation", checkpoint => checkpoint.Orientation = "landscape"),
            ("display", checkpoint => checkpoint.DisplayProfile = "tablet"),
            ("collection-item-key", checkpoint => checkpoint.CollectionItemKey = "other-item"),
        };

        foreach (var (code, mutate) in fieldMutations)
        {
            var context = Context(includeReset: false, includeOracle: true);
            mutate(context.Preconditions!.Observed!);
            var decision = MauiFlowReplaySafetyEvaluator.Evaluate(Request(
                Plan(MauiFlowSideEffectPolicies.None),
                context));

            Assert.False(decision.OrdinaryReplayAllowed);
            Assert.Contains(decision.Reasons, reason => reason.Code == $"precondition-{code}-mismatch");
        }
    }

    [Fact]
    public void Evaluate_LegacyManualRequest_RemainsReplayableButNeverRepairEligible()
    {
        var decision = MauiFlowReplaySafetyEvaluator.Evaluate(new MauiFlowRunRequest());

        Assert.True(decision.OrdinaryReplayAllowed);
        Assert.True(decision.DiagnosticReplayAllowed);
        Assert.True(decision.DownstreamContinuationAllowed);
        Assert.False(decision.RepairValidationAllowed);
        Assert.False(decision.RepairEligibility);
        Assert.False(decision.RunVerificationAllowed);
        Assert.Equal(MauiFlowSideEffectPolicies.Unspecified, decision.SideEffectPolicy);
        Assert.Contains(decision.Reasons, reason => reason.Code == "side-effect-policy-unspecified");
    }

    [Fact]
    public async Task Runner_DeniedSafetyAdmission_DoesNotInvokeMutationDriver()
    {
        var driver = new NoMutationDriver();
        var context = Context(includeReset: false, includeOracle: true);
        var result = await new MauiFlowRunner(
            driver,
            new MauiFlowRunnerOptions
            {
                Plan = Plan(MauiFlowSideEffectPolicies.TestTenantResettable),
                RunContext = context,
            }).RunWithLegacyAsync(new MauiFlow
            {
                Name = "unsafe",
                Steps =
                [
                    new FlowStep
                    {
                        Seq = 1,
                        Action = FlowActions.Tap,
                        Args = new FlowStepArgs
                        {
                            Selector = new FlowSelector { AutomationId = "submit" },
                        },
                    },
                ],
            });

        Assert.False(result.LegacyReport.Ok);
        Assert.Equal(MauiFlowFailureClasses.PreconditionUnsatisfied, result.Report.Failure!.Code);
        Assert.Equal(0, driver.MutationCalls);
        Assert.False(result.Report.ReplayEligibility!.OrdinaryReplayAllowed);
    }

    [Fact]
    public void RunReport_RoundTripsSafetyEvidenceAndRedactsSensitiveNestedMessages()
    {
        const string secret = "CorrectHorseBatteryStaple";
        var decision = MauiFlowReplaySafetyEvaluator.Evaluate(Request(
            Plan(MauiFlowSideEffectPolicies.None),
            Context(includeReset: false, includeOracle: true)));
        decision.Reasons.Add(new MauiFlowReplayEligibilityReason
        {
            Code = "x",
            Message = secret,
            Blocking = false,
        });
        var report = new MauiFlowRunReport
        {
            RunId = "run-safety",
            FlowDigest = new string('a', 64),
            SideEffectPolicy = decision.SideEffectPolicy,
            Preconditions = new MauiFlowReplayPreconditions
            {
                Expected = Checkpoint(),
                Observed = Checkpoint(),
                EvidenceReference = $"oracle={secret}",
            },
            Reset = new MauiFlowResetResult
            {
                Reference = new MauiFlowResetReference { EvidenceReference = $"token={secret}" },
                Outcome = new MauiFlowResetOutcome { Message = $"secret={secret}" },
            },
            Compensator = new MauiFlowCompensatorOutcome
            {
                Message = $"password={secret}",
            },
            BusinessOracles =
            [
                new MauiIndependentBusinessOracleResult
                {
                    OracleId = "order-recorded",
                    Succeeded = true,
                    Message = $"token={secret}",
                },
            ],
            ReplayEligibility = decision,
            StartedAt = DateTimeOffset.UtcNow,
            EndedAt = DateTimeOffset.UtcNow,
            Outcome = new MauiFlowRunOutcome { Status = MauiFlowRunOutcomes.Passed, Terminal = true },
        };

        var json = Encoding.UTF8.GetString(MauiFlowRunReportSerializer.SerializeToUtf8Bytes(report));
        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        var roundTrip = JsonSerializer.Deserialize(json, MauiTestingJsonContext.Default.MauiFlowRunReport);

        Assert.NotNull(roundTrip);
        Assert.Equal(MauiFlowSideEffectPolicies.None, roundTrip!.SideEffectPolicy);
        Assert.NotNull(roundTrip.Preconditions);
        Assert.NotNull(roundTrip.Reset?.Reference);
        Assert.NotNull(roundTrip.ReplayEligibility);
        Assert.Single(roundTrip.BusinessOracles);
    }

    private static MauiFlowRunRequest Request(MauiTestPlan plan, MauiFlowRunContext context) => new()
    {
        Plan = plan,
        Context = context,
    };

    private static MauiTestPlan Plan(string policy) => new()
    {
        PlanId = "plan",
        SideEffectPolicy = policy,
        Checkpoint = new MauiFlowCheckpointRequirements
        {
            AppBuildFingerprint = "build-1",
            SeedFingerprint = "app-seed-1",
            BackendStateFingerprint = "backend-seed-1",
            Route = "/home",
            Window = "main",
            Modal = "none",
            Locale = "en-US",
            Theme = "light",
            Orientation = "portrait",
            DisplayProfile = "phone",
            CollectionItemKey = "item-1",
        },
        Reset = new MauiTestResetRequirement
        {
            SeedFingerprint = "app-seed-1",
            BackendStateFingerprint = "backend-seed-1",
        },
        IndependentBusinessOracles =
        [
            new MauiIndependentBusinessOracleDeclaration
            {
                OracleId = "order-recorded",
                Required = true,
                Independent = true,
            },
        ],
    };

    private static MauiFlowRunContext Context(bool includeReset, bool includeOracle)
    {
        var context = new MauiFlowRunContext
        {
            Preconditions = new MauiFlowReplayPreconditions
            {
                Expected = Checkpoint(),
                Observed = Checkpoint(),
            },
        };
        if (includeReset)
        {
            context.Reset = new MauiFlowResetResult
            {
                Requested = true,
                Succeeded = true,
                AppStateSucceeded = true,
                BackendTestDataSucceeded = true,
                SeedFingerprint = "app-seed-1",
                BackendStateFingerprint = "backend-seed-1",
                Outcome = new MauiFlowResetOutcome
                {
                    Succeeded = true,
                    AppStateSucceeded = true,
                    BackendTestDataSucceeded = true,
                },
            };
        }
        if (includeOracle)
        {
            context.BusinessOracles.Add(new MauiIndependentBusinessOracleResult
            {
                OracleId = "order-recorded",
                Succeeded = true,
                Independent = true,
            });
        }
        return context;
    }

    private static MauiFlowCheckpoint Checkpoint() => new()
    {
        AppBuildFingerprint = "build-1",
        SeedFingerprint = "app-seed-1",
        BackendStateFingerprint = "backend-seed-1",
        Route = "/home",
        Window = "main",
        Modal = "none",
        Locale = "en-US",
        Theme = "light",
        Orientation = "portrait",
        DisplayProfile = "phone",
        CollectionItemKey = "item-1",
    };

    private sealed class NoMutationDriver : IMauiFlowDriver
    {
        public int MutationCalls { get; private set; }
        public WorkflowCommandReceipt? LastWorkflowCommandReceipt => null;

        public Task<List<ElementInfo>> QueryAsync(string? type = null, string? automationId = null, string? text = null)
            => Task.FromResult(new List<ElementInfo>());

        public Task<ElementInfo?> GetElementAsync(string id) => Task.FromResult<ElementInfo?>(null);
        public Task<bool> TapAsync(string elementId)
        {
            MutationCalls++;
            return Task.FromResult(true);
        }

        public Task<bool> FillAsync(string elementId, string text)
        {
            MutationCalls++;
            return Task.FromResult(true);
        }

        public Task<bool> SetPropertyAsync(string elementId, string propertyName, string value)
        {
            MutationCalls++;
            return Task.FromResult(true);
        }

        public Task<bool> ScrollAsync(
            string? elementId = null,
            double deltaX = 0,
            double deltaY = 0,
            bool animated = true,
            int? itemIndex = null,
            string? scrollToPosition = null)
        {
            MutationCalls++;
            return Task.FromResult(true);
        }

        public Task<bool> NavigateAsync(string route)
        {
            MutationCalls++;
            return Task.FromResult(true);
        }

        public Task<bool> BackAsync()
        {
            MutationCalls++;
            return Task.FromResult(true);
        }

        public Task<ThemeResult> SetThemeAsync(DevFlowTheme theme)
        {
            MutationCalls++;
            return Task.FromResult(new ThemeResult { Success = true });
        }

        public Task<string?> GetPropertyAsync(string elementId, string propertyName) => Task.FromResult<string?>(null);
        public Task<AgentStatus?> GetStatusAsync() => Task.FromResult<AgentStatus?>(null);
    }
}
