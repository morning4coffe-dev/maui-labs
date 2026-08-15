using System.Text.Json;
using System.Text.Json.Nodes;
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
        Assert.Equal(58, result.Summary.CuratedCases);
        Assert.Equal(300, result.Summary.GeneratedCases);
        Assert.Equal(0, result.Summary.DeviceBackedCases);
        Assert.Equal(31, result.Summary.CuratedRepairPositiveCases);
        Assert.Equal(16, result.Summary.CuratedNoRepairCases);
        Assert.Equal(300, result.Summary.GeneratedNoRepairCases);
        Assert.True(result.Summary.ProvenanceComplete);
        Assert.Equal(
            result.Summary.CuratedCases,
            result.Summary.ProvenanceSourceCounts.Sum(static item => item.Count));
        Assert.All(
            result.Summary.ProvenanceSourceCounts,
            static item => Assert.NotEqual(
                MauiQualificationCorpusProvenanceSourceKinds.Unknown,
                item.SourceKind));
        Assert.DoesNotContain(
            result.Summary.ProvenanceSourceCounts,
            static item => MauiQualificationCorpusProvenanceSourceKinds.IsObserved(item.SourceKind));
        Assert.InRange(result.Summary.CuratedClassificationLabeledCases, 1, result.Summary.CuratedCases);
        Assert.True(result.Cases.All(static item => item.Passed));
        Assert.All(
            result.Samples.Where(static sample => sample.Source == MauiQualificationSampleSources.Generated),
            static sample =>
            {
                Assert.True(sample.NoRepairExpected);
                Assert.False(sample.FalseHeal);
                Assert.False(sample.RealDevice);
                Assert.NotEqual(MauiQualificationSampleSources.DeviceBacked, sample.Source);
                Assert.Equal("generated-no-repair-production-pipeline", sample.Category);
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

        // The pooled 0/316 must never read as 316 independent trials: 16 curated seeds and
        // 300 machine-generated mutants of those seeds are reported separately.
        var curatedFalseHeals = report.Metrics.FalseHeals.SourceCounts
            .Single(static item => item.Source == MauiQualificationSampleSources.Curated);
        var generatedFalseHeals = report.Metrics.FalseHeals.SourceCounts
            .Single(static item => item.Source == MauiQualificationSampleSources.Generated);
        Assert.Equal(16, curatedFalseHeals.Denominator);
        Assert.Equal(300, generatedFalseHeals.Denominator);
        // Only the 16 curated seeds are independent. The 300 mutants are derived from them and
        // must never satisfy the gate's minimum on their own.
        Assert.Equal(16, report.Metrics.FalseHeals.IndependentEvaluations);
        Assert.DoesNotContain(
            report.Metrics.FalseHeals.SourceCounts,
            static item => item.Source == MauiQualificationSampleSources.DeviceBacked);
        Assert.Equal(16, report.Corpus.CuratedNoRepairCases);
        Assert.Equal(300, report.Corpus.GeneratedNoRepairCases);
        Assert.Equal(31, report.Corpus.CuratedRepairPositiveCases);
        Assert.True(report.Corpus.ProvenanceComplete);

        Assert.Equal(31, report.Metrics.RepairPrecision.Denominator);
        // 30 of the 31 repair-positives are adapted from one seed. The reported 31/31 therefore
        // rests on a single independent trial, and the gate says so.
        Assert.Equal(1, report.Metrics.RepairPrecision.IndependentEvaluations);
        Assert.Equal(
            30,
            report.Metrics.RepairPrecision.SourceCounts
                .Single(static item => item.Source == MauiQualificationSampleSources.CuratedDerived)
                .Denominator);
        Assert.Equal(31, report.Metrics.RepairRecall.Denominator);
        Assert.Equal(31, report.Metrics.RepairRecall.Numerator);

        // Classification accuracy is now a recorded number rather than an unmeasured claim,
        // but it is still under the minimum evaluation count so the gate stays not-qualified.
        var classification = report.Metrics.ClassificationAccuracy;
        Assert.Equal(45, classification.Denominator);
        Assert.Equal(45, report.Corpus.CuratedClassificationLabeledCases);
        Assert.Equal(42, classification.Numerator);
        Assert.False(classification.IndependentDeviceRuns);
        Assert.Equal("measured", report.Metrics.ClassificationMatrix.State);
        Assert.Equal(45, report.Metrics.ClassificationMatrix.SampleCount);
        Assert.Equal(42, report.Metrics.ClassificationMatrix.Correct);
        // The headline 42/45 is mostly the classifier copying a class the fixture already stamped.
        // Only 8 cases forced genuine inference, and the gate minimum counts only those.
        Assert.Equal(8, report.Metrics.ClassificationMatrix.InferredSampleCount);
        Assert.Equal(8, report.Metrics.ClassificationMatrix.InferredCorrect);
        Assert.Equal(37, report.Metrics.ClassificationMatrix.StampHonouredSampleCount);
        Assert.Equal(34, report.Metrics.ClassificationMatrix.StampHonouredCorrect);
        Assert.Equal(8, classification.IndependentEvaluations);
        Assert.Equal(
            report.Metrics.ClassificationMatrix.SampleCount,
            report.Metrics.ClassificationMatrix.InferredSampleCount +
                report.Metrics.ClassificationMatrix.StampHonouredSampleCount);
        Assert.Equal(
            report.Metrics.ClassificationMatrix.SampleCount,
            report.Metrics.ClassificationMatrix.Cells.Sum(static cell => cell.Count));
        Assert.Contains(
            report.Metrics.ClassificationMatrix.Cells,
            static cell => cell.Expected != cell.Observed && cell.Count > 0);
        Assert.Contains(
            report.Gates,
            gate => gate.GateId == "classification-accuracy" &&
                gate.Status == MauiPreviewQualificationStates.NotQualified &&
                gate.ReasonCodes.Contains("classification-evaluation-count-insufficient"));
        Assert.Contains(
            report.Gates,
            gate => gate.GateId == "repair-precision" &&
                gate.Status == MauiPreviewQualificationStates.NotQualified &&
                gate.ReasonCodes.Contains("repair-evaluation-count-insufficient"));
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
        // The gate counts independent evaluations, so a denominator inflated with restated cases
        // could not carry it.
        Assert.Equal(100, repair.IndependentEvaluations);
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
        Assert.Equal(600, report.Metrics.FalseHeals.Denominator);
        // Generated mutants are reported but never satisfy the minimum; only the 300 device-backed
        // trials do.
        Assert.Equal(300, report.Metrics.FalseHeals.IndependentEvaluations);
        Assert.Equal(
            300,
            report.Metrics.FalseHeals.SourceCounts
                .Single(static item => item.Source == MauiQualificationSampleSources.Generated)
                .Denominator);
    }

    [Fact]
    public void GateEvaluator_ClassificationAccuracyRequiresLabelledEvidenceAndConservativeLowerBound()
    {
        var qualified = MauiPreviewQualificationGateEvaluator.Evaluate(QualifiedInput());
        Assert.Equal(MauiPreviewQualificationStates.Pass, Gate(qualified, "classification-accuracy").Status);
        Assert.Equal(100, qualified.Metrics.ClassificationAccuracy.Denominator);
        Assert.Equal(100, qualified.Metrics.ClassificationAccuracy.Numerator);
        // Only genuinely inferred cases count toward the minimum. A corpus that stamps its own
        // failure class would answer by copying and could never carry this gate.
        Assert.Equal(100, qualified.Metrics.ClassificationAccuracy.IndependentEvaluations);
        Assert.Equal(100, qualified.Metrics.ClassificationMatrix.InferredSampleCount);
        Assert.Equal(0, qualified.Metrics.ClassificationMatrix.StampHonouredSampleCount);
        Assert.Equal(1, qualified.Metrics.ClassificationMatrix.LabelCount);

        var unlabelled = QualifiedInput();
        foreach (var sample in unlabelled.Samples)
        {
            sample.ExpectedFailureClass = null;
            sample.ObservedFailureClass = null;
        }
        var missing = MauiPreviewQualificationGateEvaluator.Evaluate(unlabelled);
        Assert.Equal(MauiPreviewQualificationStates.NotQualified, Gate(missing, "classification-accuracy").Status);
        Assert.Contains(
            "classification-evaluation-count-insufficient",
            Gate(missing, "classification-accuracy").ReasonCodes);
        Assert.Equal(0, missing.Metrics.ClassificationAccuracy.Denominator);
        Assert.Equal("missing", missing.Metrics.ClassificationMatrix.State);
        Assert.NotEqual(MauiPreviewQualificationStates.Pass, missing.Status);

        var misclassified = QualifiedInput();
        foreach (var sample in misclassified.Samples.Where(static item => item.ExpectedFailureClass is not null).Take(20))
            sample.ObservedFailureClass = MauiFlowFailureClasses.RouteStateDrift;
        var failed = MauiPreviewQualificationGateEvaluator.Evaluate(misclassified);
        Assert.Equal(MauiPreviewQualificationStates.Fail, Gate(failed, "classification-accuracy").Status);
        Assert.Contains(
            "classification-accuracy-lower-bound-below-threshold",
            Gate(failed, "classification-accuracy").ReasonCodes);
        Assert.Equal(80, failed.Metrics.ClassificationAccuracy.Numerator);
        Assert.Equal(2, failed.Metrics.ClassificationMatrix.LabelCount);
        var drift = failed.Metrics.ClassificationMatrix.PerClass
            .Single(static item => item.FailureClass == MauiFlowFailureClasses.RouteStateDrift);
        Assert.Equal(0, drift.Support);
        Assert.Equal(20, drift.Predicted);
        Assert.Equal(0d, drift.Precision);
        Assert.Null(drift.Recall);
    }

    [Fact]
    public void GateEvaluator_ClassificationLabelsOutsideTheClosedSetAreNormalizedAwayFromFreeText()
    {
        var input = QualifiedInput();
        foreach (var sample in input.Samples.Where(static item => item.ExpectedFailureClass is not null).Take(5))
        {
            sample.ExpectedFailureClass = "user-profile-page-locator";
            sample.ObservedFailureClass = "user-profile-page-locator";
        }

        var report = MauiPreviewQualificationGateEvaluator.Evaluate(input);

        // Unknown labels must never reach the report verbatim; they collapse to "unknown"
        // and are excluded from the accuracy denominator so they cannot inflate the rate.
        Assert.Equal(95, report.Metrics.ClassificationAccuracy.Denominator);
        Assert.DoesNotContain(
            report.Metrics.ClassificationMatrix.Cells,
            static cell => cell.Expected.Contains("profile", StringComparison.OrdinalIgnoreCase) ||
                cell.Observed.Contains("profile", StringComparison.OrdinalIgnoreCase));
        Assert.True(MauiPreviewQualificationReportValidator.Validate(report).IsValid);
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

    [Fact]
    public void Accumulator_MergesIndependentRunsButRefusesDuplicateOrIncompatibleEvidence()
    {
        var first = MauiPreviewQualificationGateEvaluator.Evaluate(QualifiedInput(), DateTimeOffset.UnixEpoch);

        // The same evidence submitted twice is one observation. Wall-clock time is excluded from
        // the run fingerprint precisely so a repeated run cannot inflate a denominator.
        var duplicate = MauiPreviewQualificationGateEvaluator.Evaluate(QualifiedInput(), DateTimeOffset.UnixEpoch.AddDays(1));
        Assert.Equal(
            MauiPreviewQualificationAccumulator.ComputeEvidenceFingerprint(first),
            MauiPreviewQualificationAccumulator.ComputeEvidenceFingerprint(duplicate));

        var deduplicated = MauiPreviewQualificationAccumulator.Accumulate([first, duplicate], DateTimeOffset.UnixEpoch);
        Assert.Equal(2, deduplicated.ConsideredRuns);
        Assert.Equal(1, deduplicated.AcceptedRuns);
        Assert.Equal(1, deduplicated.RejectedRuns);
        Assert.Equal(100, deduplicated.Metrics["repairPrecision"].Denominator);
        Assert.Contains(
            deduplicated.Runs,
            static run => run.ReasonCodes.Contains("accumulate-duplicate-run"));

        // Distinct evidence merges additively, which is what "100 clean first attempts" needs:
        // independent runs across jobs and days, not one long in-process loop. Only device-backed
        // evidence is genuinely per-run; the curated share is the same corpus re-read.
        var secondInput = QualifiedInput();
        for (var index = 0; index < 40; index++)
        {
            secondInput.Samples.Add(new MauiQualificationExecutionSample
            {
                SampleId = $"repair-second-{index}",
                Source = MauiQualificationSampleSources.DeviceBacked,
                Platform = "android",
                RealDevice = true,
                RepairProposed = true,
                RepairExpected = true,
                RepairCorrect = true,
                ExpectedFailureClass = MauiFlowFailureClasses.LocatorNotFound,
                ObservedFailureClass = MauiFlowFailureClasses.LocatorNotFound,
                FailureClassInferred = true,
            });
        }
        var second = MauiPreviewQualificationGateEvaluator.Evaluate(secondInput, DateTimeOffset.UnixEpoch.AddDays(2));
        var merged = MauiPreviewQualificationAccumulator.Accumulate([first, second], DateTimeOffset.UnixEpoch);
        Assert.Equal(2, merged.AcceptedRuns);
        Assert.Equal(0, merged.RejectedRuns);
        // 100 curated counted once (both runs read the same corpus) plus 40 device-backed trials.
        Assert.Equal(140, merged.Metrics["repairPrecision"].Denominator);
        Assert.Equal(140, merged.Metrics["repairPrecision"].Numerator);
        Assert.Equal(140, merged.Metrics["repairPrecision"].IndependentEvaluations);
        Assert.Equal(900, merged.Metrics["falseHeals"].Denominator);
        Assert.Equal(600, merged.Metrics["falseHeals"].IndependentEvaluations);
        Assert.Equal(MauiPreviewQualificationStates.Pass, Gate(merged, "accumulated-repair-precision").Status);
        Assert.Equal(MauiPreviewQualificationStates.Pass, Gate(merged, "accumulated-zero-false-heals").Status);

        // Two runs claiming the same corpus but reporting different static counts did not read the
        // same corpus. Merging them would sum evidence that is supposed to be identical.
        var shrunkStatic = QualifiedInput();
        shrunkStatic.Samples.RemoveAll(static sample =>
            sample.Source == MauiQualificationSampleSources.Curated && sample.RepairProposed == true);
        var staticMismatch = MauiPreviewQualificationAccumulator.Accumulate(
            [first, MauiPreviewQualificationGateEvaluator.Evaluate(shrunkStatic, DateTimeOffset.UnixEpoch.AddDays(4))],
            DateTimeOffset.UnixEpoch);
        Assert.Equal(1, staticMismatch.AcceptedRuns);
        Assert.Contains(
            staticMismatch.Runs,
            static run => run.ReasonCodes.Contains("accumulate-static-evidence-mismatch"));

        // Fail closed on anything that would silently pool incomparable evidence.
        var otherPlatform = QualifiedInput();
        otherPlatform.Platform = "ios";
        foreach (var sample in otherPlatform.Samples)
            sample.Platform = "ios";
        var crossPlatform = MauiPreviewQualificationAccumulator.Accumulate(
            [first, MauiPreviewQualificationGateEvaluator.Evaluate(otherPlatform, DateTimeOffset.UnixEpoch.AddDays(3))],
            DateTimeOffset.UnixEpoch);
        Assert.Equal(1, crossPlatform.AcceptedRuns);
        Assert.Contains(
            crossPlatform.Runs,
            static run => run.ReasonCodes.Contains("accumulate-platform-mismatch"));
        Assert.Equal(MauiPreviewQualificationStates.NotQualified, crossPlatform.Status);
    }

    [Fact]
    public void Accumulator_NeverTreatsPooledEvidenceAsIndependentDeviceRuns()
    {
        var deviceInput = QualifiedInput();
        var deviceRun = MauiPreviewQualificationGateEvaluator.Evaluate(deviceInput, DateTimeOffset.UnixEpoch);
        Assert.True(deviceRun.Metrics.RecordingValidity.IndependentDeviceRuns);

        var staticInput = QualifiedInput();
        staticInput.Samples.RemoveAll(static sample => sample.Source == MauiQualificationSampleSources.DeviceBacked);
        for (var index = 0; index < 100; index++)
        {
            staticInput.Samples.Add(new MauiQualificationExecutionSample
            {
                SampleId = $"generated-recording-{index}",
                Source = MauiQualificationSampleSources.Generated,
                Platform = "android",
                RecordingValid = true,
            });
        }
        var staticRun = MauiPreviewQualificationGateEvaluator.Evaluate(staticInput, DateTimeOffset.UnixEpoch.AddDays(1));
        Assert.False(staticRun.Metrics.RecordingValidity.IndependentDeviceRuns);

        // Dropping the device evidence and adding generated cases changes the static share, so the
        // pair is refused outright rather than pooled.
        var refused = MauiPreviewQualificationAccumulator.Accumulate([deviceRun, staticRun], DateTimeOffset.UnixEpoch);
        Assert.Equal(1, refused.AcceptedRuns);
        Assert.Contains(
            refused.Runs,
            static run => run.ReasonCodes.Contains("accumulate-static-evidence-mismatch"));

        // The pooling case that must not launder: identical static evidence in both runs, but one
        // run's recording evidence is not from a real device. The merged metric must not claim
        // independent device runs on the strength of the other run.
        var pooledInput = QualifiedInput();
        pooledInput.Samples.Add(new MauiQualificationExecutionSample
        {
            SampleId = "emulator-recording",
            Source = MauiQualificationSampleSources.DeviceBacked,
            Platform = "android",
            RealDevice = false,
            RecordingValid = true,
        });
        var pooledRun = MauiPreviewQualificationGateEvaluator.Evaluate(pooledInput, DateTimeOffset.UnixEpoch.AddDays(1));
        Assert.False(pooledRun.Metrics.RecordingValidity.IndependentDeviceRuns);

        var merged = MauiPreviewQualificationAccumulator.Accumulate([deviceRun, pooledRun], DateTimeOffset.UnixEpoch);
        Assert.Equal(2, merged.AcceptedRuns);
        Assert.False(merged.Metrics["recordingValidity"].IndependentDeviceRuns);
        // Static evidence is counted once no matter how many runs re-read it.
        Assert.Equal(
            deviceRun.Metrics.RecordingValidity.SourceCounts
                .Where(static item => MauiQualificationSampleSources.IsStatic(item.Source))
                .Sum(static item => item.Denominator),
            merged.Metrics["recordingValidity"].SourceCounts
                .Where(static item => MauiQualificationSampleSources.IsStatic(item.Source))
                .Sum(static item => item.Denominator));
    }

    [Fact]
    public void GateEvaluator_RestatedCasesInflateNoGateMinimum()
    {
        // The failure mode this whole change exists to prevent: 200 copies of one case must not
        // read as 200 trials just because the denominator says so.
        var input = QualifiedInput();
        input.Samples.RemoveAll(static sample => sample.RepairProposed == true);
        for (var index = 0; index < 200; index++)
        {
            input.Samples.Add(new MauiQualificationExecutionSample
            {
                SampleId = $"restated-{index}",
                Source = MauiQualificationSampleSources.CuratedDerived,
                Platform = "android",
                RepairProposed = true,
                RepairExpected = true,
                RepairCorrect = true,
            });
        }

        var report = MauiPreviewQualificationGateEvaluator.Evaluate(input, DateTimeOffset.UnixEpoch);

        Assert.Equal(200, report.Metrics.RepairPrecision.Denominator);
        Assert.Equal(0, report.Metrics.RepairPrecision.IndependentEvaluations);
        Assert.Equal(
            MauiPreviewQualificationStates.NotQualified,
            Gate(report, "repair-precision").Status);
        Assert.Contains("repair-evaluation-count-insufficient", Gate(report, "repair-precision").ReasonCodes);
    }

    [Fact]
    public void GateEvaluator_StampHonouredClassificationsNeverCarryTheAccuracyGate()
    {
        // A corpus that stamps its own failure class answers by copying. Those cases are reported
        // but must not be able to satisfy the classification minimum on their own.
        var input = QualifiedInput();
        foreach (var sample in input.Samples.Where(static sample => sample.ExpectedFailureClass is not null))
            sample.FailureClassInferred = false;

        var report = MauiPreviewQualificationGateEvaluator.Evaluate(input, DateTimeOffset.UnixEpoch);

        Assert.Equal(100, report.Metrics.ClassificationAccuracy.Denominator);
        Assert.Equal(100, report.Metrics.ClassificationAccuracy.Numerator);
        Assert.Equal(0, report.Metrics.ClassificationAccuracy.IndependentEvaluations);
        Assert.Equal(100, report.Metrics.ClassificationMatrix.StampHonouredSampleCount);
        Assert.Equal(
            MauiPreviewQualificationStates.NotQualified,
            Gate(report, "classification-accuracy").Status);
    }

    [Fact]
    public void Accumulator_RefusesRunsWithRelaxedThresholdsOrUnmodelledEvidence()
    {
        var first = MauiPreviewQualificationGateEvaluator.Evaluate(QualifiedInput(), DateTimeOffset.UnixEpoch);

        // A hand-edited run file cannot lower the bar for the whole accumulation.
        var relaxed = MauiPreviewQualificationGateEvaluator.Evaluate(QualifiedInput(), DateTimeOffset.UnixEpoch.AddDays(1));
        relaxed.Thresholds.MinimumRepairEvaluations = 1;
        var withRelaxed = MauiPreviewQualificationAccumulator.Accumulate([first, relaxed], DateTimeOffset.UnixEpoch);
        Assert.Equal(1, withRelaxed.AcceptedRuns);
        Assert.Contains(
            withRelaxed.Runs,
            static run => run.ReasonCodes.Contains("accumulate-threshold-not-policy-default"));
        Assert.Equal(100, withRelaxed.Thresholds.MinimumRepairEvaluations);

        // Unknown properties survive a JSON round-trip but are not fingerprinted, so a report
        // carrying them cannot be deduplicated correctly and is refused instead.
        var unmodelled = MauiPreviewQualificationGateEvaluator.Evaluate(QualifiedInput(), DateTimeOffset.UnixEpoch.AddDays(2));
        unmodelled.Corpus.ExtensionData = new Dictionary<string, JsonElement>
        {
            ["surprise"] = JsonDocument.Parse("1").RootElement.Clone(),
        };
        var withUnmodelled = MauiPreviewQualificationAccumulator.Accumulate([first, unmodelled], DateTimeOffset.UnixEpoch);
        Assert.Contains(
            withUnmodelled.Runs,
            static run => run.ReasonCodes.Contains("accumulate-unmodelled-evidence"));
    }

    [Fact]
    public void Accumulator_ProfileOrderIsNotFreshEvidence()
    {
        var first = QualifiedInput();
        first.Profiles.Add(new MauiQualificationPlatformProfile { Platform = "android", Scope = "a" });
        first.Profiles.Add(new MauiQualificationPlatformProfile { Platform = "android", Scope = "b" });
        var reordered = QualifiedInput();
        reordered.Profiles.Add(new MauiQualificationPlatformProfile { Platform = "android", Scope = "b" });
        reordered.Profiles.Add(new MauiQualificationPlatformProfile { Platform = "android", Scope = "a" });

        Assert.Equal(
            MauiPreviewQualificationAccumulator.ComputeEvidenceFingerprint(
                MauiPreviewQualificationGateEvaluator.Evaluate(first, DateTimeOffset.UnixEpoch)),
            MauiPreviewQualificationAccumulator.ComputeEvidenceFingerprint(
                MauiPreviewQualificationGateEvaluator.Evaluate(reordered, DateTimeOffset.UnixEpoch.AddDays(1))));
    }

    [Fact]
    public void CorpusRunner_RejectsUnknownCaseKeysAndUnattributedDerivedCases()
    {
        // Copied rather than mutated in place: SelectorHealthTests reads the same files from a
        // parallel xUnit collection, and a hang-kill mid-test would leave the repository dirty.
        var scratch = Path.Combine(
            Path.GetDirectoryName(typeof(PreviewQualificationTests).Assembly.Location)!,
            "corpus-mutation-tests",
            Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(FindRepositoryRoot(), "tests", "DevFlow", "InspectorCorpus"), scratch);
        try
        {
            var casePath = Directory.GetFiles(Path.Combine(scratch, "cases"), "repair-positive-*.json")
                .OrderBy(static path => path, StringComparer.Ordinal)
                .First();
            var original = File.ReadAllText(casePath);
            Assert.Empty(RunCorpus(scratch).Summary.Errors);

            // The schema declares additionalProperties:false at the case root. A typo must fail
            // the corpus, not quietly drop the case out of a denominator.
            var withUnknownKey = JsonNode.Parse(original)!.AsObject();
            withUnknownKey["expectedFailureclass"] = "locator-not-found";
            File.WriteAllText(casePath, withUnknownKey.ToJsonString());
            Assert.Contains("corpus-case-unknown-property", RunCorpus(scratch).Summary.Errors);

            // A derived case must name its seed, or the curated-versus-derived split it feeds is
            // unverifiable.
            var withoutDerivedFrom = JsonNode.Parse(original)!.AsObject();
            withoutDerivedFrom["provenance"]!.AsObject().Remove("derivedFrom");
            File.WriteAllText(casePath, withoutDerivedFrom.ToJsonString());
            Assert.Contains("corpus-case-provenance-invalid", RunCorpus(scratch).Summary.Errors);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public void CorpusRunner_NeverCountsAClassTheFixtureAlreadyNamedAsInference()
    {
        var scratch = Path.Combine(
            Path.GetDirectoryName(typeof(PreviewQualificationTests).Assembly.Location)!,
            "corpus-inference-tests",
            Guid.NewGuid().ToString("N"));
        var source = Path.Combine(FindRepositoryRoot(), "tests", "DevFlow", "InspectorCorpus");
        CopyDirectory(source, scratch);
        try
        {
            var baselineMatrix = Report(RunCorpus(scratch), DateTimeOffset.UnixEpoch).Metrics.ClassificationMatrix;

            // MauiFlowFailureClassifier.Classify short-circuits on terminalOutcome and on an
            // otherFailures flag before it ever looks at the stamped failure class. A case that
            // omits "failure" but names its class through either of those routes is still answered
            // by copying, and must not be counted as a genuinely inferred evaluation.
            var seed = JsonNode.Parse(
                File.ReadAllText(Path.Combine(scratch, "cases", "baseline-unique-automation-id.json")))!.AsObject();
            foreach (var (id, fixtureKey, fixtureValue) in new[]
            {
                ("classification-shortcut-transport", "otherFailures", (JsonNode)new JsonArray("transport")),
                ("classification-shortcut-cancelled", "terminalOutcome", JsonValue.Create("cancelled")!),
            })
            {
                var injected = JsonNode.Parse(seed.ToJsonString())!.AsObject();
                injected["id"] = id;
                injected["expectedFailureClass"] = fixtureKey == "otherFailures" ? "transport" : "cancelled";
                injected["fixture"]!.AsObject()[fixtureKey] = fixtureValue;
                File.WriteAllText(Path.Combine(scratch, "cases", id + ".json"), injected.ToJsonString());
            }
            var manifestPath = Path.Combine(scratch, "corpus-manifest.json");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            foreach (var id in new[] { "classification-shortcut-transport", "classification-shortcut-cancelled" })
            {
                manifest["cases"]!.AsArray().Add(new JsonObject
                {
                    ["id"] = id,
                    ["file"] = "cases/" + id + ".json",
                    ["kind"] = "baseline",
                    ["disposition"] = "diagnostic-only",
                });
            }
            File.WriteAllText(manifestPath, manifest.ToJsonString());

            var run = RunCorpus(scratch);
            Assert.Empty(run.Summary.Errors);
            var matrix = Report(run, DateTimeOffset.UnixEpoch).Metrics.ClassificationMatrix;
            Assert.Equal(baselineMatrix.SampleCount + 2, matrix.SampleCount);
            // Both new cases land in the stamp-honoured bucket, not the inferred one.
            Assert.Equal(baselineMatrix.InferredSampleCount, matrix.InferredSampleCount);
            Assert.Equal(baselineMatrix.StampHonouredSampleCount + 2, matrix.StampHonouredSampleCount);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public void Baseline_MatchesAFreshlyGeneratedReport()
    {
        // The baseline diff is monotone: it flags current < baseline and is silent otherwise, so a
        // commit that weakens the corpus *and* re-baselines is green. This is what stops that.
        var baselinePath = Path.Combine(
            FindRepositoryRoot(), "tests", "DevFlow", "InspectorCorpus", "baselines", "qualification.json");
        var committed = JsonNode.Parse(File.ReadAllText(baselinePath))!.AsObject();
        var corpus = RunCorpus();
        var freshReport = MauiPreviewQualificationGateEvaluator.Evaluate(
            new MauiPreviewQualificationInput
            {
                Platform = committed["platform"]!.GetValue<string>(),
                Corpus = corpus.Summary,
                Samples = corpus.Samples,
                PrivacySecurity = corpus.PrivacySecurity,
            },
            DateTimeOffset.UnixEpoch);
        var fresh = JsonNode.Parse(JsonSerializer.Serialize(
            freshReport, MauiTestingJsonContext.Default.MauiPreviewQualificationReport))!.AsObject();

        Assert.Equal(Canonical(committed["corpus"]!), Canonical(fresh["corpus"]!));

        // Every metric the CI diff gates on, recomputed from the corpus on disk. runtimeOverhead is
        // machine dependent and the rest carry no static evidence.
        foreach (var name in new[]
        {
            "repairPrecision", "repairRecall", "falseHeals", "abstention",
            "classificationAccuracy", "classificationMatrix", "selectorStability",
            "recordingValidity", "privacySecurityEscapes",
        })
        {
            Assert.Equal(Canonical(committed["metrics"]![name]!), Canonical(fresh["metrics"]![name]!));
        }

        Assert.Equal(committed["status"]!.GetValue<string>(), freshReport.Status);
    }

    private static string Canonical(JsonNode node) =>
        JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(node.ToJsonString()));

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    [Fact]
    public void Accumulator_ReReadingTheSameCorpusNeverGrowsIndependentEvidence()
    {
        // --mutation-seed changes the evidence fingerprint, so 30 seeded runs are 30 *distinct*,
        // accepted runs by the dedupe rule. They are still one corpus read thirty times, and the
        // independent counts the gates read must not move.
        var runs = Enumerable.Range(0, 30)
            .Select(seed => MauiPreviewQualificationCorpusRunner.Run(new MauiPreviewQualificationCorpusRunRequest
            {
                CorpusRoot = Path.Combine(FindRepositoryRoot(), "tests", "DevFlow", "InspectorCorpus"),
                Platform = "android",
                MutationSeed = 20260802 + seed,
                GeneratedNoRepairEvaluations = 300,
            }))
            .Select((result, index) => Report(result, DateTimeOffset.UnixEpoch.AddDays(index)))
            .ToList();
        Assert.Equal(
            30,
            runs.Select(MauiPreviewQualificationAccumulator.ComputeEvidenceFingerprint)
                .Distinct(StringComparer.Ordinal)
                .Count());

        var single = MauiPreviewQualificationAccumulator.Accumulate([runs[0]], DateTimeOffset.UnixEpoch);
        var many = MauiPreviewQualificationAccumulator.Accumulate(runs, DateTimeOffset.UnixEpoch);
        Assert.Equal(30, many.AcceptedRuns);
        foreach (var name in MauiPreviewQualificationAccumulator.MergedMetricNames)
        {
            Assert.Equal(single.Metrics[name].Denominator, many.Metrics[name].Denominator);
            Assert.Equal(single.Metrics[name].IndependentEvaluations, many.Metrics[name].IndependentEvaluations);
        }
        Assert.Equal(1, many.Metrics["repairPrecision"].IndependentEvaluations);
        Assert.Equal(16, many.Metrics["falseHeals"].IndependentEvaluations);
        Assert.Equal(8, many.Metrics["classificationAccuracy"].IndependentEvaluations);
        Assert.NotEqual(MauiPreviewQualificationStates.Pass, Gate(many, "accumulated-repair-precision").Status);
        Assert.NotEqual(MauiPreviewQualificationStates.Pass, Gate(many, "accumulated-zero-false-heals").Status);
        Assert.NotEqual(MauiPreviewQualificationStates.Pass, Gate(many, "accumulated-classification-accuracy").Status);
    }

    [Fact]
    public void Accumulator_RefusesRunsWhoseSourceCountsDoNotAddUp()
    {
        var honest = MauiPreviewQualificationGateEvaluator.Evaluate(QualifiedInput(), DateTimeOffset.UnixEpoch);
        var edited = MauiPreviewQualificationGateEvaluator.Evaluate(QualifiedInput(), DateTimeOffset.UnixEpoch.AddDays(1));
        // Exactly what a hand-edited run file looks like: totals raised, per-source counts left
        // alone. Nothing else in the pipeline recomputes these, so the merge has to refuse them.
        edited.Metrics.RepairPrecision.Denominator += 50;
        edited.Metrics.RepairPrecision.Numerator += 50;
        edited.Metrics.RepairPrecision.IndependentEvaluations += 50;

        var accumulation = MauiPreviewQualificationAccumulator.Accumulate([honest, edited], DateTimeOffset.UnixEpoch);
        Assert.Equal(1, accumulation.AcceptedRuns);
        Assert.Contains(
            accumulation.Runs,
            static run => run.ReasonCodes.Contains("accumulate-incoherent-metric"));
        Assert.Equal(100, accumulation.Metrics["repairPrecision"].IndependentEvaluations);
    }

    [Fact]
    public void GateEvaluator_LowerBoundGatesReadTheIndependentIntervalNotThePooledOne()
    {
        var report = Report(RunCorpus(), DateTimeOffset.UnixEpoch);
        var precision = report.Metrics.RepairPrecision;

        // 31/31 pooled versus 1/1 independent. The pooled Wilson lower bound is far above the
        // truth the sample supports, which is exactly how clones would buy a passing gate.
        Assert.Equal(31, precision.Denominator);
        Assert.Equal(1, precision.IndependentEvaluations);
        Assert.NotNull(precision.ConfidenceInterval);
        Assert.NotNull(precision.IndependentConfidenceInterval);
        Assert.True(precision.IndependentConfidenceInterval!.Lower < precision.ConfidenceInterval!.Lower);
        Assert.True(precision.IndependentConfidenceInterval.Lower < 0.25);
        Assert.True(precision.ConfidenceInterval.Lower > 0.85);

        var classification = report.Metrics.ClassificationAccuracy;
        Assert.True(classification.IndependentConfidenceInterval!.Lower < classification.ConfidenceInterval!.Lower);
    }

    private static MauiPreviewQualificationReport Report(
        MauiPreviewQualificationCorpusRunResult corpus,
        DateTimeOffset generatedAt) =>
        MauiPreviewQualificationGateEvaluator.Evaluate(
            new MauiPreviewQualificationInput
            {
                Platform = "android",
                Corpus = corpus.Summary,
                Samples = corpus.Samples,
                PrivacySecurity = corpus.PrivacySecurity,
            },
            generatedAt);

    private static MauiQualificationGateResult Gate(MauiQualificationAccumulation accumulation, string gateId) =>
        accumulation.Gates.Single(gate => gate.GateId == gateId);

    private static MauiPreviewQualificationCorpusRunResult RunCorpus(string? corpusRoot = null) =>
        MauiPreviewQualificationCorpusRunner.Run(new MauiPreviewQualificationCorpusRunRequest
        {
            CorpusRoot = corpusRoot ?? Path.Combine(FindRepositoryRoot(), "tests", "DevFlow", "InspectorCorpus"),
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
                CuratedRepairPositiveCases = 100,
                CuratedNoRepairCases = 100,
                GeneratedNoRepairCases = 300,
                CuratedClassificationLabeledCases = 100,
                ProvenanceComplete = true,
                ProvenanceSourceCounts =
                [
                    new MauiQualificationCorpusProvenanceCount
                    {
                        SourceKind = MauiQualificationCorpusProvenanceSourceKinds.ObservedLocalRun,
                        Count = 100,
                    },
                ],
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
                // A fully qualified run carries classification ground truth alongside repair
                // evidence: the accuracy gate must be earned, not skipped for lack of labels.
                ExpectedFailureClass = MauiFlowFailureClasses.LocatorNotFound,
                ObservedFailureClass = MauiFlowFailureClasses.LocatorNotFound,
                // The classifier derived this class from replay facts. A sample whose evidence
                // already stamped the class is correct by construction and never counts toward
                // this gate's minimum.
                FailureClassInferred = true,
            });
        }
        // A qualified preview needs an independent no-repair denominator, not machine-generated
        // mutants of a handful of seeds. The generated mutants stay in the report as context;
        // only these device-backed trials can satisfy the gate minimum.
        for (var index = 0; index < 300; index++)
        {
            input.Samples.Add(new MauiQualificationExecutionSample
            {
                SampleId = $"device-no-repair-{index}",
                Source = MauiQualificationSampleSources.DeviceBacked,
                Platform = "android",
                DeviceEvidenceKind = "physical-device",
                RealDevice = true,
                NoRepairExpected = true,
                FalseHeal = false,
                Abstained = true,
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
