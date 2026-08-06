using System.Text.Json;
using Microsoft.Maui.DevFlow.Driver;
using Microsoft.Maui.DevFlow.Testing;
using YamlDotNet.RepresentationModel;

namespace Microsoft.Maui.DevFlow.Tests;

public sealed class PreviewQualificationTests
{
    [Fact]
    public void CorpusRunner_ValidatesStaticCasesAndGeneratesDistinctNoRepairSamples()
    {
        var result = RunCorpus();

        Assert.True(result.Summary.ManifestValid);
        Assert.True(result.Summary.CaseSchemaValid);
        Assert.Empty(result.Summary.Errors);
        Assert.Equal(28, result.Summary.CuratedCases);
        Assert.Equal(300, result.Summary.GeneratedCases);
        Assert.Equal(0, result.Summary.DeviceBackedCases);
        Assert.True(result.Cases.All(static item => item.Passed));
        Assert.All(
            result.Samples.Where(static sample => sample.Source == MauiQualificationSampleSources.Generated),
            static sample =>
            {
                Assert.True(sample.NoRepairExpected);
                Assert.False(sample.FalseHeal);
                Assert.False(sample.RealDevice);
                Assert.NotEqual(MauiQualificationSampleSources.DeviceBacked, sample.Source);
            });
    }

    [Fact]
    public void GateEvaluator_StaticCorpusWithoutDeviceEvidence_IsNotQualified()
    {
        var corpus = RunCorpus();
        var report = MauiPreviewQualificationGateEvaluator.Evaluate(new MauiPreviewQualificationInput
        {
            Platform = "android",
            Corpus = corpus.Summary,
            Samples = corpus.Samples,
            PrivacySecurity = corpus.PrivacySecurity,
            RuntimeOverhead = HostOverhead(),
            Evidence = new MauiQualificationRequiredEvidence
            {
                CorpusManifestValid = true,
                CaseSchemaValid = true,
            },
        }, DateTimeOffset.UnixEpoch);

        Assert.Equal(MauiPreviewQualificationStates.NotQualified, report.Status);
        Assert.Equal(316, report.Metrics.FalseHeals.Denominator);
        Assert.Equal(0, report.Metrics.FalseHeals.Numerator);
        Assert.False(report.Metrics.FalseHeals.IndependentDeviceRuns);
        Assert.Equal(1, report.Metrics.RepairPrecision.Denominator);
        Assert.Equal(1, report.Metrics.RepairRecall.Denominator);
        Assert.Equal(1, report.Metrics.RepairRecall.Numerator);
        Assert.Contains(
            report.Gates,
            gate => gate.GateId == "android-tier1-first-attempts" &&
                gate.Status == MauiPreviewQualificationStates.NotQualified &&
                gate.ReasonCodes.Contains("tier1-flow-declaration-missing"));
        Assert.Contains(
            report.Gates,
            gate => gate.GateId == "android-device-overhead" &&
                gate.Status == MauiPreviewQualificationStates.NotQualified);
        Assert.True(MauiPreviewQualificationReportValidator.Validate(report).IsValid);
    }

    [Fact]
    public void QualificationReport_UsesVersionedSchemaEnvelopeAndSafeRequiredFields()
    {
        var report = MauiPreviewQualificationGateEvaluator.Evaluate(QualifiedInput(), DateTimeOffset.UnixEpoch);
        var json = JsonSerializer.Serialize(report, MauiTestingJsonContext.Default.MauiPreviewQualificationReport);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var schemaPath = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "DevFlow",
            "spec",
            "schemas",
            "maui-preview-qualification-v1.json");
        using var schema = JsonDocument.Parse(File.ReadAllText(schemaPath));

