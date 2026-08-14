using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.DevFlow.Driver;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.DevFlow.Tests;

public sealed class MauiFlowRunReportTests : IDisposable
{
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory,
        "flow-run-report-tests",
        Guid.NewGuid().ToString("N"));

    public MauiFlowRunReportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void BuildFingerprint_UnknownPlaceholdersDoNotCreateFalsePreconditions()
    {
        Assert.Null(MauiFlowRunner.BuildFingerprint(new AgentStatus
        {
            App = new AppDescriptor { Version = "unknown", Build = "unknown" },
        }));
        Assert.Equal("unknown:42", MauiFlowRunner.BuildFingerprint(new AgentStatus
        {
            App = new AppDescriptor { Build = "42" },
        }));
    }

    [Fact]
    public void Validate_PassedExecutionMayRemainIndependentlyUnverified()
    {
        var report = new MauiFlowRunReport
        {
            RunId = "run-unverified",
            FlowDigest = new string('a', 64),
            SideEffectPolicy = MauiFlowSideEffectPolicies.None,
            StartedAt = DateTimeOffset.UnixEpoch,
            EndedAt = DateTimeOffset.UnixEpoch,
            Outcome = new MauiFlowRunOutcome
            {
                Status = MauiFlowRunOutcomes.Passed,
                Terminal = true,
                Verified = false,
            },
            Verification = new MauiFlowRunVerification { Verified = false },
            ReplayEligibility = new MauiFlowReplayEligibilityDecision
            {
                SideEffectPolicy = MauiFlowSideEffectPolicies.None,
                RunVerificationAllowed = false,
            },
        };

        var validation = MauiFlowRunReportSerializer.Validate(report);

        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
    }

    [Fact]
    public void Validate_VerifiedStatusMustMatchPassedExecutionAndCompatibilityMirror()
    {
        var report = new MauiFlowRunReport
        {
            RunId = "run-invalid-verification",
            FlowDigest = new string('a', 64),
            SideEffectPolicy = MauiFlowSideEffectPolicies.None,
            StartedAt = DateTimeOffset.UnixEpoch,
            EndedAt = DateTimeOffset.UnixEpoch,
            Outcome = new MauiFlowRunOutcome
            {
                Status = MauiFlowRunOutcomes.Failed,
                Terminal = true,
                Verified = false,
            },
            Verification = new MauiFlowRunVerification { Verified = true },
            Failure = new MauiFlowFailure
            {
                Class = MauiFlowFailureClasses.AssertionFailed,
                Code = MauiFlowFailureClasses.AssertionFailed,
                Retryable = false,
            },
            ReplayEligibility = new MauiFlowReplayEligibilityDecision
            {
                SideEffectPolicy = MauiFlowSideEffectPolicies.None,
                RunVerificationAllowed = true,
            },
        };

        var validation = MauiFlowRunReportSerializer.Validate(report);

        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Contains("passed execution", StringComparison.Ordinal));
        Assert.Contains(validation.Errors, error => error.Contains("must match", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_PreflightFailureWithoutFlowDigest_IsSchemaValid()
    {
        var report = new MauiFlowRunReport
        {
            RunId = "run-preflight",
            StartedAt = DateTimeOffset.UnixEpoch,
            EndedAt = DateTimeOffset.UnixEpoch.AddSeconds(1),
            Outcome = new MauiFlowRunOutcome
            {
                Status = MauiFlowRunOutcomes.Failed,
                Terminal = true,
                Verified = false,
            },
            Failure = new MauiFlowFailure
            {
                Class = MauiFlowFailureClasses.FlowInvalid,
                Code = "project-path-missing",
                Category = "flow",
                Phase = "validation",
                Retryable = false,
            },
        };

        var validation = MauiFlowRunReportSerializer.Validate(report);

        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
    }

    [Fact]
    public async Task RunAsync_Mutation_RecordsOrderedActionabilityAndWorkflowReceipt()
    {
        var driver = new FakeDriver { EmitReceipt = true };
        var report = await new MauiFlowRunner(
            driver,
            new MauiFlowRunnerOptions
            {
                RunId = "run-report",
                PollTries = 1,
                PollGapMs = 0,
            }).RunAsync(TapFlow());

        Assert.Equal(MauiFlowRunOutcomes.Passed, report.Outcome!.Status);
        Assert.Equal("run-report", report.RunId);
        Assert.Equal("agent-instance", report.Target!.AgentInstanceId);
        Assert.NotNull(report.FlowDigest);
        Assert.True(MauiFlowRunReportSerializer.Validate(report).IsValid);

        var step = Assert.Single(report.Steps);
        Assert.Equal("1", step.StepId);
        Assert.Equal(1, step.CandidateCount);
        Assert.Equal("resolved", step.TargetResolution!.Status);
        Assert.NotEmpty(step.Actionability);
        Assert.Equal(
            Enumerable.Range(1, step.Actionability.Count),
            step.Actionability.Select(attempt => attempt.Sequence!.Value).ToArray());
        Assert.All(step.Actionability, attempt => Assert.NotNull(attempt.WaitDurationMs));
        Assert.NotNull(step.Dispatch);
        Assert.Equal(7, step.Dispatch!.Sequence);
        Assert.Equal("cmd_1", step.Dispatch.CommandId);
        Assert.Equal("completed", step.Dispatch.CompletionCertainty);
        Assert.Equal("completed", step.CompletionCertainty);
    }

    [Fact]
    public async Task RunAsync_StableStepId_PreservesIdentityAndLegacySequence()
    {
        var flow = TapFlow();
        flow.Steps[0].StepId = "submit-order";

        var result = await new MauiFlowRunner(
            new FakeDriver(),
            new MauiFlowRunnerOptions { PollTries = 1, PollGapMs = 0 })
            .RunWithLegacyAsync(flow);

        Assert.Equal("submit-order", Assert.Single(result.Report.Steps).StepId);
        Assert.Equal(1, Assert.Single(result.LegacyReport.Results).Seq);
    }

    [Fact]
    public async Task RunWithLegacyAsync_SensitiveAssertion_RedactsStructuredValuesAndPreservesLegacyShape()
    {
        const string secret = "CorrectHorseBatteryStaple";
        var driver = new FakeDriver { PropertyValue = secret };
        var flow = new MauiFlow
        {
            Name = "assert-password",
            Steps =
            {
                new FlowStep
                {
                    Seq = 1,
                    Action = FlowActions.Assert,
                    Asserts =
                    [
                        new FlowAssert
                        {
                            Kind = "propEquals",
                            Verify = true,
                            Name = "Password",
                            Expected = secret,
                            Selector = new FlowSelector { AutomationId = "submit" },
                        },
                    ],
                },
            },
        };

        var result = await new MauiFlowRunner(driver, new MauiFlowRunnerOptions
        {
            PollTries = 1,
            PollGapMs = 0,
        }).RunWithLegacyAsync(flow);

        var assertion = Assert.Single(Assert.Single(result.Report.Steps).Assertions);
        Assert.True(assertion.Passed);
        Assert.Null(assertion.Expected);
        Assert.Null(assertion.Actual);
        Assert.Equal("redacted", assertion.ExpectedDisclosure!.State);
        Assert.Equal("redacted", assertion.ActualDisclosure!.State);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(result.Report, MauiTestingJsonContext.Default.MauiFlowRunReport));
        Assert.Equal(secret, result.LegacyReport.Results[0].Asserts[0].Actual);
        Assert.NotNull(result.LegacyReport.StructuredReport);
    }

    [Fact]
    public async Task RunAsync_BoundedEventsAndActionability_RecordsExplicitTruncation()
    {
        var report = await new MauiFlowRunner(
            new FakeDriver(),
            new MauiFlowRunnerOptions
            {
                PollTries = 3,
                PollGapMs = 0,
                ReportLimits = new MauiFlowRunReportLimits
                {
                    MaxEvents = 1,
                    MaxActionabilityAttemptsPerStep = 1,
                    MaxAssertionsPerStep = 1,
                    MaxArtifacts = 1,
                    MaxJsonBytes = 16 * 1024,
                },
            }).RunAsync(TapFlow());

        Assert.True(report.Truncated);
        Assert.Single(report.Events);
        Assert.Single(report.Steps[0].Actionability);
        Assert.Contains(report.Omissions, omission => omission.Kind is "events" or "actionability");
    }

    [Fact]
    public void UnexpectedInfrastructureFailure_UsesFixedPublicSafeMessage()
    {
        Assert.Equal(
            "The flow runner encountered an infrastructure error.",
            MauiFlowRunner.InfrastructureErrorMessage);
    }

    [Fact]
    public async Task RunAsync_StepObservationDelay_PausesOnlyBetweenSteps()
    {
        var flow = TapFlow();
        flow.Steps.Add(new FlowStep
        {
            Seq = 2,
            Action = FlowActions.Tap,
            Target = new FlowSelector { AutomationId = "submit" },
        });
        var stopwatch = Stopwatch.StartNew();

        var report = await new MauiFlowRunner(
            new FakeDriver(),
            new MauiFlowRunnerOptions
            {
                PollTries = 1,
                PollGapMs = 0,
                StepObservationDelayMs = 50,
            }).RunAsync(flow);

        Assert.Equal(MauiFlowRunOutcomes.Passed, report.Outcome!.Status);
        Assert.True(stopwatch.ElapsedMilliseconds >= 40, $"Observed {stopwatch.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public void ApplyLimits_OversizedStepSet_RetainsBoundedTerminalSummary()
    {
        const int maximum = 16 * 1024;
        var report = new MauiFlowRunReport
        {
            RunId = "run-size-limit",
            DivergenceStepId = "step-399",
            Outcome = new MauiFlowRunOutcome { Status = MauiFlowRunOutcomes.Failed, Terminal = true },
            Steps = Enumerable.Range(1, 400)
                .Select(index => new MauiFlowStepAttempt
                {
                    StepId = $"step-{index}",
                    Sequence = index,
                    Action = new string('a', 256),
                    Intent = new string('i', 1024),
                    Assertions =
                    [
                        new MauiFlowAssertionResult
                        {
                            Kind = "property",
                            Message = new string('m', 1024),
                        },
                    ],
                })
                .ToList(),
        };

        MauiFlowRunReportSerializer.ApplyLimits(
            report,
            new MauiFlowRunReportLimits { MaxJsonBytes = maximum });
        var bytes = MauiFlowRunReportSerializer.SerializeToUtf8Bytes(report);

        Assert.True(bytes.Length <= maximum);
        Assert.True(report.Truncated);
        Assert.Single(report.Steps);
        Assert.Equal("step-399", report.Steps[0].StepId);
    }

    [Fact]
    public async Task RunAsync_UnknownCompletion_UsesTypedFailureAndLegacyMapping()
    {
        var receipt = new WorkflowCommandReceipt
        {
            RunId = "run",
            Sequence = 3,
            CommandId = "cmd_unknown",
            ActionDigest = "digest",
            AuthorityEpoch = 1,
            AcknowledgementState = "prepared",
        };
        var driver = new FakeDriver
        {
            TapFailure = new WorkflowCommandException(
                "workflow-unknown-completion",
                receipt: receipt),
        };

        var result = await new MauiFlowRunner(driver, new MauiFlowRunnerOptions
        {
            PollTries = 1,
            PollGapMs = 0,
        }).RunWithLegacyAsync(TapFlow());

        Assert.Equal(MauiFlowRunOutcomes.Failed, result.Report.Outcome!.Status);
        Assert.Equal(MauiFlowFailureClasses.UnknownCompletion, result.Report.Failure!.Code);
        Assert.Equal(FlowFailureKinds.UnknownCompletion, result.LegacyReport.Results[0].FailureKind);
        Assert.Equal("unknown", result.Report.Steps[0].Dispatch!.CompletionCertainty);
    }

    [Fact]
    public void FailureClassifier_OnlyMarksVerifiedPreDispatchLocatorFailureRepairEligible()
    {
        var eligible = MauiFlowFailureClassifier.Classify(new MauiFlowFailureFacts
        {
            LegacyFailureKind = FlowFailureKinds.NotFound,
            BeforeDispatch = true,
            CheckpointVerified = true,
            CheckpointMatches = true,
            RouteMatches = true,
        });
        var routeDrift = MauiFlowFailureClassifier.Classify(new MauiFlowFailureFacts
        {
            LegacyFailureKind = FlowFailureKinds.NotFound,
            BeforeDispatch = true,
            CheckpointVerified = true,
            CheckpointMatches = false,
            RouteMatches = false,
        });

        Assert.Equal(MauiFlowFailureClasses.LocatorNotFound, eligible.Code);
        Assert.True(eligible.RepairEligible);
        Assert.Equal(MauiFlowFailureClasses.RouteStateDrift, routeDrift.Code);
        Assert.False(routeDrift.RepairEligible);
    }

    [Fact]
    public void WriteAtomic_WritesBoundedReportAndArtifactReference()
    {
        var report = new MauiFlowRunReport
        {
            RunId = "run-write",
            FlowDigest = new string('a', 64),
            StartedAt = DateTimeOffset.UtcNow,
            EndedAt = DateTimeOffset.UtcNow,
            Outcome = new MauiFlowRunOutcome
            {
                Status = MauiFlowRunOutcomes.Passed,
                Terminal = true,
            },
        };

        var write = MauiFlowRunReportSerializer.WriteAtomic(report, _root);

        Assert.True(write.Ok, write.Error);
        Assert.NotNull(write.Path);
        Assert.True(File.Exists(write.Path));
        Assert.NotNull(write.Digest);
        Assert.Contains(report.Artifacts, artifact => artifact.Kind == "flow-run-report");
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(write.Path!)!, "*.tmp"));
        var persisted = JsonSerializer.Deserialize(
            File.ReadAllText(write.Path!),
            MauiTestingJsonContext.Default.MauiFlowRunReport);
        Assert.NotNull(persisted);
        Assert.Equal("run-write", persisted!.RunId);
        Assert.Equal(write.Digest, persisted.ReportDigest);
    }

    [Fact]
    public void LegacyAdapter_MapsTypedLocatorFailureWithoutChangingLegacyKind()
    {
        var report = new MauiFlowRunReport
        {
            RunId = "run-adapter",
            FlowDigest = new string('b', 64),
            Outcome = new MauiFlowRunOutcome { Status = MauiFlowRunOutcomes.Failed, Terminal = true },
            DivergenceStepId = "4",
            Steps =
            [
                new MauiFlowStepAttempt
                {
                    StepId = "4",
                    Sequence = 4,
                    Action = FlowActions.Tap,
                    FailureClass = MauiFlowFailureClasses.LocatorNotFound,
                },
            ],
        };

        var legacy = FlowReplayReportAdapter.ToLegacy(report, "legacy");

        Assert.False(legacy.Ok);
        Assert.Equal(4, legacy.DivergencePoint);
        Assert.Equal(FlowFailureKinds.NotFound, legacy.Results[0].FailureKind);
        Assert.Same(report, legacy.StructuredReport);
    }

    [Fact]
    public void LegacyAdapter_StableStepId_UsesRecordedSequenceFallback()
    {
        var report = new MauiFlowRunReport
        {
            Outcome = new MauiFlowRunOutcome { Status = MauiFlowRunOutcomes.Failed, Terminal = true },
            DivergenceStepId = "save-order",
            Steps =
            [
                new MauiFlowStepAttempt
                {
                    StepId = "save-order",
                    Sequence = 4,
                    Action = FlowActions.Tap,
                    FailureClass = MauiFlowFailureClasses.LocatorNotFound,
                },
            ],
        };

        var legacy = FlowReplayReportAdapter.ToLegacy(report);

        Assert.Equal(4, legacy.DivergencePoint);
        Assert.Equal(4, Assert.Single(legacy.Results).Seq);
    }

    [Fact]
    public void LegacyAdapter_PreStepFailure_ExplainsWhyNoStepsRan()
    {
        var report = new MauiFlowRunReport
        {
            RunId = "run-admission-failure",
            FlowDigest = new string('d', 64),
            Outcome = new MauiFlowRunOutcome
            {
                Status = MauiFlowRunOutcomes.Failed,
                Summary = "The target agent is already held by another mutation lease.",
                Terminal = true,
            },
            Failure = new MauiFlowFailure
            {
                Class = MauiFlowFailureClasses.LeaseConflict,
                Message = "The target agent is already held by another mutation lease.",
            },
        };

        var legacy = FlowReplayReportAdapter.ToLegacy(report, "legacy");

        Assert.False(legacy.Ok);
        Assert.Equal(1, legacy.Total);
        Assert.Equal(1, legacy.Failed);
        var result = Assert.Single(legacy.Results);
        Assert.Equal("run", result.Action);
        Assert.Equal("Prepare run", result.Label);
        Assert.Equal(FlowFailureKinds.Drive, result.FailureKind);
        Assert.Contains("mutation lease", result.Error);
        Assert.Same(report, legacy.StructuredReport);
    }

    [Fact]
    public void SerializeToUtf8Bytes_HostileSensitiveValues_AreDescriptorsOrOmitted()
    {
        const string secret = "CorrectHorseBatteryStaple";
        var report = new MauiFlowRunReport
        {
            RunId = "run-sensitive",
            FlowDigest = new string('c', 64),
            StartedAt = DateTimeOffset.UtcNow,
            EndedAt = DateTimeOffset.UtcNow,
            Outcome = new MauiFlowRunOutcome { Status = MauiFlowRunOutcomes.Failed, Terminal = true },
            Events =
            [
                new MauiFlowRunEvent
                {
                    Kind = "failure",
                    Message = secret,
                    Data = JsonSerializer.SerializeToElement(new { secret }),
                },
            ],
            Steps =
            [
                new MauiFlowStepAttempt
                {
                    StepId = "1",
                    Selector = new FlowSelector { Text = secret },
                    Assertions =
                    [
                        new MauiFlowAssertionResult
                        {
                            Kind = "propEquals",
                            Expected = secret,
                            Actual = secret,
                        },
                    ],
                },
            ],
        };

        var json = System.Text.Encoding.UTF8.GetString(MauiFlowRunReportSerializer.SerializeToUtf8Bytes(report));

        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.Contains("redacted", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"data\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeToUtf8Bytes_StripsExtensionsAbsolutePathsAndPersonalMessages()
    {
        const string email = "person@example.test";
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signature123";
        var report = new MauiFlowRunReport
        {
            RunId = "run-redaction",
            FlowDigest = new string('a', 64),
            StartedAt = DateTimeOffset.UnixEpoch,
            EndedAt = DateTimeOffset.UnixEpoch.AddSeconds(1),
            ReportPath = @"C:\Users\person\flow-run.json",
            Outcome = new MauiFlowRunOutcome
            {
                Status = MauiFlowRunOutcomes.Failed,
                Terminal = true,
                Summary = $"Contact {email}; token={jwt}; path=C:\\Users\\person\\secret.txt",
                ExtensionData = new Dictionary<string, JsonElement>
                {
                    ["prompt"] = JsonSerializer.SerializeToElement("untrusted"),
                },
            },
            Failure = new MauiFlowFailure
            {
                Class = MauiFlowFailureClasses.AssertionFailed,
                Code = MauiFlowFailureClasses.AssertionFailed,
                Retryable = false,
            },
            Artifacts =
            [
                new MauiFlowArtifactReference
                {
                    Kind = "raw-log",
                    Path = @"C:\Users\person\raw.log",
                },
            ],
            ExtensionData = new Dictionary<string, JsonElement>
            {
                ["x-report"] = JsonSerializer.SerializeToElement(true),
            },
        };

        var json = Encoding.UTF8.GetString(MauiFlowRunReportSerializer.SerializeToUtf8Bytes(report));

        Assert.DoesNotContain(email, json, StringComparison.Ordinal);
        Assert.DoesNotContain(jwt, json, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"prompt\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"x-report\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"reportPath\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"path\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunWithLegacyAsync_DetailedEvidence_BindsEvidenceArtifactToFinalReport()
    {
        var evidence = new DetailedEvidenceCapture();
        var result = await new MauiFlowRunner(
            new FakeDriver { ElementExists = false },
            new MauiFlowRunnerOptions { PollTries = 1, PollGapMs = 0 },
            evidence).RunWithLegacyAsync(TapFlow());

        Assert.NotNull(evidence.Context);
        Assert.Equal(result.Report.RunId, evidence.Context!.Report.RunId);
        Assert.Equal(MauiFlowFailureClasses.LocatorNotFound, evidence.Context.Report.Failure!.Code);
        Assert.NotNull(evidence.Context.ReportDigest);
        Assert.Contains(result.Report.Artifacts, artifact =>
            artifact.Kind == "mauitrace" &&
            artifact.Digest == "sha256:evidence");
    }

    private static MauiFlow TapFlow() => new()
    {
        Name = "tap",
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
    };

    private sealed class FakeDriver : IMauiFlowDriver
    {
        private readonly ElementInfo _element = new()
        {
            Id = "submit-id",
            AutomationId = "submit",
            Type = "Button",
            IsVisible = true,
            IsEnabled = true,
            Bounds = new BoundsInfo { X = 1, Y = 2, Width = 100, Height = 40 },
        };

        public bool EmitReceipt { get; set; }
        public bool ElementExists { get; set; } = true;
        public Exception? TapFailure { get; set; }
        public string? PropertyValue { get; set; } = "value";
        public WorkflowCommandReceipt? LastWorkflowCommandReceipt { get; private set; }

        public Task<List<ElementInfo>> QueryAsync(string? type = null, string? automationId = null, string? text = null)
            => Task.FromResult(
                ElementExists &&
                (automationId is null || string.Equals(automationId, _element.AutomationId, StringComparison.Ordinal))
                    ? new List<ElementInfo> { _element }
                    : new List<ElementInfo>());

        public Task<ElementInfo?> GetElementAsync(string id)
            => Task.FromResult<ElementInfo?>(
                ElementExists && string.Equals(id, _element.Id, StringComparison.Ordinal) ? _element : null);

        public Task<bool> TapAsync(string elementId)
        {
            if (TapFailure is not null)
                throw TapFailure;
            if (EmitReceipt)
            {
                LastWorkflowCommandReceipt = new WorkflowCommandReceipt
                {
                    RunId = "run-report",
                    Sequence = 7,
                    CommandId = "cmd_1",
                    ActionDigest = "digest_1",
                    AuthorityEpoch = 2,
                    AcknowledgementState = "acknowledged",
                };
            }
            return Task.FromResult(true);
        }

        public Task<bool> FillAsync(string elementId, string text) => Task.FromResult(true);
        public Task<bool> SetPropertyAsync(string elementId, string propertyName, string value) => Task.FromResult(true);
        public Task<bool> ScrollAsync(string? elementId = null, double deltaX = 0, double deltaY = 0, bool animated = true, int? itemIndex = null, string? scrollToPosition = null) => Task.FromResult(true);
        public Task<bool> NavigateAsync(string route) => Task.FromResult(true);
        public Task<bool> BackAsync() => Task.FromResult(true);
        public Task<ThemeResult> SetThemeAsync(DevFlowTheme theme) => Task.FromResult(new ThemeResult { Success = true });
        public Task<string?> GetPropertyAsync(string elementId, string propertyName) => Task.FromResult(PropertyValue);

        public Task<AgentStatus?> GetStatusAsync() => Task.FromResult<AgentStatus?>(new AgentStatus
        {
            Route = "/home",
            Agent = new AgentDescriptor { InstanceId = "agent-instance" },
            Device = new DeviceDescriptor { Platform = "windows", WindowWidth = 800, WindowHeight = 600 },
            App = new AppDescriptor { PackageId = "com.example.app", Version = "1.0", Build = "42" },
        });
    }

    private sealed class DetailedEvidenceCapture : IFlowRunEvidenceCapture
    {
        public MauiFlowRunEvidenceContext? Context { get; private set; }
        public MauiFlowArtifactReference? CapturedArtifact { get; private set; }

        public Task CaptureOnFailureAsync(
            MauiFlow flow,
            FlowStep failedStep,
            FlowStepResult result,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task CaptureOnRunFailureAsync(
            MauiFlowRunEvidenceContext context,
            CancellationToken cancellationToken)
        {
            Context = context;
            CapturedArtifact = new MauiFlowArtifactReference
            {
                ArtifactId = "evidence",
                Kind = "mauitrace",
                Digest = "sha256:evidence",
                Redacted = true,
            };
            return Task.CompletedTask;
        }
    }
}
