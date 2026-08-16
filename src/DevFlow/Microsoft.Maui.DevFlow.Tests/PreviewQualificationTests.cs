using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
        // 100 device-backed repair trials per run, summed, plus the 40 extra in the second run.
        // Repair evidence in a qualified run is device-backed — the static corpus scores repair
        // with harness rules — so all of it is genuinely per-run and accumulates.
        Assert.Equal(240, merged.Metrics["repairPrecision"].Denominator);
        Assert.Equal(240, merged.Metrics["repairPrecision"].Numerator);
        // The static share is still counted once, which false heals shows: both runs re-read the
        // same 300 generated mutants (counted once) and each contributes 300 device-backed trials.
        Assert.Equal(900, merged.Metrics["falseHeals"].Denominator);
        Assert.Equal(
            300,
            merged.Metrics["falseHeals"].SourceCounts
                .Single(static count => count.Source == MauiQualificationSampleSources.Generated)
                .Denominator);
        Assert.Equal(240, merged.Metrics["repairPrecision"].IndependentEvaluations);
        Assert.Equal(600, merged.Metrics["falseHeals"].IndependentEvaluations);
        Assert.Equal(MauiPreviewQualificationStates.Pass, Gate(merged, "accumulated-repair-precision").Status);
        Assert.Equal(MauiPreviewQualificationStates.Pass, Gate(merged, "accumulated-zero-false-heals").Status);

        // Two runs claiming the same corpus but reporting different static counts did not read the
        // same corpus. Merging them would sum evidence that is supposed to be identical.
        var shrunkStatic = QualifiedInput();
        shrunkStatic.Samples.RemoveAll(static sample =>
            sample.Source == MauiQualificationSampleSources.Generated && sample.NoRepairExpected == true);
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

        // The assertions above are published values; they stay true even if the gates read the
        // wrong interval. This is the one that inverts: enough independent evidence to clear the
        // count check, an independent lower bound below the threshold, and a pooled lower bound
        // above it. If a gate reverts to metric.ConfidenceInterval it passes and this fails.
        var input = new MauiPreviewQualificationInput { Platform = "android" };
        for (var index = 0; index < 100; index++)
        {
            input.Samples.Add(new MauiQualificationExecutionSample
            {
                SampleId = $"device-repair-{index}",
                Source = MauiQualificationSampleSources.DeviceBacked,
                Platform = "android",
                RealDevice = true,
                RepairExpected = true,
                RepairProposed = true,
                RepairCorrect = index >= 4,
            });
        }
        for (var index = 0; index < 900; index++)
        {
            input.Samples.Add(new MauiQualificationExecutionSample
            {
                SampleId = $"derived-repair-{index}",
                Source = MauiQualificationSampleSources.CuratedDerived,
                Platform = "android",
                RepairExpected = true,
                RepairProposed = true,
                RepairCorrect = true,
            });
        }
        var skewed = MauiPreviewQualificationGateEvaluator.Evaluate(input, DateTimeOffset.UnixEpoch).Metrics.RepairPrecision;
        Assert.Equal(1000, skewed.Denominator);
        Assert.Equal(100, skewed.IndependentEvaluations);
        Assert.True(skewed.ConfidenceInterval!.Lower > 0.95, $"pooled lower bound was {skewed.ConfidenceInterval.Lower}");
        Assert.True(skewed.IndependentConfidenceInterval!.Lower < 0.95, $"independent lower bound was {skewed.IndependentConfidenceInterval.Lower}");

        var gate = MauiPreviewQualificationGateEvaluator
            .Evaluate(input, DateTimeOffset.UnixEpoch)
            .Gates.Single(static item => item.GateId == "repair-precision");
        Assert.Equal(MauiPreviewQualificationStates.Fail, gate.Status);
        Assert.Contains("repair-precision-lower-bound-below-threshold", gate.ReasonCodes);
    }

    [Fact]
    public void Accumulator_SumsCleanFirstAttemptsAcrossRuns()
    {
        // The whole argument for --accumulate over a higher --repeat cap: 20 attempts per run is
        // the harness limit, so 100 clean first attempts per flow has to come from separate jobs.
        var runs = new List<MauiPreviewQualificationReport>();
        for (var run = 0; run < 5; run++)
        {
            var input = new MauiPreviewQualificationInput { Platform = "android" };
            input.Tier1Flows.Add("checkout");
            // Independent shards run on distinct devices; the evidence fingerprint refuses
            // two runs whose recorded evidence is indistinguishable.
            input.Profiles.Add(new MauiQualificationPlatformProfile
            {
                Platform = "android",
                Scope = "tier1",
                DeviceEvidenceKind = "physical-device",
                RealDevice = true,
                DeviceFingerprint = $"device-{run}",
            });
            for (var attempt = 0; attempt < 20; attempt++)
            {
                input.Samples.Add(new MauiQualificationExecutionSample
                {
                    SampleId = $"run{run}-attempt{attempt}",
                    Source = MauiQualificationSampleSources.DeviceBacked,
                    Platform = "android",
                    RealDevice = true,
                    DeviceEvidenceKind = "physical-device",
                    FlowId = "checkout",
                    FirstAttempt = true,
                    CleanState = true,
                    Outcome = MauiFlowRunOutcomes.Passed,
                });
            }
            runs.Add(MauiPreviewQualificationGateEvaluator.Evaluate(input, DateTimeOffset.UnixEpoch.AddDays(run)));
        }

        var single = MauiPreviewQualificationAccumulator.Accumulate([runs[0]], DateTimeOffset.UnixEpoch);
        var singleFlow = Assert.Single(single.FirstAttemptFlows);
        Assert.Equal(20, singleFlow.CleanFirstAttempts);
        Assert.NotEqual(MauiPreviewQualificationStates.Pass, Gate(single, "accumulated-tier1-first-attempts").Status);

        var merged = MauiPreviewQualificationAccumulator.Accumulate(runs, DateTimeOffset.UnixEpoch);
        Assert.Equal(5, merged.AcceptedRuns);
        var flow = Assert.Single(merged.FirstAttemptFlows);
        Assert.Equal(singleFlow.FlowId, flow.FlowId);
        Assert.Equal(100, flow.CleanFirstAttempts);
        Assert.Equal(100, flow.PassedFirstAttempts);
        Assert.True(flow.RealDeviceEvidence);
        // The independence of those 5 runs is self-reported, so the merge publishes the shape of
        // the claim rather than pretending to have verified it.
        Assert.Equal(5, flow.ContributingRuns);
        Assert.Equal(5, flow.ContributingDevices);
        Assert.Equal(MauiPreviewQualificationStates.Pass, Gate(merged, "accumulated-tier1-first-attempts").Status);
    }

    [Fact]
    public void Accumulator_RefusesARunThatRepeatsOneFlowToReachTheAttemptThreshold()
    {
        // Without a coherence check on the flow list, repeating one 20/20 entry five times inside
        // a single file reaches the 100-attempt threshold from 20 real attempts.
        var input = new MauiPreviewQualificationInput { Platform = "android" };
        input.Tier1Flows.Add("checkout");
        input.Profiles.Add(new MauiQualificationPlatformProfile
        {
            Platform = "android",
            Scope = "tier1",
            DeviceEvidenceKind = "physical-device",
            RealDevice = true,
            DeviceFingerprint = "device-0",
        });
        var report = MauiPreviewQualificationGateEvaluator.Evaluate(input, DateTimeOffset.UnixEpoch);
        for (var copy = 0; copy < 5; copy++)
        {
            report.Metrics.FlakeFirstAttemptStability.Flows.Add(new MauiQualificationFlowAttemptSummary
            {
                FlowId = "checkout",
                CleanFirstAttempts = 20,
                PassedFirstAttempts = 20,
                Stability = 1,
                RealDeviceEvidence = true,
            });
        }

        var accumulation = MauiPreviewQualificationAccumulator.Accumulate([report], DateTimeOffset.UnixEpoch);
        Assert.Equal(0, accumulation.AcceptedRuns);
        Assert.Contains(
            accumulation.Runs,
            static run => run.ReasonCodes.Contains("accumulate-incoherent-flow-evidence"));
        Assert.Empty(accumulation.FirstAttemptFlows);
        Assert.NotEqual(MauiPreviewQualificationStates.Pass, Gate(accumulation, "accumulated-tier1-first-attempts").Status);
    }

    [Fact]
    public void Accumulator_RefusesDeviceEvidenceThatNamesNoDevice()
    {
        var input = new MauiPreviewQualificationInput { Platform = "android" };
        input.Tier1Flows.Add("checkout");
        var report = MauiPreviewQualificationGateEvaluator.Evaluate(input, DateTimeOffset.UnixEpoch);
        report.Metrics.FlakeFirstAttemptStability.Flows.Add(new MauiQualificationFlowAttemptSummary
        {
            FlowId = "checkout",
            CleanFirstAttempts = 200,
            PassedFirstAttempts = 200,
            Stability = 1,
            RealDeviceEvidence = true,
        });

        var accumulation = MauiPreviewQualificationAccumulator.Accumulate([report], DateTimeOffset.UnixEpoch);
        Assert.Equal(0, accumulation.AcceptedRuns);
        Assert.Contains(
            accumulation.Runs,
            static run => run.ReasonCodes.Contains("accumulate-unattributed-device-evidence"));
    }

    [Fact]
    public void Accumulator_KeepsTheEvidenceWhenOneStaleRunNamesADifferentCorpus()
    {
        // With the oldest run as reference, one leftover file from a superseded corpus rejected
        // every current run as a fingerprint mismatch and the merge reported almost nothing.
        static MauiPreviewQualificationReport Run(string corpusFingerprint, int hours, int attempts)
        {
            var input = new MauiPreviewQualificationInput { Platform = "android" };
            input.Tier1Flows.Add("checkout");
            input.Profiles.Add(new MauiQualificationPlatformProfile
            {
                Platform = "android",
                RealDevice = true,
                DeviceFingerprint = $"device-{hours}",
            });
            var report = MauiPreviewQualificationGateEvaluator.Evaluate(
                input,
                DateTimeOffset.UnixEpoch.AddHours(hours));
            report.Fingerprints.CorpusFingerprint = corpusFingerprint;
            report.Metrics.FlakeFirstAttemptStability.Flows.Add(new MauiQualificationFlowAttemptSummary
            {
                FlowId = "checkout",
                CleanFirstAttempts = attempts,
                PassedFirstAttempts = attempts,
                Stability = 1,
                RealDeviceEvidence = true,
            });
            return report;
        }

        var accumulation = MauiPreviewQualificationAccumulator.Accumulate(
            [Run("sha256:stale", 0, 7), Run("sha256:current", 1, 20), Run("sha256:current", 2, 20)],
            DateTimeOffset.UnixEpoch);

        Assert.Equal(2, accumulation.AcceptedRuns);
        Assert.Equal(
            40,
            accumulation.FirstAttemptFlows.Single(static flow => flow.FlowId == "checkout").CleanFirstAttempts);
        // The stale run is discarded loudly, not silently.
        Assert.Contains(
            accumulation.Runs,
            static run => run.ReasonCodes.Contains("accumulate-corpus-fingerprint-mismatch"));
    }

    [Fact]
    public void Accumulator_ElectsTheReferenceThatAdmitsTheMostRunsRatherThanTheLargestLookalikeGroup()
    {
        // Grouping on a subset of the compared fields elected a cohort whose members reject each
        // other: three runs differing only in --generated-no-repair share contract, platform,
        // policy and corpus fingerprint. Electing one of those discards the runs that agree.
        static MauiPreviewQualificationReport Run(int hours, int generated, int attempts)
        {
            var input = new MauiPreviewQualificationInput { Platform = "android" };
            input.Tier1Flows.Add("checkout");
            input.Profiles.Add(new MauiQualificationPlatformProfile
            {
                Platform = "android",
                RealDevice = true,
                DeviceFingerprint = $"device-{hours}",
            });
            var report = MauiPreviewQualificationGateEvaluator.Evaluate(
                input,
                DateTimeOffset.UnixEpoch.AddHours(hours));
            report.Fingerprints.CorpusFingerprint = "sha256:shared";
            report.Metrics.FalseHeals.Numerator = 0;
            report.Metrics.FalseHeals.Denominator = generated;
            report.Metrics.FalseHeals.IndependentEvaluations = 0;
            report.Metrics.FalseHeals.SourceCounts =
            [
                new MauiQualificationRateSourceCount
                {
                    Source = MauiQualificationSampleSources.Generated,
                    Numerator = 0,
                    Denominator = generated,
                    IndependentEvaluations = 0,
                },
            ];
            report.Metrics.FlakeFirstAttemptStability.Flows.Add(new MauiQualificationFlowAttemptSummary
            {
                FlowId = "checkout",
                CleanFirstAttempts = attempts,
                PassedFirstAttempts = attempts,
                Stability = 1,
                RealDeviceEvidence = true,
            });
            return report;
        }

        // Four mutually incompatible lookalikes (each its own static evidence) against three runs
        // that agree with each other. The lookalikes are the larger group *and* hold the newest
        // run, so a rule that groups on a proxy key and breaks ties by recency elects one of them
        // and admits only itself; electing by what a candidate actually admits keeps the trio.
        var accumulation = MauiPreviewQualificationAccumulator.Accumulate(
            [
                Run(1, 300, 20), Run(2, 300, 20), Run(3, 300, 20),
                Run(4, 100, 5), Run(5, 200, 5), Run(6, 250, 5), Run(7, 275, 5),
            ],
            DateTimeOffset.UnixEpoch);

        Assert.Equal(3, accumulation.AcceptedRuns);
        Assert.Equal(
            60,
            accumulation.FirstAttemptFlows.Single(static flow => flow.FlowId == "checkout").CleanFirstAttempts);
    }

    [Fact]
    public void Accumulator_RefusesToPoolEvidenceGatheredAgainstDifferentBuilds()
    {
        // Varying the claimed commit minted independent-looking runs exactly as varying
        // deviceFingerprint did, and reads as ordinary metadata rather than as a lever.
        static MauiPreviewQualificationReport Run(string commit, int hours)
        {
            var input = new MauiPreviewQualificationInput { Platform = "android" };
            input.Tier1Flows.Add("checkout");
            input.Profiles.Add(new MauiQualificationPlatformProfile
            {
                Platform = "android",
                RealDevice = true,
                DeviceFingerprint = "one-device",
            });
            var report = MauiPreviewQualificationGateEvaluator.Evaluate(
                input,
                DateTimeOffset.UnixEpoch.AddHours(hours));
            report.Fingerprints.RepositoryCommit = commit;
            report.Metrics.FlakeFirstAttemptStability.Flows.Add(new MauiQualificationFlowAttemptSummary
            {
                FlowId = "checkout",
                CleanFirstAttempts = 20,
                PassedFirstAttempts = 20,
                Stability = 1,
                RealDeviceEvidence = true,
            });
            return report;
        }

        var accumulation = MauiPreviewQualificationAccumulator.Accumulate(
            [Run("commit-a", 1), Run("commit-b", 2), Run("commit-c", 3)],
            DateTimeOffset.UnixEpoch);

        Assert.Equal(1, accumulation.AcceptedRuns);
        Assert.Contains(
            accumulation.Runs,
            static run => run.ReasonCodes.Contains("accumulate-product-identity-mismatch"));
    }

    [Fact]
    public void Accumulator_PoolsAStaticRunWithADeviceRunAndSaysWhatItCouldNotVerify()
    {
        // "unknown" is what FingerprintOrUnknown writes when the harness was never told a commit,
        // so comparing raw strings read a plain static run and a device-evidence run built from an
        // artifact manifest as two different builds — and the majority vote then discarded the one
        // device shard, which is the only run carrying first-attempt evidence at all. An
        // unasserted fact cannot contradict anything; what it also cannot do is confirm a match,
        // so the merge has to say which facts it took on trust.
        static MauiPreviewQualificationReport Run(string? commit, int hours, int attempts)
        {
            var input = new MauiPreviewQualificationInput { Platform = "android" };
            input.Tier1Flows.Add("checkout");
            input.Profiles.Add(new MauiQualificationPlatformProfile
            {
                Platform = "android",
                RealDevice = true,
                DeviceFingerprint = $"device-{hours}",
            });
            var report = MauiPreviewQualificationGateEvaluator.Evaluate(
                input,
                DateTimeOffset.UnixEpoch.AddHours(hours));
            if (commit is not null)
                report.Fingerprints.RepositoryCommit = commit;
            if (attempts > 0)
            {
                report.Metrics.FlakeFirstAttemptStability.Flows.Add(new MauiQualificationFlowAttemptSummary
                {
                    FlowId = "checkout",
                    CleanFirstAttempts = attempts,
                    PassedFirstAttempts = attempts,
                    Stability = 1,
                    RealDeviceEvidence = true,
                });
            }
            return report;
        }

        var accumulation = MauiPreviewQualificationAccumulator.Accumulate(
            [Run(null, 1, 0), Run("sha256:abc", 2, 100)],
            DateTimeOffset.UnixEpoch);

        Assert.Equal(2, accumulation.AcceptedRuns);
        Assert.Equal(
            100,
            accumulation.FirstAttemptFlows.Single(static flow => flow.FlowId == "checkout").CleanFirstAttempts);
        Assert.Contains("repositoryCommit", accumulation.UnverifiedProductIdentity);
    }

    [Fact]
    public void Accumulator_DoesNotLaunderHarnessScoredEvidenceIntoProductEvidence()
    {
        // Pooling must not upgrade what a number measures. If one contributing run scored its
        // no-repair samples with harness-local rules, the merged rate did too, however many
        // shipped-analyzer runs it is pooled with.
        static MauiPreviewQualificationReport Run(string kind, int hours)
        {
            var input = new MauiPreviewQualificationInput { Platform = "android" };
            // Distinct device evidence, or the two runs hash identically and the second is
            // correctly refused as a duplicate before the merge is reached.
            input.Profiles.Add(new MauiQualificationPlatformProfile
            {
                Platform = "android",
                RealDevice = true,
                DeviceFingerprint = $"device-{hours}",
            });
            var report = MauiPreviewQualificationGateEvaluator.Evaluate(
                input,
                DateTimeOffset.UnixEpoch.AddHours(hours));
            report.Metrics.FalseHeals.State = "measured";
            report.Metrics.FalseHeals.Denominator = 10;
            report.Metrics.FalseHeals.SourceCounts =
            [
                new MauiQualificationRateSourceCount
                {
                    Source = MauiQualificationSampleSources.Curated,
                    Numerator = 0,
                    Denominator = 10,
                    IndependentEvaluations = 10,
                },
            ];
            report.Metrics.FalseHeals.IndependentEvaluations = 10;
            report.Metrics.FalseHeals.Exercises = new MauiQualificationMetricProvenance
            {
                Component = $"Component.{kind}",
                Kind = kind,
                Note = "test",
            };
            return report;
        }

        var accumulation = MauiPreviewQualificationAccumulator.Accumulate(
            [
                Run(MauiQualificationMetricProvenanceKinds.ShippedAnalyzer, 1),
                Run(MauiQualificationMetricProvenanceKinds.HarnessLocalRules, 2),
            ],
            DateTimeOffset.UnixEpoch);

        Assert.Equal(2, accumulation.AcceptedRuns);
        var merged = accumulation.Metrics["falseHeals"];
        Assert.Equal(MauiQualificationMetricProvenanceKinds.HarnessLocalRules, merged.Exercises?.Kind);
        Assert.False(MauiQualificationMetricProvenanceKinds.IsProductEvidence(merged.Exercises?.Kind));

        // The merged verdict must carry the same disclosure the per-run report does. CI reads
        // `--accumulate --fail-on-non-pass`, so a gate that only exists per-run is not a gate.
        var gate = Gate(accumulation, "product-analyzer-coverage");
        Assert.Equal(MauiPreviewQualificationStates.NotQualified, gate.Status);
        Assert.Contains("corpus-does-not-exercise-shipped-analyzer", gate.ReasonCodes);
        Assert.Contains("falseHeals", gate.Message);
    }

    [Fact]
    public void Accumulator_TreatsAnUndeclaredContributorAsUnknownRatherThanProductEvidence()
    {
        // Silence is not a claim of product provenance. Filtering undeclared contributors out
        // before picking the weakest kind would let one run's honest label speak for another
        // run's unlabelled evidence.
        static MauiPreviewQualificationReport Run(MauiQualificationMetricProvenance? exercises, int hours)
        {
            var input = new MauiPreviewQualificationInput { Platform = "android" };
            input.Profiles.Add(new MauiQualificationPlatformProfile
            {
                Platform = "android",
                RealDevice = true,
                DeviceFingerprint = $"device-{hours}",
            });
            var report = MauiPreviewQualificationGateEvaluator.Evaluate(
                input,
                DateTimeOffset.UnixEpoch.AddHours(hours));
            report.Metrics.FalseHeals.State = "measured";
            report.Metrics.FalseHeals.Denominator = 10;
            report.Metrics.FalseHeals.IndependentEvaluations = 10;
            report.Metrics.FalseHeals.SourceCounts =
            [
                new MauiQualificationRateSourceCount
                {
                    Source = MauiQualificationSampleSources.DeviceBacked,
                    Numerator = 0,
                    Denominator = 10,
                    IndependentEvaluations = 10,
                },
            ];
            report.Metrics.FalseHeals.Exercises = exercises;
            return report;
        }

        var accumulation = MauiPreviewQualificationAccumulator.Accumulate(
            [
                Run(
                    new MauiQualificationMetricProvenance
                    {
                        Component = "submitting-run",
                        Kind = MauiQualificationMetricProvenanceKinds.SampleSupplied,
                        Note = "test",
                    },
                    1),
                Run(null, 2),
            ],
            DateTimeOffset.UnixEpoch);

        Assert.Equal(2, accumulation.AcceptedRuns);
        var merged = accumulation.Metrics["falseHeals"];
        Assert.Equal("unknown", merged.Exercises?.Kind);
        Assert.False(MauiQualificationMetricProvenanceKinds.IsProductEvidence(merged.Exercises?.Kind));
        Assert.Contains("without declaring what produced them", merged.Exercises!.Note);
        Assert.Equal(
            MauiPreviewQualificationStates.NotQualified,
            Gate(accumulation, "product-analyzer-coverage").Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Accumulator_MergesProvenanceToTheWeakestClaimRegardlessOfRunOrder(bool reversed)
    {
        // Merge order is wall-clock sort order, which is arbitrary with respect to evidence
        // quality. A first-match rule let whichever run happened to sort first decide between
        // shipped-analyzer and sample-supplied, so pooling could upgrade the published label.
        var first = RunWith(MauiQualificationMetricProvenanceKinds.ShippedAnalyzer, "run-a", 1);
        var second = RunWith(MauiQualificationMetricProvenanceKinds.SampleSupplied, "run-b", 2);
        var reports = reversed ? new[] { second, first } : [first, second];

        var accumulation = MauiPreviewQualificationAccumulator.Accumulate(reports, DateTimeOffset.UnixEpoch);

        Assert.Equal(2, accumulation.AcceptedRuns);
        Assert.Equal(
            MauiQualificationMetricProvenanceKinds.SampleSupplied,
            accumulation.Metrics["falseHeals"].Exercises?.Kind);

        static MauiPreviewQualificationReport RunWith(string kind, string device, int hours)
        {
            var input = new MauiPreviewQualificationInput { Platform = "android" };
            input.Profiles.Add(new MauiQualificationPlatformProfile
            {
                Platform = "android",
                RealDevice = true,
                DeviceFingerprint = device,
            });
            var report = MauiPreviewQualificationGateEvaluator.Evaluate(
                input,
                DateTimeOffset.UnixEpoch.AddHours(hours));
            report.Metrics.FalseHeals.State = "measured";
            report.Metrics.FalseHeals.Denominator = 10;
            report.Metrics.FalseHeals.IndependentEvaluations = 10;
            report.Metrics.FalseHeals.SourceCounts =
            [
                new MauiQualificationRateSourceCount
                {
                    Source = MauiQualificationSampleSources.DeviceBacked,
                    Numerator = 0,
                    Denominator = 10,
                    IndependentEvaluations = 10,
                },
            ];
            report.Metrics.FalseHeals.Exercises = new MauiQualificationMetricProvenance
            {
                Component = device,
                Kind = kind,
            };
            return report;
        }
    }

    [Fact]
    public void Accumulator_RefusesARunWhoseDeclaredProvenanceContradictsItsOwnSources()
    {
        // The overstating direction is checkable, so it is checked. Otherwise a hand-edited run
        // file could keep an honest curated denominator and simply claim a device observed it.
        var input = new MauiPreviewQualificationInput { Platform = "android" };
        var report = MauiPreviewQualificationGateEvaluator.Evaluate(input, DateTimeOffset.UnixEpoch);
        report.Metrics.FalseHeals.State = "measured";
        report.Metrics.FalseHeals.Denominator = 10;
        report.Metrics.FalseHeals.IndependentEvaluations = 10;
        report.Metrics.FalseHeals.SourceCounts =
        [
            new MauiQualificationRateSourceCount
            {
                Source = MauiQualificationSampleSources.Curated,
                Numerator = 0,
                Denominator = 10,
                IndependentEvaluations = 10,
            },
        ];
        report.Metrics.FalseHeals.Exercises = new MauiQualificationMetricProvenance
        {
            Component = "submitting-run",
            Kind = MauiQualificationMetricProvenanceKinds.SampleSupplied,
            Note = "claimed observed on a device",
        };

        var accumulation = MauiPreviewQualificationAccumulator.Accumulate([report], DateTimeOffset.UnixEpoch);

        Assert.Equal(0, accumulation.AcceptedRuns);
        Assert.Contains(
            accumulation.Runs,
            static run => run.ReasonCodes.Contains("accumulate-provenance-mismatch"));
    }

    [Fact]
    public void Accumulator_KeepsUnderstatedProvenanceRatherThanDiscardingTheEvidence()
    {
        // An earlier version of this tool labelled an under-attested device sample with the static
        // component. That is an understatement, not a lie, and rejecting it would silently drop a
        // whole run's evidence during a mixed-version rollout — a discarded run never reaches the
        // gates, so a real failure would disappear instead of failing.
        var input = new MauiPreviewQualificationInput { Platform = "android" };
        input.Profiles.Add(new MauiQualificationPlatformProfile
        {
            Platform = "android",
            RealDevice = true,
            DeviceFingerprint = "legacy-device",
        });
        var report = MauiPreviewQualificationGateEvaluator.Evaluate(input, DateTimeOffset.UnixEpoch);
        report.Metrics.FalseHeals.State = "measured";
        report.Metrics.FalseHeals.Denominator = 10;
        report.Metrics.FalseHeals.IndependentEvaluations = 10;
        report.Metrics.FalseHeals.SourceCounts =
        [
            new MauiQualificationRateSourceCount
            {
                Source = MauiQualificationSampleSources.DeviceBacked,
                Numerator = 0,
                Denominator = 10,
                IndependentEvaluations = 10,
            },
        ];
        report.Metrics.FalseHeals.Exercises = new MauiQualificationMetricProvenance
        {
            Component = "MauiPreviewQualificationCorpusRunner.EvaluateFixture",
            Kind = MauiQualificationMetricProvenanceKinds.HarnessLocalRules,
            Note = "written by an older evaluator",
        };

        var accumulation = MauiPreviewQualificationAccumulator.Accumulate([report], DateTimeOffset.UnixEpoch);

        Assert.Equal(1, accumulation.AcceptedRuns);
        Assert.DoesNotContain(
            accumulation.Runs,
            static run => run.ReasonCodes.Contains("accumulate-provenance-mismatch"));
        // Accepted, but never upgraded: the understatement is what the merged total reports.
        Assert.Equal(
            MauiQualificationMetricProvenanceKinds.HarnessLocalRules,
            accumulation.Metrics["falseHeals"].Exercises?.Kind);
    }

    [Theory]
    [InlineData(MauiQualificationSampleSources.Curated, MauiQualificationSampleSources.DeviceBacked, true)]
    [InlineData(MauiQualificationSampleSources.Curated, MauiQualificationSampleSources.Generated, true)]
    [InlineData(MauiQualificationSampleSources.DeviceBacked, MauiQualificationSampleSources.Generated, true)]
    [InlineData(MauiQualificationSampleSources.DeviceBacked, MauiQualificationSampleSources.DeviceBacked, false)]
    [InlineData(MauiQualificationSampleSources.CuratedDerived, MauiQualificationSampleSources.Generated, false)]
    [InlineData(MauiQualificationSampleSources.Generated, MauiQualificationSampleSources.Generated, true)]
    public void Accumulator_AcceptsEveryProvenanceTheEvaluatorItselfProduces(
        string firstSource,
        string secondSource,
        bool inferred)
    {
        // The provenance check derives its expectation from source counts; the evaluator derives
        // the label from samples. If those two ever disagree, the accumulator starts rejecting
        // reports this very assembly generated. They disagreed once, when the label was computed
        // from the stricter real-device attestation test rather than from the source, so a
        // device-backed sample missing deviceEvidenceKind was attributed to the corpus runner.
        var input = new MauiPreviewQualificationInput { Platform = "android" };
        input.Profiles.Add(new MauiQualificationPlatformProfile
        {
            Platform = "android",
            RealDevice = true,
            DeviceFingerprint = $"device-{firstSource}-{secondSource}-{inferred}",
        });
        foreach (var (source, index) in new[] { (firstSource, 0), (secondSource, 1) })
        {
            input.Samples.Add(new MauiQualificationExecutionSample
            {
                SampleId = $"repair-{index}",
                Source = source,
                Platform = "android",
                RealDevice = true,
                RepairExpected = true,
                RepairProposed = true,
                RepairCorrect = true,
                NoRepairExpected = false,
                ExpectedFailureClass = MauiFlowFailureClasses.LocatorNotFound,
                ObservedFailureClass = MauiFlowFailureClasses.LocatorNotFound,
                FailureClassInferred = inferred,
            });
            input.Samples.Add(new MauiQualificationExecutionSample
            {
                SampleId = $"no-repair-{index}",
                Source = source,
                Platform = "android",
                RealDevice = true,
                NoRepairExpected = true,
                FalseHeal = false,
                Abstained = true,
            });
        }
        var report = MauiPreviewQualificationGateEvaluator.Evaluate(input, DateTimeOffset.UnixEpoch);

        var accumulation = MauiPreviewQualificationAccumulator.Accumulate([report], DateTimeOffset.UnixEpoch);

        Assert.DoesNotContain(
            accumulation.Runs.SelectMany(static run => run.ReasonCodes),
            static reason => reason == "accumulate-provenance-mismatch");
        Assert.Equal(1, accumulation.AcceptedRuns);
    }

    [Fact]
    public void Corpus_WillNotCreditARunWithASampleWhoseSourceItCannotIdentify()
    {
        // An unrecognised source normalises to "unknown". It is not device-backed, so it cannot be
        // described as something the submitting run observed — the permissive reading would let an
        // unlabelled sample buy a pass on the coverage gate.
        var input = QualifiedInput();
        input.Samples.Add(new MauiQualificationExecutionSample
        {
            SampleId = "unlabelled-no-repair",
            Source = "totally-made-up",
            NoRepairExpected = true,
            FalseHeal = false,
            Abstained = true,
        });

        var report = MauiPreviewQualificationGateEvaluator.Evaluate(input, DateTimeOffset.UnixEpoch);

        Assert.Equal(
            MauiQualificationMetricProvenanceKinds.SampleSupplied,
            report.Metrics.FalseHeals.Exercises?.Kind);
        // The unknown-source sample is disclosed as scored here, not credited to the run.
        Assert.Contains("Pooled with 301 sample(s) scored by", report.Metrics.FalseHeals.Exercises!.Note);
    }

    [Fact]
    public void Accumulator_RefusesARunWhoseThresholdsDoNotMatchThePublishedPolicy()
    {
        var input = new MauiPreviewQualificationInput { Platform = "android" };
        var report = MauiPreviewQualificationGateEvaluator.Evaluate(input, DateTimeOffset.UnixEpoch);
        // ThresholdsMatch used to omit this field, so a run could declare a laxer stability bar
        // than the one its merged evidence would be published under and still be pooled.
        report.Thresholds.MinimumCleanFirstAttemptsPerTier1Flow = 1;

        var accumulation = MauiPreviewQualificationAccumulator.Accumulate([report], DateTimeOffset.UnixEpoch);

        Assert.Equal(0, accumulation.AcceptedRuns);
        Assert.Contains(
            accumulation.Runs,
            static run => run.ReasonCodes.Contains("accumulate-threshold-not-policy-default"));
    }

    [Fact]
    public void GateEvaluator_FailsAMeasuredFlowEvenWhenAnotherFlowHasNotRun()
    {
        // Pooling the reason codes let one unexercised flow downgrade another flow's measured
        // stability failure to not-qualified, which reports exit 0 on a real regression.
        var input = new MauiPreviewQualificationInput { Platform = "android" };
        input.Tier1Flows.Add("measured");
        input.Tier1Flows.Add("not-yet-run");
        for (var attempt = 0; attempt < 200; attempt++)
        {
            input.Samples.Add(new MauiQualificationExecutionSample
            {
                SampleId = $"measured-{attempt}",
                Source = MauiQualificationSampleSources.DeviceBacked,
                Platform = "android",
                RealDevice = true,
                DeviceEvidenceKind = "physical-device",
                FlowId = "measured",
                FirstAttempt = true,
                CleanState = true,
                Outcome = attempt < 190 ? MauiFlowRunOutcomes.Passed : MauiFlowRunOutcomes.Failed,
            });
        }

        var report = MauiPreviewQualificationGateEvaluator.Evaluate(input, DateTimeOffset.UnixEpoch);
        var gate = Assert.Single(report.Gates, candidate => candidate.GateId == "android-tier1-first-attempts");
        Assert.Equal(MauiPreviewQualificationStates.Fail, gate.Status);
        Assert.Contains("android-first-attempt-stability-below-threshold", gate.ReasonCodes);
    }

    [Fact]
    public void Accumulator_RefusesRunsCarryingAnUnrecognisedSampleSource()
    {
        var honest = MauiPreviewQualificationGateEvaluator.Evaluate(QualifiedInput(), DateTimeOffset.UnixEpoch);
        var smuggled = MauiPreviewQualificationGateEvaluator.Evaluate(QualifiedInput(), DateTimeOffset.UnixEpoch.AddDays(1));
        // A source name the merge does not model would be classified as "not static" and summed as
        // fresh evidence. This check is a spell-checker, not an authenticity check: it rejects the
        // label it does not recognise, and would merge the same 300 samples verbatim if they were
        // labelled `device-backed`. Nothing in a JSON file proves a number came from a device.
        smuggled.Metrics.FalseHeals.SourceCounts.Add(new MauiQualificationRateSourceCount
        {
            Source = "device-backed-lab",
            Numerator = 0,
            Denominator = 300,
            IndependentEvaluations = 300,
        });
        smuggled.Metrics.FalseHeals.Denominator += 300;
        smuggled.Metrics.FalseHeals.IndependentEvaluations += 300;

        var accumulation = MauiPreviewQualificationAccumulator.Accumulate([honest, smuggled], DateTimeOffset.UnixEpoch);
        Assert.Equal(1, accumulation.AcceptedRuns);
        Assert.Contains(
            accumulation.Runs,
            static run => run.ReasonCodes.Contains("accumulate-unknown-sample-source"));
        Assert.Equal(
            honest.Metrics.FalseHeals.IndependentEvaluations,
            accumulation.Metrics["falseHeals"].IndependentEvaluations);
    }

    [Fact]
    public void CorpusRunner_ReportsUndeclaredClonesAndPinsTheCaseContentsInTheFingerprint()
    {
        var scratch = Path.Combine(
            Path.GetDirectoryName(typeof(PreviewQualificationTests).Assembly.Location)!,
            "corpus-clone-tests",
            Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(FindRepositoryRoot(), "tests", "DevFlow", "InspectorCorpus"), scratch);
        try
        {
            var before = RunCorpus(scratch).Summary;
            Assert.Equal(0, before.UndeclaredProjectionCollisions);

            // A copy of a repair-positive seed that claims to be hand-authored is counted as an
            // independent trial by the provenance split. The projection check is what discloses it.
            var seedPath = Directory.GetFiles(Path.Combine(scratch, "cases"), "repair-positive-*.json")
                .OrderBy(static path => path, StringComparer.Ordinal)
                .First();
            var clone = JsonNode.Parse(File.ReadAllText(seedPath))!.AsObject();
            clone["id"] = "repair-positive-undeclared-clone";
            clone["provenance"]!.AsObject()["method"] = "hand-authored";
            clone["provenance"]!.AsObject().Remove("derivedFrom");
            File.WriteAllText(Path.Combine(scratch, "cases", "repair-positive-undeclared-clone.json"), clone.ToJsonString());
            var manifestPath = Path.Combine(scratch, "corpus-manifest.json");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            manifest["cases"]!.AsArray().Add(new JsonObject
            {
                ["id"] = "repair-positive-undeclared-clone",
                ["file"] = "cases/repair-positive-undeclared-clone.json",
                ["kind"] = "repair-positive",
                ["disposition"] = "repair-eligible",
            });
            File.WriteAllText(manifestPath, manifest.ToJsonString());

            var after = RunCorpus(scratch).Summary;
            Assert.Empty(after.Errors);
            Assert.Equal(before.CuratedCases + 1, after.CuratedCases);
            Assert.Equal(before.CuratedDerivedCases, after.CuratedDerivedCases);
            Assert.True(after.UndeclaredProjectionCollisions > 0);
            Assert.True(after.UndeclaredShapeCollisions > 0);

            // Perturbing an evidence-neutral fixture value until the diagnostics differ evades the
            // projection check. The fixture-shape check is what still sees it.
            var evasive = JsonNode.Parse(File.ReadAllText(seedPath))!.AsObject();
            evasive["id"] = "repair-positive-evasive-clone";
            evasive["provenance"]!.AsObject()["method"] = "hand-authored";
            evasive["provenance"]!.AsObject().Remove("derivedFrom");
            evasive["fixture"]!.AsObject()["checkpointMismatches"] = new JsonArray("modal", "orientation");
            File.WriteAllText(Path.Combine(scratch, "cases", "repair-positive-evasive-clone.json"), evasive.ToJsonString());
            manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            manifest["cases"]!.AsArray().Add(new JsonObject
            {
                ["id"] = "repair-positive-evasive-clone",
                ["file"] = "cases/repair-positive-evasive-clone.json",
                ["kind"] = "repair-positive",
                ["disposition"] = "repair-eligible",
            });
            File.WriteAllText(manifestPath, manifest.ToJsonString());
            var evaded = RunCorpus(scratch).Summary;
            Assert.True(evaded.UndeclaredShapeCollisions > after.UndeclaredShapeCollisions);

            // Containment alone was evadable in one edit: add an ignored key *and* delete an
            // optional one, and neither shape contains the other. The distance bound is what still
            // sees that. The shape spans the whole case document, so the deleted key may live
            // under `expect` — this clone is genuinely incomparable to both the seed and the
            // evasive clone under containment, and is caught at distance 2 from the evasive one.
            var incomparable = JsonNode.Parse(File.ReadAllText(seedPath))!.AsObject();
            incomparable["id"] = "repair-positive-incomparable-clone";
            incomparable["provenance"]!.AsObject()["method"] = "hand-authored";
            incomparable["provenance"]!.AsObject().Remove("derivedFrom");
            incomparable["fixture"]!.AsObject()["checkpointMismatches"] = new JsonArray("locale");
            incomparable["fixture"]!.AsObject()["note"] = "restated";
            incomparable["expect"]!.AsObject().Remove("candidateKinds");
            File.WriteAllText(
                Path.Combine(scratch, "cases", "repair-positive-incomparable-clone.json"),
                incomparable.ToJsonString());
            manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            manifest["cases"]!.AsArray().Add(new JsonObject
            {
                ["id"] = "repair-positive-incomparable-clone",
                ["file"] = "cases/repair-positive-incomparable-clone.json",
                ["kind"] = "repair-positive",
                ["disposition"] = "repair-eligible",
            });
            File.WriteAllText(manifestPath, manifest.ToJsonString());
            var incomparableSummary = RunCorpus(scratch).Summary;
            Assert.True(incomparableSummary.UndeclaredShapeCollisions > evaded.UndeclaredShapeCollisions);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public void CorpusRunner_PinsCaseAndSecurityContentsInTheFingerprintWithoutTouchingTheManifest()
    {
        var scratch = Path.Combine(
            Path.GetDirectoryName(typeof(PreviewQualificationTests).Assembly.Location)!,
            "corpus-fingerprint-tests",
            Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(FindRepositoryRoot(), "tests", "DevFlow", "InspectorCorpus"), scratch);
        try
        {
            var manifestPath = Path.Combine(scratch, "corpus-manifest.json");
            var manifestBytes = File.ReadAllBytes(manifestPath);
            var before = RunCorpus(scratch).Summary;

            // A case edit with the manifest byte-for-byte untouched must move the fingerprint, or
            // the accumulator would treat two different corpora as the same static evidence.
            var casePath = Directory.GetFiles(Path.Combine(scratch, "cases"), "*.json")
                .OrderBy(static path => path, StringComparer.Ordinal)
                .First();
            var edited = JsonNode.Parse(File.ReadAllText(casePath))!.AsObject();
            edited["provenance"]!.AsObject()["labeledBy"] = "someone-else";
            File.WriteAllText(casePath, edited.ToJsonString());
            Assert.Equal(manifestBytes, File.ReadAllBytes(manifestPath));

            var afterCaseEdit = RunCorpus(scratch).Summary;
            Assert.NotEqual(before.ManifestFingerprint, afterCaseEdit.ManifestFingerprint);

            // The privacy/security corpus feeds a published gate but lives outside cases/, so the
            // fingerprint has to cover the whole tree, not just the case directory.
            var securityPath = Directory.GetFiles(scratch, "*.json", SearchOption.AllDirectories)
                .First(static path => path.Contains("security", StringComparison.OrdinalIgnoreCase));
            File.AppendAllText(securityPath, " ");
            var afterSecurityEdit = RunCorpus(scratch).Summary;
            Assert.NotEqual(afterCaseEdit.ManifestFingerprint, afterSecurityEdit.ManifestFingerprint);

            // baselines/ holds the report generated *from* this fingerprint. If it counted, no
            // regenerated baseline would ever match the corpus it was generated from.
            var baselineDirectory = Path.Combine(scratch, "baselines");
            Directory.CreateDirectory(baselineDirectory);
            File.WriteAllText(Path.Combine(baselineDirectory, "qualification.json"), "{\"status\":\"rewritten\"}");
            Assert.Equal(afterSecurityEdit.ManifestFingerprint, RunCorpus(scratch).Summary.ManifestFingerprint);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public void CorpusRunner_ReadsTheSameCorpusTheSameWayWhateverTheCheckoutDidToLineEndings()
    {
        // Hashing raw bytes anywhere makes the committed baseline unreproducible: it would pass on
        // the author's CRLF working tree and fail on a clean LF clone, or the reverse.
        var scratch = Path.Combine(
            Path.GetDirectoryName(typeof(PreviewQualificationTests).Assembly.Location)!,
            "corpus-eol-tests",
            Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(FindRepositoryRoot(), "tests", "DevFlow", "InspectorCorpus"), scratch);
        try
        {
            static void Rewrite(string root, Func<string, string> convert)
            {
                foreach (var path in Directory.GetFiles(root, "*.json", SearchOption.AllDirectories))
                    File.WriteAllText(path, convert(File.ReadAllText(path)));
            }

            Rewrite(scratch, static text => text.Replace("\r\n", "\n", StringComparison.Ordinal));
            var lf = RunCorpus(scratch);
            Rewrite(scratch, static text => text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\n", "\r\n", StringComparison.Ordinal));
            var crlf = RunCorpus(scratch);

            Assert.Equal(lf.Summary.ManifestFingerprint, crlf.Summary.ManifestFingerprint);
            Assert.Equal(
                lf.Summary.SecurityCorpus?.ManifestFingerprint,
                crlf.Summary.SecurityCorpus?.ManifestFingerprint);
            Assert.NotNull(lf.Summary.SecurityCorpus?.ManifestFingerprint);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public void CorpusRunner_RefusesACaseThatHidesInTheUnhashedBaselinesDirectory()
    {
        // baselines/ is excluded from the fingerprint. A case evaluated from there would be
        // evidence that can be rewritten without moving the fingerprint the accumulator and the
        // baseline diff both rely on.
        var scratch = Path.Combine(
            Path.GetDirectoryName(typeof(PreviewQualificationTests).Assembly.Location)!,
            "corpus-baseline-case-tests",
            Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(FindRepositoryRoot(), "tests", "DevFlow", "InspectorCorpus"), scratch);
        try
        {
            var manifestPath = Path.Combine(scratch, "corpus-manifest.json");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            var moved = manifest["cases"]!.AsArray()
                .First(node => node!["kind"]!.GetValue<string>() == "no-repair")!.AsObject();
            var sourceFile = moved["file"]!.GetValue<string>();
            Directory.CreateDirectory(Path.Combine(scratch, "baselines"));
            var hiddenName = Path.GetFileName(sourceFile);
            File.Move(
                Path.Combine(scratch, sourceFile.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(scratch, "baselines", hiddenName));
            moved["file"] = "baselines/" + hiddenName;
            File.WriteAllText(manifestPath, manifest.ToJsonString());

            var summary = RunCorpus(scratch).Summary;

            Assert.Contains("corpus-case-path-invalid", summary.Errors);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public void CorpusRunner_RefusesACaseWhoseExtensionOnlyResolvesOnACaseInsensitiveFileSystem()
    {
        // Directory.GetFiles matches case-insensitively on Windows and case-sensitively on Linux.
        // Accepting `cases/x.JSON` from the manifest while the tree hash globbed "*.json" put that
        // case inside the fingerprint on one platform and outside it on the other — the same
        // "change a published number with the fingerprint frozen" hole as hiding under baselines/,
        // reached by a different path. The manifest now refuses the file outright, and the hash
        // enumerates everything so a stray copy still moves the fingerprint on both platforms.
        var scratch = Path.Combine(
            Path.GetDirectoryName(typeof(PreviewQualificationTests).Assembly.Location)!,
            "corpus-case-extension-tests",
            Guid.NewGuid().ToString("N"));
        CopyDirectory(Path.Combine(FindRepositoryRoot(), "tests", "DevFlow", "InspectorCorpus"), scratch);
        try
        {
            var manifestPath = Path.Combine(scratch, "corpus-manifest.json");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
            var renamed = manifest["cases"]!.AsArray()
                .First(node => node!["kind"]!.GetValue<string>() == "no-repair")!.AsObject();
            var sourceFile = renamed["file"]!.GetValue<string>();
            var shoutedFile = sourceFile[..^5] + ".JSON";
            File.Move(
                Path.Combine(scratch, sourceFile.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(scratch, shoutedFile.Replace('/', Path.DirectorySeparatorChar)));
            renamed["file"] = shoutedFile;
            File.WriteAllText(manifestPath, manifest.ToJsonString());

            var summary = RunCorpus(scratch).Summary;

            Assert.Contains("corpus-case-path-invalid", summary.Errors);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException) { /* best effort */ }
        }
    }

    [Fact]
    public void CorpusRunner_CountsTheWholeCaseDocumentAndStopsAtThreeKeyPaths()
    {
        // The counter's shape is the whole case document, not the `fixture` object — so a clone
        // that varies only its provenance notes is caught, and two cases one key path apart are a
        // restatement no matter how different the fixture values read. The bound is also real:
        // three edits escape, which is a disclosed limit rather than a claim the counter does not
        // meet. Both halves are asserted here because the name said "fixture" for long enough that
        // two reviews reasoned about the wrong object.
        //
        // Each scenario gets a pristine corpus so the two clones are only ever measured against
        // the committed cases, never against each other.
        static int CollisionsWithClone(string cloneId, Action<JsonObject> mutate)
        {
            var scratch = Path.Combine(
                Path.GetDirectoryName(typeof(PreviewQualificationTests).Assembly.Location)!,
                "corpus-shape-scope-tests",
                Guid.NewGuid().ToString("N"));
            CopyDirectory(Path.Combine(FindRepositoryRoot(), "tests", "DevFlow", "InspectorCorpus"), scratch);
            try
            {
                var manifestPath = Path.Combine(scratch, "corpus-manifest.json");
                var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
                var seedEntry = manifest["cases"]!.AsArray()
                    .First(node => node!["kind"]!.GetValue<string>() == "no-repair")!.AsObject();
                var seedPath = Path.Combine(
                    scratch,
                    seedEntry["file"]!.GetValue<string>().Replace('/', Path.DirectorySeparatorChar));
                var clone = JsonNode.Parse(File.ReadAllText(seedPath))!.AsObject();
                clone["id"] = cloneId;
                clone["provenance"]!.AsObject()["method"] = "hand-authored";
                clone["provenance"]!.AsObject().Remove("derivedFrom");
                mutate(clone);
                File.WriteAllText(Path.Combine(scratch, "cases", cloneId + ".json"), clone.ToJsonString());
                manifest["cases"]!.AsArray().Add(new JsonObject
                {
                    ["id"] = cloneId,
                    ["file"] = "cases/" + cloneId + ".json",
                    ["kind"] = seedEntry["kind"]!.GetValue<string>(),
                    ["disposition"] = seedEntry["disposition"]!.GetValue<string>(),
                });
                File.WriteAllText(manifestPath, manifest.ToJsonString());
                return RunCorpus(scratch).Summary.UndeclaredShapeCollisions;
            }
            finally
            {
                try { Directory.Delete(scratch, recursive: true); }
                catch (IOException) { /* best effort */ }
            }
        }

        var committed = RunCorpus().Summary.UndeclaredShapeCollisions;

        // One key path apart, and the only added key is outside `fixture` entirely.
        Assert.Equal(
            committed + 1,
            CollisionsWithClone("no-repair-provenance-only-clone", static clone =>
                clone["provenance"]!.AsObject()["notes"] = "restated with a different note"));

        // Three key paths from the seed *and* not a superset of it — adding keys alone never
        // escapes, because containment catches any superset however much is bolted on. Escaping
        // takes an add-and-remove wide enough to clear the bound, which is the disclosed limit.
        Assert.Equal(
            committed,
            CollisionsWithClone("no-repair-three-key-edit", static clone =>
            {
                var fixture = clone["fixture"]!.AsObject();
                fixture.Remove(fixture.Select(static pair => pair.Key).First());
                fixture["firstNewKey"] = "a";
                fixture["secondNewKey"] = "b";
            }));
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

    [Fact]
    public void Corpus_DeclaresThatItScoresRepairWithHarnessRulesRatherThanTheShippedAnalyzer()
    {
        var report = Report(RunCorpus(), DateTimeOffset.UnixEpoch);

        Assert.False(report.Corpus.ExercisesShippedAnalyzer);
        foreach (var metric in new[]
                 {
                     report.Metrics.RepairPrecision,
                     report.Metrics.RepairRecall,
                     report.Metrics.FalseHeals,
                     report.Metrics.Abstention,
                 })
        {
            Assert.Equal(
                MauiQualificationMetricProvenanceKinds.HarnessLocalRules,
                metric.Exercises?.Kind);
            Assert.False(MauiQualificationMetricProvenanceKinds.IsProductEvidence(metric.Exercises?.Kind));
        }

        // The failure class is the one corpus answer that comes from shipped code.
        Assert.Equal(
            MauiQualificationMetricProvenanceKinds.ShippedAnalyzer,
            report.Metrics.ClassificationAccuracy.Exercises?.Kind);

        var gate = report.Gates.Single(entry => entry.GateId == "product-analyzer-coverage");
        Assert.Equal(MauiPreviewQualificationStates.NotQualified, gate.Status);
        Assert.Contains("corpus-does-not-exercise-shipped-analyzer", gate.ReasonCodes);
        Assert.Contains("falseHeals", gate.Message);
        Assert.DoesNotContain("classificationAccuracy", gate.Message);
    }

    [Fact]
    public void CoverageGate_NeverClaimsAnalyzerProvenanceItDidNotVerify()
    {
        // Sample-supplied means "a run said so". The gate may accept it — nothing else in this
        // system verifies a submitted run either — but it must report it as self-reported rather
        // than describing it as the shipped analyzer, and it must leave a machine-readable marker.
        var report = MauiPreviewQualificationGateEvaluator.Evaluate(QualifiedInput(), DateTimeOffset.UnixEpoch);

        var gate = report.Gates.Single(entry => entry.GateId == "product-analyzer-coverage");
        Assert.Equal(MauiPreviewQualificationStates.Pass, gate.Status);
        Assert.Contains("provenance-self-reported", gate.ReasonCodes);
        Assert.Contains("claiming the submitting run observed them", gate.Message);
        Assert.Contains("read rather than observed", gate.Message);
        Assert.DoesNotContain("verified", gate.Message);
        foreach (var name in new[] { "repairPrecision", "repairRecall", "falseHeals", "abstention" })
            Assert.Contains(name, gate.Message);
    }

    [Fact]
    public void CoverageGate_WillNotTakeACorpusSummarysWordThatHarnessRulesAreTheShippedAnalyzer()
    {
        // MauiPreviewQualificationInput.Corpus is caller-supplied. If the repair metrics' declared
        // component were derived from it, a hand-written input could assert that the harness rules
        // are the shipped analyzer — the strongest claim this report can make, on no evidence, and
        // with no reason code attached. The label is a compile-time constant for that reason.
        var input = QualifiedInput();
        input.Corpus!.ExercisesShippedAnalyzer = true;
        input.Samples.Add(new MauiQualificationExecutionSample
        {
            SampleId = "curated-repair",
            Source = MauiQualificationSampleSources.Curated,
            RepairExpected = true,
            RepairProposed = true,
            RepairCorrect = true,
        });

        var report = MauiPreviewQualificationGateEvaluator.Evaluate(input, DateTimeOffset.UnixEpoch);

        Assert.Equal(
            MauiQualificationMetricProvenanceKinds.HarnessLocalRules,
            report.Metrics.RepairPrecision.Exercises?.Kind);
        var gate = report.Gates.Single(entry => entry.GateId == "product-analyzer-coverage");
        Assert.Equal(MauiPreviewQualificationStates.NotQualified, gate.Status);
        Assert.Contains("corpus-does-not-exercise-shipped-analyzer", gate.ReasonCodes);
        Assert.DoesNotContain("Produced by the shipped analyzer", gate.Message);
    }

    [Fact]
    public void CoverageGate_NeverPassesWithoutSayingWhoseWordItIsTaking()
    {
        // Nothing in this gate can watch code run: shipped-analyzer and sample-supplied are both
        // labels it read. An unqualified pass — pass with no reason codes — would be indistinguishable
        // from verification, so there is no such branch, in-process or merged.
        var metrics = new (string, int, MauiQualificationMetricProvenance?)[]
        {
            ("repairPrecision", 10, Claim(MauiQualificationMetricProvenanceKinds.ShippedAnalyzer)),
            ("repairRecall", 10, Claim(MauiQualificationMetricProvenanceKinds.ShippedAnalyzer)),
            ("falseHeals", 10, Claim(MauiQualificationMetricProvenanceKinds.ShippedAnalyzer)),
            ("abstention", 10, Claim(MauiQualificationMetricProvenanceKinds.ShippedAnalyzer)),
            ("classificationAccuracy", 10, Claim(MauiQualificationMetricProvenanceKinds.ShippedAnalyzer)),
        };

        var gate = MauiPreviewQualificationGateEvaluator.BuildProductAnalyzerCoverageGate(metrics);

        Assert.Equal(MauiPreviewQualificationStates.Pass, gate.Status);
        Assert.Contains("provenance-self-reported", gate.ReasonCodes);
        Assert.Contains("read rather than observed", gate.Message);
        Assert.DoesNotContain("verified", gate.Message);

        static MauiQualificationMetricProvenance Claim(string kind) => new()
        {
            Component = "MauiFlowFailureClassifier.Classify",
            Kind = kind,
        };
    }

    [Fact]
    public void CoverageGate_WillNotLetOneMetricSpeakForTheOnesItExistsToDisclose()
    {
        // The gate exists because repair scoring does not reach the product. An input carrying only
        // classificationAccuracy says nothing about that, and absence is not coverage — this is the
        // shape that previously reached pass with an empty reason list.
        var gate = MauiPreviewQualificationGateEvaluator.BuildProductAnalyzerCoverageGate(
        [
            ("repairPrecision", 0, null),
            ("repairRecall", 0, null),
            ("falseHeals", 0, null),
            ("abstention", 0, null),
            ("classificationAccuracy", 1, new MauiQualificationMetricProvenance
            {
                Component = "MauiFlowFailureClassifier.Classify",
                Kind = MauiQualificationMetricProvenanceKinds.ShippedAnalyzer,
            }),
        ]);

        Assert.Equal(MauiPreviewQualificationStates.NotQualified, gate.Status);
        Assert.Contains("product-analyzer-coverage-scope-missing", gate.ReasonCodes);
    }

    [Fact]
    public void Classification_WillNotBorrowTheShippedClassifiersNameForAnUnstampedLabel()
    {
        // observedFailureClass is caller-supplied. Before the producer stamp, a hand-written sample
        // was labelled shipped-analyzer purely for not being device-backed, which let a fabricated
        // input claim the strongest provenance the report can carry.
        var input = new MauiPreviewQualificationInput { Platform = "android" };
        input.Samples.Add(new MauiQualificationExecutionSample
        {
            SampleId = "fabricated",
            Source = MauiQualificationSampleSources.Curated,
            Platform = "android",
            ExpectedFailureClass = MauiFlowFailureClasses.LocatorNotFound,
            ObservedFailureClass = MauiFlowFailureClasses.LocatorNotFound,
            FailureClassInferred = true,
        });

        var report = MauiPreviewQualificationGateEvaluator.Evaluate(input, DateTimeOffset.UnixEpoch);

        Assert.Equal(
            MauiQualificationMetricProvenanceKinds.Unknown,
            report.Metrics.ClassificationAccuracy.Exercises?.Kind);
        Assert.Contains("no producer stamp", report.Metrics.ClassificationAccuracy.Exercises!.Note);
        var gate = report.Gates.Single(entry => entry.GateId == "product-analyzer-coverage");
        Assert.Equal(MauiPreviewQualificationStates.NotQualified, gate.Status);
        Assert.Contains("provenance-unknown", gate.ReasonCodes);
    }

    [Fact]
    public void Classification_KeepsTheShippedClassifierLabelWhenTheRunnerStampedIt()
    {
        // The corpus runner does call MauiFlowFailureClassifier.Classify, so the honest corpus run
        // must keep its label — the stamp is what separates it from the fabricated case above.
        var report = Report(RunCorpus(), DateTimeOffset.UnixEpoch);

        Assert.Equal(
            MauiQualificationMetricProvenanceKinds.ShippedAnalyzer,
            report.Metrics.ClassificationAccuracy.Exercises?.Kind);
    }

    [Fact]
    public void CoverageGate_TreatsAbsentEvidenceAsUnknownRatherThanCoverage()
    {
        // Every other gate in the evaluator answers "nothing was measured" with not-qualified.
        // A pass here would let an empty report advertise analyzer coverage it never had.
        var gate = MauiPreviewQualificationGateEvaluator.BuildProductAnalyzerCoverageGate(
            [("repairPrecision", 0, null), ("falseHeals", 0, null)]);

        Assert.Equal(MauiPreviewQualificationStates.NotQualified, gate.Status);
        Assert.Contains("product-analyzer-coverage-evidence-missing", gate.ReasonCodes);
    }

    [Fact]
    public void CoverageGate_RefusesToTreatAnUndeclaredOrUnknownMetricAsCoverage()
    {
        var gate = MauiPreviewQualificationGateEvaluator.BuildProductAnalyzerCoverageGate(
        [
            ("repairPrecision", 10, new MauiQualificationMetricProvenance
            {
                Component = "submitting-run",
                Kind = MauiQualificationMetricProvenanceKinds.SampleSupplied,
            }),
            ("repairRecall", 10, new MauiQualificationMetricProvenance
            {
                Component = "submitting-run",
                Kind = MauiQualificationMetricProvenanceKinds.SampleSupplied,
            }),
            ("falseHeals", 10, null),
            ("abstention", 10, new MauiQualificationMetricProvenance
            {
                Component = "a + undeclared",
                Kind = MauiQualificationMetricProvenanceKinds.Unknown,
            }),
            ("classificationAccuracy", 10, new MauiQualificationMetricProvenance
            {
                Component = "   ",
                Kind = MauiQualificationMetricProvenanceKinds.ShippedAnalyzer,
            }),
        ]);

        Assert.Equal(MauiPreviewQualificationStates.NotQualified, gate.Status);
        Assert.Contains("provenance-undeclared", gate.ReasonCodes);
        Assert.Contains("provenance-unknown", gate.ReasonCodes);
        Assert.Contains("provenance-component-missing", gate.ReasonCodes);
        // A metric that declared nothing and a metric that declared a kind but no component are
        // different failures, and neither message may be said about the other.
        Assert.Contains("Declared nothing about what produced them: falseHeals", gate.Message);
        Assert.Contains("Named a kind but no component: classificationAccuracy", gate.Message);
        Assert.Contains("disagreed about what produced them: abstention", gate.Message);
    }

    [Fact]
    public void Corpus_DisclosesHarnessScoredSamplesPooledBehindADeviceBackedGateSubset()
    {
        // The judged subset decides the kind, but the published denominator can still hold samples
        // this report scored. Saying only "this report did not score it" would misdescribe them.
        // A generated mutant is exactly that case: counted, never independent, scored here.
        var input = QualifiedInput();
        input.Samples.Add(new MauiQualificationExecutionSample
        {
            SampleId = "pooled-generated-no-repair",
            Source = MauiQualificationSampleSources.Generated,
            NoRepairExpected = true,
            FalseHeal = false,
            Abstained = true,
        });

        var report = MauiPreviewQualificationGateEvaluator.Evaluate(input, DateTimeOffset.UnixEpoch);

        var falseHeals = report.Metrics.FalseHeals;
        Assert.Equal(MauiQualificationMetricProvenanceKinds.SampleSupplied, falseHeals.Exercises?.Kind);
        Assert.Equal(301, falseHeals.SourceCounts.Single(
            static count => count.Source == MauiQualificationSampleSources.Generated).Denominator);
        Assert.Contains("Pooled with 301 sample(s) scored by", falseHeals.Exercises!.Note);
        Assert.Contains("included in the pooled numerator that the false-heal gate", falseHeals.Exercises.Note);
    }

    [Fact]
    public void Corpus_KeepsOneMetricHonestWhenItMixesCuratedAndDeviceEvidence()
    {
        // Both source kinds inside a single metric's gate-carrying subset: the counts sum, but the
        // provenance takes the weaker of the two because pooling must never upgrade what a number
        // measures.
        var input = QualifiedInput();
        input.Samples.Add(new MauiQualificationExecutionSample
        {
            SampleId = "mixed-curated-repair",
            Source = MauiQualificationSampleSources.Curated,
            RepairExpected = true,
            RepairProposed = true,
            RepairCorrect = true,
        });

        var report = MauiPreviewQualificationGateEvaluator.Evaluate(input, DateTimeOffset.UnixEpoch);

        var precision = report.Metrics.RepairPrecision;
        Assert.Equal(101, precision.Denominator);
        Assert.Equal(101, precision.IndependentEvaluations);
        Assert.Equal(
            [MauiQualificationSampleSources.Curated, MauiQualificationSampleSources.DeviceBacked],
            precision.SourceCounts.Select(static count => count.Source));
        Assert.Equal(
            MauiQualificationMetricProvenanceKinds.HarnessLocalRules,
            precision.Exercises?.Kind);
        Assert.Contains("Pooled with run-supplied samples.", precision.Exercises!.Note);
        Assert.Equal(
            MauiPreviewQualificationStates.NotQualified,
            report.Gates.Single(entry => entry.GateId == "product-analyzer-coverage").Status);
    }

    [Fact]
    public void Corpus_PublishesTheBaseFixtureAndSeedCountBehindTheGeneratedDenominator()
    {
        var report = Report(RunCorpus(), DateTimeOffset.UnixEpoch);

        // 300 mutants resampled from a handful of originals under one seed are not 300 trials.
        Assert.Equal(1, report.Corpus.GeneratedSeedCount);
        Assert.NotNull(report.Corpus.GeneratedBaseFixtures);
        Assert.InRange(report.Corpus.GeneratedBaseFixtures!.Value, 1, report.Corpus.CuratedNoRepairCases);
        Assert.True(report.Corpus.GeneratedBaseFixtures < report.Corpus.GeneratedNoRepairCases);
    }

    [Fact]
    public void Corpus_KeepsTheAnalyzerCoverageDisclosureHonestWhenTheRunnerChanges()
    {
        // A tripwire, not a style rule. Scoped to the qualification harness files the disclosure is
        // actually about: scanning the whole assembly would let an unrelated, legitimate call to
        // the analyzer elsewhere permanently disarm it. It asserts equality so both directions
        // fail — an overstatement, and a rewiring that forgets to flip the flag.
        //
        // Two limits are real and worth stating rather than papering over. It only sees this file
        // glob, so wiring the analyzer in from a differently named file would leave it green; and
        // it only sees this call shape, so an indirection through a delegate or reflection would
        // too. Under an equality assert a false negative is the dangerous direction, because it
        // makes the tripwire agree with a `false` declaration instead of tightening it. Stripping
        // comments *and* string literals is what keeps prose on either side from deciding it.
        var testingRoot = Path.Combine(
            FindRepositoryRoot(), "src", "DevFlow", "Microsoft.Maui.DevFlow.Testing");
        var callsAnalyzer = Directory
            .EnumerateFiles(testingRoot, "MauiPreviewQualification*.cs", SearchOption.AllDirectories)
            .Where(static path =>
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .Select(StripCommentsAndLiterals)
            .Any(static text => Regex.IsMatch(text, @"MauiSelectorHealthAnalyzer\s*\.\s*Analyze\s*\("));
        var declared = Report(RunCorpus(), DateTimeOffset.UnixEpoch).Corpus.ExercisesShippedAnalyzer;

        Assert.Equal(callsAnalyzer, declared);
    }

    /// <summary>
    /// Removes string literals first, then comments, so neither a sentence naming the call nor a
    /// literal containing a comment marker can decide the tripwire. Literals go first because a
    /// string such as <c>"//"</c> would otherwise blank the rest of a real line of code.
    /// </summary>
    private static string StripCommentsAndLiterals(string source)
    {
        // Verbatim and raw string literals collapse to an empty literal; interpolated holes go with
        // them, which is fine — the tripwire only cares about executable call sites.
        var withoutRaw = Regex.Replace(source, "\"\"\".*?\"\"\"", "\"\"", RegexOptions.Singleline);
        var withoutVerbatim = Regex.Replace(withoutRaw, "@\"(?:[^\"]|\"\")*\"", "\"\"");
        var withoutStrings = Regex.Replace(withoutVerbatim, @"(?<!')""(?:\\.|[^""\\])*""", "\"\"");
        var withoutChars = Regex.Replace(withoutStrings, @"'(?:\\.|[^'\\])'", "' '");
        var withoutBlocks = Regex.Replace(withoutChars, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return string.Join(
            "\n",
            withoutBlocks.Split('\n').Select(static line =>
            {
                var index = line.IndexOf("//", StringComparison.Ordinal);
                return index >= 0 ? line[..index] : line;
            }));
    }

    [Theory]
    [InlineData("var help = \"call MauiSelectorHealthAnalyzer.Analyze(input) here\";", false)]
    [InlineData("var marker = \"/*\"; MauiSelectorHealthAnalyzer.Analyze(input); var end = \"*/\";", true)]
    [InlineData("var doc = \"https://x\"; MauiSelectorHealthAnalyzer.Analyze(input);", true)]
    [InlineData("// MauiSelectorHealthAnalyzer.Analyze(input);", false)]
    [InlineData("MauiSelectorHealthAnalyzer . Analyze ( input );", true)]
    public void Tripwire_ReadsCodeRatherThanProseInEitherDirection(string source, bool expected)
    {
        // Under an equality assert both mistakes matter: prose that looks like a call would force
        // the disclosure to overstate, and a literal that swallows a real call would let it
        // understate while the test stayed green.
        var actual = Regex.IsMatch(
            StripCommentsAndLiterals(source),
            @"MauiSelectorHealthAnalyzer\s*\.\s*Analyze\s*\(");

        Assert.Equal(expected, actual);
    }

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

        // A run that claims real-device evidence has to say which device produced it; the
        // accumulator refuses unattributed device claims.
        input.Profiles.Add(new MauiQualificationPlatformProfile
        {
            Platform = "android",
            Scope = "tier1",
            DeviceEvidenceKind = "physical-device",
            RealDevice = true,
            DeviceFingerprint = "qualified-device",
        });
        for (var index = 0; index < 100; index++)
        {
            input.Samples.Add(new MauiQualificationExecutionSample
            {
                SampleId = $"repair-{index}",
                // Repair evidence has to come from a real run for the same reason the no-repair
                // denominator does: the static corpus scores repair with rules re-implemented in
                // the harness, so a preview whose precision rests on curated cases has measured
                // the harness agreeing with itself.
                Source = MauiQualificationSampleSources.DeviceBacked,
                Platform = "android",
                DeviceEvidenceKind = "physical-device",
                RealDevice = true,
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