        Assert.Equal(1, root.GetProperty("schema").GetInt32());
        Assert.Equal("maui-preview-qualification", root.GetProperty("kind").GetString());
        Assert.Equal("preview-qualification-v1", root.GetProperty("contractVersion").GetString());
        foreach (var required in schema.RootElement.GetProperty("required").EnumerateArray())
            Assert.True(root.TryGetProperty(required.GetString()!, out _), $"Missing {required.GetString()}.");
        Assert.False(root.GetProperty("featureFlags").GetProperty("autoApplyRepair").GetBoolean());
        Assert.False(root.GetProperty("featureFlags").GetProperty("autoApplySource").GetBoolean());
        Assert.True(MauiPreviewQualificationReportValidator.Validate(report).IsValid);
    }

    [Fact]
    public void GateEvaluator_ThresholdBoundariesAndWilsonLowerBound_AreAppliedConservatively()
    {
        var report = MauiPreviewQualificationGateEvaluator.Evaluate(QualifiedInput());

        Assert.Equal(MauiPreviewQualificationStates.Pass, report.Status);
        var repair = report.Metrics.RepairPrecision;
        Assert.Equal(100, repair.Denominator);
        Assert.Equal(100, repair.Numerator);
        Assert.NotNull(repair.ConfidenceInterval);
        Assert.True(repair.ConfidenceInterval!.Lower >= 0.95);

        var oneIncorrect = QualifiedInput();
        oneIncorrect.Samples.First(static sample => sample.RepairProposed == true).RepairCorrect = false;
        var failed = MauiPreviewQualificationGateEvaluator.Evaluate(oneIncorrect);
        Assert.Equal(MauiPreviewQualificationStates.Fail, failed.Status);
        Assert.Equal(
            MauiPreviewQualificationStates.Fail,
            Gate(failed, "repair-precision").Status);
    }

    [Fact]
    public void GateEvaluator_ZeroFalseHealRuleFailsAtSingleEscape()
    {
        var input = QualifiedInput();
        var noRepair = input.Samples.First(static sample => sample.NoRepairExpected == true);
        noRepair.FalseHeal = true;
        noRepair.Abstained = false;

        var report = MauiPreviewQualificationGateEvaluator.Evaluate(input);

        Assert.Equal(MauiPreviewQualificationStates.Fail, Gate(report, "zero-false-heals").Status);
        Assert.Equal(1, report.Metrics.FalseHeals.Numerator);
        Assert.Equal(300, report.Metrics.FalseHeals.Denominator);
    }

    [Fact]
    public void GateEvaluator_CalibrationRequiresEceBeforeProbabilityLikeConfidence()
    {
        var input = QualifiedInput();
        for (var index = 0; index < 100; index++)
        {
            input.Samples.Add(new MauiQualificationExecutionSample
            {
                SampleId = $"calibration-{index}",
                Source = MauiQualificationSampleSources.Curated,
                ProbabilityLikeConfidence = 0.9,
                ExpectedOutcome = true,
            });
        }

        var report = MauiPreviewQualificationGateEvaluator.Evaluate(input);

        Assert.True(report.Metrics.Calibration.ProbabilityLikeConfidenceDisplayed);
        Assert.Equal(100, report.Metrics.Calibration.SampleCount);
        Assert.True(report.Metrics.Calibration.Ece > 0.05);
        Assert.Equal(MauiPreviewQualificationStates.Fail, Gate(report, "confidence-calibration").Status);
    }

    [Fact]
    public void GateEvaluator_GeneratedSamplesNeverCountAsRealDeviceFirstAttempts()
    {
        var input = QualifiedInput();
        input.Samples.RemoveAll(static sample => sample.Source == MauiQualificationSampleSources.DeviceBacked);
        for (var index = 0; index < 100; index++)
        {
            input.Samples.Add(new MauiQualificationExecutionSample
            {
                SampleId = $"generated-first-attempt-{index}",
                Source = MauiQualificationSampleSources.Generated,
                Platform = "android",
                FlowId = "tier-one",
                Tier = "tier-1",
                CleanState = true,
                FirstAttempt = true,
                Outcome = MauiFlowRunOutcomes.Passed,
                SelectorStable = true,
            });
        }

        var report = MauiPreviewQualificationGateEvaluator.Evaluate(input);

        Assert.Equal(MauiPreviewQualificationStates.NotQualified, report.Status);
        Assert.Equal(0, Assert.Single(report.Metrics.FlakeFirstAttemptStability.Flows).CleanFirstAttempts);
        Assert.False(Assert.Single(report.Metrics.FlakeFirstAttemptStability.Flows).RealDeviceEvidence);
        Assert.False(report.Metrics.FalseHeals.IndependentDeviceRuns);
    }

    [Fact]
    public void GateEvaluator_FirstAttemptAccounting_IgnoresDiagnosticsAndRequiresInfrastructureReason()
    {
        var input = QualifiedInput();
        input.Samples.RemoveAll(static sample => sample.Source == MauiQualificationSampleSources.DeviceBacked);
        input.Samples.Add(new MauiQualificationExecutionSample
        {
            SampleId = "diagnostic",
            Source = MauiQualificationSampleSources.DeviceBacked,
            Platform = "android",
            FlowId = "tier-one",
            DeviceEvidenceKind = "physical-device",
            RealDevice = true,
            CleanState = true,
            FirstAttempt = false,
            DiagnosticRerun = true,
            Outcome = MauiFlowRunOutcomes.Passed,
        });
        input.Samples.Add(new MauiQualificationExecutionSample
        {
            SampleId = "unexplained-infrastructure",
            Source = MauiQualificationSampleSources.DeviceBacked,
            Platform = "android",
            FlowId = "tier-one",
            DeviceEvidenceKind = "physical-device",
            RealDevice = true,
            CleanState = true,
            FirstAttempt = true,
            Outcome = MauiFlowRunOutcomes.InfrastructureError,
        });
        input.Samples.Add(new MauiQualificationExecutionSample
        {
            SampleId = "explained-infrastructure",
            Source = MauiQualificationSampleSources.DeviceBacked,
            Platform = "android",
            FlowId = "tier-one",
            DeviceEvidenceKind = "physical-device",
            RealDevice = true,
            CleanState = true,
            FirstAttempt = true,
            Outcome = MauiFlowRunOutcomes.InfrastructureError,
            InfrastructureExclusionReason = "recorded-host-startup-timeout",
        });

        var report = MauiPreviewQualificationGateEvaluator.Evaluate(input);
        var metric = report.Metrics.FlakeFirstAttemptStability;

        Assert.Equal(1, metric.DiagnosticRerunsIgnored);
        Assert.Equal(1, metric.Stability.Denominator);
        Assert.Equal(0, metric.Stability.Numerator);
        Assert.Single(metric.InfrastructureExclusions);
    }

    [Fact]
    public void SecurityCorpusAndCanaryScanner_NeverCopyCanaryToQualificationProjection()
    {
        var corpus = MauiQualificationSecurityCorpusRunner.Run(Path.Combine(FindRepositoryRoot(), "tests", "DevFlow", "InspectorCorpus"));
        const string canary = "DEVFLOW_TEST_CANARY";
        var redacted = MauiQualificationPrivacyScanner.RedactUntrusted(canary);
        var scan = MauiQualificationPrivacyScanner.Scan(
            [new KeyValuePair<string, string?>("report", redacted)],
            [canary]);

        Assert.True(corpus.Summary.Valid);
        Assert.Equal(corpus.Summary.CaseCount, corpus.Summary.PassedCount);
        Assert.True(scan.Passed);
        Assert.DoesNotContain(canary, redacted, StringComparison.Ordinal);

        var escaped = MauiQualificationPrivacyScanner.Scan(
            [new KeyValuePair<string, string?>("report", canary)],
            [canary]);
        Assert.False(escaped.Passed);
        Assert.Equal("report", Assert.Single(escaped.Escapes).Field);
    }

    [Fact]
    public void QualificationReport_RedactsCanariesFromProfilesFlagsArtifactsAndExternalCorpusErrors()
    {
        const string canary = "DEVFLOW_CANARY_DO_NOT_PROJECT";
        var input = QualifiedInput();
        input.Platform = canary;
        input.Profiles.Add(new MauiQualificationPlatformProfile
        {
            Platform = canary,
            Scope = canary,
            DeviceEvidenceKind = canary,
            DeviceFingerprint = canary,
        });
        input.FeatureFlags = new MauiPreviewFeatureFlags
        {
            KillSwitches = [canary],
        };
        input.Corpus!.Errors.Add(canary);
        input.ArtifactRefs.Add(new MauiQualificationArtifactReference
        {
            Kind = canary,
            Digest = canary,
            Reference = canary,
        });
        input.Thresholds = new MauiQualificationGateThresholds
        {
            PolicyVersion = canary,
            ExtensionData = new Dictionary<string, JsonElement>
            {
                [canary] = JsonSerializer.SerializeToElement(canary),
            },
        };
        input.RuntimeOverhead = new MauiQualificationRuntimeOverheadMetric
        {
            HostOperations =
            [
                new MauiQualificationDurationMetric
                {
                    State = "missing",
                    Operation = canary,
                    MissingReason = canary,
                },
            ],
            DeviceOverhead = new MauiQualificationDurationMetric
            {
                State = "missing",
                Operation = canary,
                MissingReason = canary,
            },
        };

        var report = MauiPreviewQualificationGateEvaluator.Evaluate(input);
        var json = JsonSerializer.Serialize(report, MauiTestingJsonContext.Default.MauiPreviewQualificationReport);

        Assert.DoesNotContain(canary, json, StringComparison.Ordinal);
        Assert.Equal("unknown", report.Platform);
        Assert.Equal("unknown", report.Profiles.Last().Platform);
        Assert.Empty(report.FeatureFlags.KillSwitches);
        Assert.Equal("unknown", Assert.Single(report.ArtifactRefs).Kind);
    }

    [Fact]
    public void ArtifactManifestReader_EmulatorEvidenceDoesNotBecomeRealDeviceEvidence()
    {
        var manifest = """
            {
              "schema": 1,
              "kind": "devflow-flow-pilot",
              "repository": { "commit": "commit" },
              "testing": { "packageVersion": "testing" },
              "platform": { "name": "android", "androidSdk": { "apiLevel": "35", "avdName": "preview-avd" } },
              "app": { "packageId": "example", "packageDigest": "package" },
              "validationErrors": [],
              "artifacts": [
                { "kind": "flow-run-report", "sha256": "sha256:artifact", "sizeBytes": 1 }
              ],
              "flows": [
                {
                  "digest": "sha256:flow",
                  "tier": "tier-1",
                  "firstAttempt": { "runKind": "clean", "outcome": "passed", "reportDigest": "sha256:report" }
                }
              ]
            }
            """;

        var parsed = MauiPreviewQualificationArtifactManifestReader.ParseJson(manifest);
        var sample = Assert.Single(parsed.Input.Samples);

        Assert.True(parsed.Ok);
        Assert.Equal("emulator", sample.DeviceEvidenceKind);
        Assert.False(sample.RealDevice);
        Assert.Contains("sha256:flow", parsed.Input.Tier1Flows);
        Assert.Null(sample.ReportSchemaValid);
    }

    [Fact]
    public void ArtifactManifestReader_FixtureInitializationDiagnostic_BecomesRedactedQualificationExclusion()
    {
        var manifest = """
            {
              "schema": 1,
              "kind": "devflow-flow-pilot",
              "repository": { "commit": "commit" },
              "testing": { "packageVersion": "testing" },
              "platform": { "name": "android", "androidSdk": { "apiLevel": "35", "avdName": "preview-avd" } },
              "app": { "packageId": "example", "packageDigest": "package" },
              "validationErrors": [],
              "artifacts": [
                {
                  "kind": "fixture-initialization-diagnostic",
                  "path": "artifacts/devflow/run/android/host-diagnostics/fixture-initialization.json",
                  "sha256": "sha256:fixture-diagnostic",
                  "sizeBytes": 123,
                  "redacted": true
                }
              ],
              "omissions": [
                {
                  "kind": "fixture-initialization-diagnostic",
                  "reason": "A bounded redacted Android fixture initialization diagnostic was captured."
                }
              ],
              "flows": [
                {
                  "digest": "sha256:flow",
                  "tier": "tier-1",
                  "firstAttempt": {
                    "runKind": "clean",
                    "outcome": "infrastructure-error"
                  }
                }
              ]
            }
            """;

        var parsed = MauiPreviewQualificationArtifactManifestReader.ParseJson(manifest);
        var sample = Assert.Single(parsed.Input.Samples);
        var report = MauiPreviewQualificationGateEvaluator.Evaluate(parsed.Input);

        Assert.True(parsed.Ok, string.Join("; ", parsed.Errors));
        Assert.Equal("recorded-fixture-initialization-diagnostic", sample.InfrastructureExclusionReason);
        Assert.Contains(
            parsed.Input.ArtifactRefs,
            reference => reference.Kind == "fixture-initialization-diagnostic" && reference.Redacted == true);
        Assert.Contains(
            report.Exclusions,
            exclusion => exclusion.Kind == "android-fixture-initialization" && exclusion.Count == 1);
    }

    [Fact]
    public void HostPerformanceBudget_FailsOnlyMeasuredHostRegression()
    {
        var input = QualifiedInput();
        input.RuntimeOverhead!.HostOperations[0].P95Ms = 251;

        var report = MauiPreviewQualificationGateEvaluator.Evaluate(input);

        Assert.Equal(MauiPreviewQualificationStates.Fail, Gate(report, "deterministic-host-performance").Status);
    }

    [Fact]
    public void PreviewFeatureFlagConfiguration_EnablesOnlyProposalSurfacesAndHonorsKillSwitches()
    {
        var flags = MauiPreviewFeatureFlagConfiguration.FromEnvironment(name => name switch
        {
            "DEVFLOW_PREVIEW_WORKBENCH" => "true",
            "DEVFLOW_PREVIEW_REPAIR_PROPOSALS" => "1",
            "DEVFLOW_PREVIEW_KILL_SWITCHES" => "repair-proposals",
            "DEVFLOW_PREVIEW_AUTO_APPLY_REPAIR" => "true",
            _ => null,
        });

        Assert.True(flags.IsEnabled("workbench"));
        Assert.False(flags.IsEnabled("repair-proposals"));
        Assert.False(flags.AutoApplyRepair);
        Assert.False(flags.AutoApplySource);
        Assert.False(flags.ModelProviderEnabled);
        Assert.False(flags.TelemetryEgressEnabled);
        Assert.False(flags.RequiredPullRequestGate);
    }

    [Fact]
    public void Fuzzing_IsSeedDeterministicAndCoversParsingSelectorsHistoryImportsAndTransitions()
    {
        var options = new MauiQualificationFuzzOptions
        {
            Seed = 401,
            Iterations = 48,
            MaximumDuration = TimeSpan.FromSeconds(2),
        };
        var first = MauiQualificationFuzz.Run(options, FuzzOperation);
        var second = MauiQualificationFuzz.Run(options, FuzzOperation);

        Assert.Equal(options.Seed, first.Seed);
        Assert.Equal(options.Iterations, first.Executed);
        Assert.Equal(first.OperationsDigest, second.OperationsDigest);
        Assert.True(first.Elapsed <= options.MaximumDuration);
    }

    [Fact]
    public void QualificationWorkflow_IsValidYamlAndKeepsStaticGateAdvisory()
    {
        var path = Path.Combine(FindRepositoryRoot(), ".github", "workflows", "devflow-integration.yml");
        var text = File.ReadAllText(path);
        var yaml = new YamlStream();
        using (var reader = new StringReader(text))
            yaml.Load(reader);

        Assert.NotNull(Assert.Single(yaml.Documents).RootNode);
        Assert.Contains("static-qualification", text, StringComparison.Ordinal);
        Assert.Contains("devflow flow qualify", text, StringComparison.Ordinal);
        Assert.Contains("qualification", text, StringComparison.Ordinal);
        Assert.DoesNotContain("required: true", text, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FuzzOperation(Random random, int iteration)
    {
        var id = $"save-{random.Next(0, 10_000)}";
        var flow = new MauiFlow
        {
            Name = "fuzz",
            Steps =
            [
                new FlowStep
                {
                    Seq = 1,
                    Action = FlowActions.Tap,
                    Target = new FlowSelector { AutomationId = id },
                    Asserts = [new FlowAssert { Kind = "exists", Selector = new FlowSelector { AutomationId = id }, Verify = true }],
                },
            ],
        };
        var markdown = FlowMarkdown.Serialize(flow);
        var parsed = FlowMarkdown.Parse(markdown);
        _ = MauiTestPlanValidator.ValidateJson(
            """{"schema":1,"planId":"fuzz","revision":1,"flow":{"digest":"sha256:flow"},"goal":"goal","scenarios":[],"preconditions":[],"reset":{"required":true},"acceptanceCriteria":[],"sideEffectPolicy":"none","provenance":{"actorKind":"human","channel":"test"}}""",
            out _);
        var reportJson = JsonSerializer.Serialize(
            new MauiFlowRunReport
            {
                RunId = $"run-{iteration}",
                FlowDigest = "sha256:flow",
                Outcome = new MauiFlowRunOutcome { Status = MauiFlowRunOutcomes.Passed, Terminal = true },
                EndedAt = DateTimeOffset.UnixEpoch,
            },
            MauiTestingJsonContext.Default.MauiFlowRunReport);
        _ = JsonSerializer.Deserialize(reportJson, MauiTestingJsonContext.Default.MauiFlowRunReport);
        var proposalJson = JsonSerializer.Serialize(
            new MauiFlowRepairProposal { ProposalId = $"proposal-{iteration}", State = "proposed" },
            MauiTestingJsonContext.Default.MauiFlowRepairProposal);
        _ = JsonSerializer.Deserialize(proposalJson, MauiTestingJsonContext.Default.MauiFlowRepairProposal);
        var trustJson = JsonSerializer.Serialize(
            new MauiArtifactTrustRecord
            {
                Identity = MauiImportedArtifactIdentity.Create(),
                Verification = new MauiArtifactTrustVerificationResult { State = MauiArtifactTrustStates.Untrusted },
            },
            MauiTestingJsonContext.Default.MauiArtifactTrustRecord);
        _ = JsonSerializer.Deserialize(trustJson, MauiTestingJsonContext.Default.MauiArtifactTrustRecord);
        _ = MauiPreviewQualificationArtifactManifestReader.ParseJson(iteration % 2 == 0 ? "{}" : "[]");

        var target = new MauiSelectorObservationElement
        {
            Id = id,
            Type = "Button",
            Role = "button",
            Traits = ["interactive"],
            AutomationId = id,
            IsVisible = true,
            IsEnabled = true,
        };
        _ = MauiSelectorCandidateGenerator.Generate(new MauiSelectorObservation
        {
            Target = target,
            Elements = [target],
            Context = new MauiSelectorObservationContext { Platform = "android" },
        });

        var firstPayload = "sha256:first";
        var firstDigest = MauiQualificationHashChain.CreateDigest(null, firstPayload);
        var secondPayload = "sha256:second";
        var secondDigest = MauiQualificationHashChain.CreateDigest(firstDigest, secondPayload);
        if (!MauiQualificationHashChain.IsValid(
            [
                new MauiQualificationHashChainEntry { PayloadDigest = firstPayload, Digest = firstDigest },
                new MauiQualificationHashChainEntry { PreviousDigest = firstDigest, PayloadDigest = secondPayload, Digest = secondDigest },
            ]))
        {
            throw new InvalidOperationException("Hash-chain property failed.");
        }

        var transition = MauiQualificationProposalTransitionPolicy.Evaluate(new MauiQualificationProposalTransition
        {
            State = "approved",
            HumanApprovalRecorded = random.Next(2) == 0,
            GrantValid = random.Next(2) == 0,
            ApplyRequested = random.Next(2) == 0,
        });
        if (transition.AutomaticApplyAllowed)
            throw new InvalidOperationException("Automatic apply must remain prohibited.");
        return parsed.Ok ? transition.ReasonCode : "parse-failed";
    }

    private static MauiPreviewQualificationCorpusRunResult RunCorpus() =>
        MauiPreviewQualificationCorpusRunner.Run(new MauiPreviewQualificationCorpusRunRequest
        {
            CorpusRoot = Path.Combine(FindRepositoryRoot(), "tests", "DevFlow", "InspectorCorpus"),
            Platform = "android",
            MutationSeed = 20260802,
            GeneratedNoRepairEvaluations = 300,
        });

    private static MauiPreviewQualificationInput QualifiedInput()
    {
        var input = new MauiPreviewQualificationInput
        {
            Platform = "android",
            Corpus = new MauiQualificationCorpusSummary
            {
                Version = "test",
                ManifestFingerprint = "sha256:corpus",
                StaticOnly = true,
                ManifestValid = true,
                CaseSchemaValid = true,
                CuratedCases = 100,
                GeneratedCases = 300,
                SecurityCorpus = new MauiQualificationSecurityCorpusSummary
                {
                    Valid = true,
                    CaseCount = 18,
                    PassedCount = 18,
                },
            },
            Evidence = new MauiQualificationRequiredEvidence
            {
                CorpusManifestValid = true,
                CaseSchemaValid = true,
                ReportSchemaValid = true,
                RecordingValid = true,
                FirstAttemptEvidencePresent = true,
                ArtifactManifestValid = true,
                ArtifactReferencesComplete = true,
            },
            Review = new MauiQualificationReviewEvidence
            {
                PlanId = "plan",
                PlanReviewStatus = "approved",
                RubberDuckReviewStatus = "approved",
                IndependentReviewStatus = "approved",
            },
            PrivacySecurity = new MauiQualificationPrivacySecurityMetric
            {
                State = "measured",
                TestCount = 18,
                EscapeCount = 0,
                CanaryScanPassed = true,
            },
            RuntimeOverhead = HostOverhead(deviceMeasured: true),
            Tier1Flows = ["tier-one"],
        };

        for (var index = 0; index < 100; index++)
        {
            input.Samples.Add(new MauiQualificationExecutionSample
            {
                SampleId = $"repair-{index}",
                Source = MauiQualificationSampleSources.Curated,
                Platform = "android",
                RepairProposed = true,
                RepairExpected = true,
                RepairCorrect = true,
            });
        }
        for (var index = 0; index < 300; index++)
        {
            input.Samples.Add(new MauiQualificationExecutionSample
            {
                SampleId = $"no-repair-{index}",
                Source = MauiQualificationSampleSources.Generated,
                Platform = "android",
                NoRepairExpected = true,
                FalseHeal = false,
                Abstained = true,
            });
        }
        for (var index = 0; index < 100; index++)
        {
            input.Samples.Add(new MauiQualificationExecutionSample
            {
                SampleId = $"device-{index}",
                Source = MauiQualificationSampleSources.DeviceBacked,
                Platform = "android",
                FlowId = "tier-one",
                Tier = "tier-1",
                DeviceEvidenceKind = "physical-device",
                RealDevice = true,
                CleanState = true,
                FirstAttempt = true,
                Outcome = MauiFlowRunOutcomes.Passed,
                RecordingValid = true,
                ReportPresent = true,
                ReportSchemaValid = true,
                ReportComplete = true,
                SelectorStable = true,
                TimeToDiagnosisMs = 1,
                TraceBytes = 10,
                ReportBytes = 20,
            });
        }
        return input;
    }

    private static MauiQualificationRuntimeOverheadMetric HostOverhead(bool deviceMeasured = false) => new()
    {
        HostOperations =
        [
            new MauiQualificationDurationMetric
            {
                State = "measured",
                Operation = "parse",
                SampleCount = 10,
                P50Ms = 1,
                P95Ms = 1,
                MaxMs = 1,
            },
        ],
        DeviceOverhead = new MauiQualificationDurationMetric
        {
            State = deviceMeasured ? "measured" : "missing",
            Operation = "android-device-overhead",
            SampleCount = deviceMeasured ? 10 : 0,
            P50Ms = deviceMeasured ? 1 : null,
            P95Ms = deviceMeasured ? 1 : null,
            MaxMs = deviceMeasured ? 1 : null,
            MissingReason = deviceMeasured ? null : "missing",
        },
    };

    private static MauiQualificationGateResult Gate(MauiPreviewQualificationReport report, string id) =>
        Assert.Single(report.Gates, gate => gate.GateId == id);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
