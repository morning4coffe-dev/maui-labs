using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Microsoft.Maui.DevFlow.Testing;

/// <summary>
/// Deterministically aggregates redacted static and device evidence into preview gate results.
/// It does not contact a device, read a workspace, replay a flow, or apply a proposal.
/// </summary>
public static class MauiPreviewQualificationGateEvaluator
{
    private const string Android = "android";
    private const string TierOne = "tier-1";

    /// <summary>Evaluates all Android engineering-preview gates from explicitly supplied evidence.</summary>
    public static MauiPreviewQualificationReport Evaluate(
        MauiPreviewQualificationInput? input,
        DateTimeOffset? generatedAt = null)
    {
        input ??= new MauiPreviewQualificationInput();
        var validation = MauiPreviewQualificationInputValidator.Validate(input);
        var thresholds = NormalizeThresholds(input.Thresholds ?? new MauiQualificationGateThresholds());
        var samples = (input.Samples ?? [])
            .Where(static sample => sample is not null)
            .ToList();
        var platform = NormalizePlatform(input.Platform);

        var report = new MauiPreviewQualificationReport
        {
            GeneratedAt = generatedAt ?? DateTimeOffset.UtcNow,
            Platform = platform,
            Fingerprints = Sanitize(input.Fingerprints),
            Profiles = (input.Profiles ?? []).Select(Sanitize).ToList(),
            AppleQa = SanitizeAppleQa(input.AppleQa),
            FeatureFlags = Sanitize(input.FeatureFlags),
            Review = Sanitize(input.Review),
            Corpus = Sanitize(input.Corpus),
            Thresholds = thresholds,
            ArtifactRefs = (input.ArtifactRefs ?? []).Select(Sanitize).ToList(),
            Exclusions = BuildExclusions(input),
        };

        foreach (var error in validation.Errors)
            AddReason(report, error.Code, "error", error.Message);

        report.Metrics = BuildMetrics(input, samples, platform, thresholds);
        AddCorpusGate(report);
        AddFeatureFlagGate(report);
        AddReviewGate(report, thresholds);
        AddRequiredEvidenceGate(report, input, samples);
        AddRepairPrecisionGate(report, thresholds);
        AddClassificationAccuracyGate(report, thresholds);
        AddFalseHealGate(report, thresholds);
        AddSelectorStabilityGate(report, thresholds);
        AddCalibrationGate(report, thresholds);
        AddPrivacySecurityGate(report);
        AddHostPerformanceGate(report, thresholds);
        AddDeviceOverheadGate(report);
        AddAndroidFirstAttemptGate(report, platform, thresholds);

        if (validation.Errors.Count > 0)
        {
            report.Gates.Add(new MauiQualificationGateResult
            {
                GateId = "input-contract",
                Status = MauiPreviewQualificationStates.Fail,
                Message = "Qualification input violates the versioned contract.",
                ReasonCodes = validation.Errors.Select(static error => error.Code).Distinct(StringComparer.Ordinal).ToList(),
            });
        }

        report.Status = AggregateStatus(report.Gates);
        foreach (var gate in report.Gates.Where(static gate => gate.Status != MauiPreviewQualificationStates.Pass))
        {
            AddReason(
                report,
                $"gate-{gate.GateId}-{gate.Status}",
                gate.Status == MauiPreviewQualificationStates.Fail ? "error" : "warning",
                gate.Message);
        }
        return report;
    }

    private static MauiQualificationMetrics BuildMetrics(
        MauiPreviewQualificationInput input,
        IReadOnlyList<MauiQualificationExecutionSample> samples,
        string platform,
        MauiQualificationGateThresholds thresholds)
    {
        var included = samples.Where(static sample => sample.Excluded != true).ToList();
        var repair = included
            .Where(static sample => sample.RepairProposed == true && sample.RepairCorrect.HasValue)
            .ToList();
        var repairExpected = included
            .Where(static sample => sample.RepairExpected == true)
            .ToList();
        var noRepair = included
            .Where(static sample => sample.NoRepairExpected == true)
            .ToList();
        var deviceSelector = included
            .Where(sample => IsInPlatformScope(sample, platform) && IsRealDeviceSample(sample) && sample.SelectorStable.HasValue)
            .ToList();
        var recording = included.Where(static sample => sample.RecordingValid.HasValue).ToList();
        var calibration = MauiQualificationStatistics.CalculateCalibration(
            included
                .Where(static sample => sample.ProbabilityLikeConfidence.HasValue && sample.ExpectedOutcome.HasValue)
                .Select(static sample => (sample.ProbabilityLikeConfidence!.Value, sample.ExpectedOutcome!.Value)),
            bucketCount: 10);
        var diagnosis = BuildDurationMetric(
            "time-to-diagnosis",
            included.Where(static sample => sample.TimeToDiagnosisMs is >= 0).Select(static sample => sample.TimeToDiagnosisMs!.Value));
        var trace = BuildTraceReportMetric(included);
        var overhead = SanitizeRuntimeOverhead(input.RuntimeOverhead);
        if (overhead.DeviceOverhead is null)
            overhead.DeviceOverhead = new MauiQualificationDurationMetric();
        if (overhead.DeviceOverhead.State == "missing" && string.IsNullOrWhiteSpace(overhead.DeviceOverhead.MissingReason))
        {
            overhead.DeviceOverhead.MissingReason =
                "No Android pilot artifact supplied device-overhead evidence.";
        }

        var privacy = BuildPrivacySecurityMetric(input, included);
        var firstAttempts = BuildFirstAttemptMetric(input, samples, platform, thresholds);
        var classification = included
            .Where(static sample =>
                MauiFlowFailureClassifier.IsKnownFailureClass(sample.ExpectedFailureClass) &&
                sample.ObservedFailureClass is not null)
            .ToList();

        return new MauiQualificationMetrics
        {
            RecordingValidity = BuildRate(
                recording,
                static sample => sample.RecordingValid == true,
                independentDeviceRuns: recording.Count > 0 && recording.All(IsRealDeviceSample),
                thresholds.ConfidenceLevel),
            RepairPrecision = BuildRate(
                repair,
                static sample => sample.RepairCorrect == true,
                independentDeviceRuns: repair.Count > 0 && repair.All(IsRealDeviceSample),
                thresholds.ConfidenceLevel),
            RepairRecall = BuildRate(
                repairExpected,
                static sample => sample.RepairProposed == true && sample.RepairCorrect == true,
                independentDeviceRuns: repairExpected.Count > 0 && repairExpected.All(IsRealDeviceSample),
                thresholds.ConfidenceLevel),
            FalseHeals = BuildRate(
                noRepair,
                static sample => sample.FalseHeal == true,
                independentDeviceRuns: noRepair.Count > 0 && noRepair.All(IsRealDeviceSample),
                thresholds.ConfidenceLevel),
            Abstention = BuildRate(
                noRepair.Where(static sample => sample.Abstained.HasValue).ToList(),
                static sample => sample.Abstained == true,
                independentDeviceRuns: noRepair.Count > 0 && noRepair.All(IsRealDeviceSample),
                thresholds.ConfidenceLevel),
            SelectorStability = BuildRate(
                deviceSelector,
                static sample => sample.SelectorStable == true,
                independentDeviceRuns: deviceSelector.Count > 0,
                thresholds.ConfidenceLevel),
            ClassificationAccuracy = BuildRate(
                classification,
                static sample => string.Equals(
                    NormalizeFailureClass(sample.ExpectedFailureClass),
                    NormalizeFailureClass(sample.ObservedFailureClass),
                    StringComparison.Ordinal),
                independentDeviceRuns: classification.Count > 0 && classification.All(IsRealDeviceSample),
                thresholds.ConfidenceLevel),
            ClassificationMatrix = BuildClassificationMatrix(classification),
            Calibration = calibration,
            TimeToDiagnosis = diagnosis,
            TraceReportSize = trace,
            RuntimeOverhead = overhead,
            FlakeFirstAttemptStability = firstAttempts,
            HumanDecisionOutcomes = BuildHumanDecisionOutcomes(included),
            PrivacySecurityEscapes = privacy,
        };
    }

    private static MauiQualificationRateMetric BuildRate(
        IReadOnlyList<MauiQualificationExecutionSample> samples,
        Func<MauiQualificationExecutionSample, bool> success,
        bool independentDeviceRuns,
        double confidenceLevel)
    {
        var denominator = samples.Count;
        var numerator = samples.Count(success);
        return new MauiQualificationRateMetric
        {
            State = denominator == 0 ? "missing" : "measured",
            Numerator = numerator,
            Denominator = denominator,
            Value = denominator == 0 ? null : (double)numerator / denominator,
            ConfidenceInterval = denominator == 0
                ? null
                : MauiQualificationStatistics.WilsonInterval(numerator, denominator, confidenceLevel),
            SampleSources = samples
                .Select(static sample => NormalizeSource(sample.Source))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static source => source, StringComparer.Ordinal)
                .ToList(),
            // Per-source counts keep a pooled denominator honest: 0/316 is reported as the
            // curated and generated shares it is actually made of.
            SourceCounts = samples
                .GroupBy(static sample => NormalizeSource(sample.Source), StringComparer.Ordinal)
                .OrderBy(static group => group.Key, StringComparer.Ordinal)
                .Select(group => new MauiQualificationRateSourceCount
                {
                    Source = group.Key,
                    Numerator = group.Count(success),
                    Denominator = group.Count(),
                })
                .ToList(),
            IndependentDeviceRuns = denominator == 0 ? null : independentDeviceRuns,
        };
    }

    /// <summary>
    /// Builds a bounded expected-versus-observed failure-class confusion matrix. Labels are
    /// normalized to the closed failure-class set so free text can never reach the report.
    /// </summary>
    private static MauiQualificationClassificationMatrix BuildClassificationMatrix(
        IReadOnlyList<MauiQualificationExecutionSample> samples)
    {
        if (samples.Count == 0)
        {
            return new MauiQualificationClassificationMatrix
            {
                State = "missing",
                MissingReason = "No sample carried both a ground-truth and an observed failure class.",
            };
        }

        var pairs = samples
            .Select(static sample => (
                Expected: NormalizeFailureClass(sample.ExpectedFailureClass),
                Observed: NormalizeFailureClass(sample.ObservedFailureClass)))
            .ToList();
        var cells = pairs
            .GroupBy(static pair => pair, EqualityComparer<(string Expected, string Observed)>.Default)
            .Select(static group => new MauiQualificationClassificationCell
            {
                Expected = group.Key.Expected,
                Observed = group.Key.Observed,
                Count = group.Count(),
            })
            .OrderBy(static cell => cell.Expected, StringComparer.Ordinal)
            .ThenBy(static cell => cell.Observed, StringComparer.Ordinal)
            .ToList();
        var labels = pairs
            .SelectMany(static pair => new[] { pair.Expected, pair.Observed })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static label => label, StringComparer.Ordinal)
            .ToList();
        var perClass = labels
            .Select(label =>
            {
                var support = pairs.Count(pair => pair.Expected == label);
                var predicted = pairs.Count(pair => pair.Observed == label);
                var correct = pairs.Count(pair => pair.Expected == label && pair.Observed == label);
                return new MauiQualificationClassificationClassResult
                {
                    FailureClass = label,
                    Support = support,
                    Predicted = predicted,
                    Correct = correct,
                    Precision = predicted == 0 ? null : (double)correct / predicted,
                    Recall = support == 0 ? null : (double)correct / support,
                };
            })
            .ToList();

        return new MauiQualificationClassificationMatrix
        {
            State = "measured",
            SampleCount = pairs.Count,
            Correct = pairs.Count(static pair => pair.Expected == pair.Observed),
            LabelCount = labels.Count,
            Cells = cells,
            PerClass = perClass,
        };
    }

    private static MauiQualificationFirstAttemptMetric BuildFirstAttemptMetric(
        MauiPreviewQualificationInput input,
        IReadOnlyList<MauiQualificationExecutionSample> samples,
        string platform,
        MauiQualificationGateThresholds thresholds)
    {
        var result = new MauiQualificationFirstAttemptMetric();
        var ignoredDiagnostics = samples.Count(static sample => sample.DiagnosticRerun == true);
        var firstAttempts = new List<MauiQualificationExecutionSample>();

        foreach (var sample in samples)
        {
            if (sample.DiagnosticRerun == true)
                continue;
            if (sample.Excluded == true)
                continue;
            if (!IsInPlatformScope(sample, platform) || !IsRealDeviceSample(sample) ||
                sample.CleanState != true || sample.FirstAttempt != true)
            {
                continue;
            }

            if (IsInfrastructureOutcome(sample.Outcome) &&
                !string.IsNullOrWhiteSpace(sample.InfrastructureExclusionReason))
            {
                result.InfrastructureExclusions.Add(new MauiQualificationExclusion
                {
                    Kind = "infrastructure-first-attempt",
                    Count = 1,
                    Reason = "recorded-deterministic-infrastructure-reason",
                });
                continue;
            }

            firstAttempts.Add(sample);
        }

        var declaredFlows = (input.Tier1Flows ?? [])
            .Where(static flow => !string.IsNullOrWhiteSpace(flow))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static flow => flow, StringComparer.Ordinal)
            .ToList();
        foreach (var flow in declaredFlows)
        {
            var flowSamples = firstAttempts
                .Where(sample => string.Equals(sample.FlowId, flow, StringComparison.Ordinal))
                .ToList();
            var passed = flowSamples.Count(static sample =>
                string.Equals(sample.Outcome, MauiFlowRunOutcomes.Passed, StringComparison.Ordinal));
            result.Flows.Add(new MauiQualificationFlowAttemptSummary
            {
                FlowId = MauiQualificationSanitizer.Fingerprint(flow),
                CleanFirstAttempts = flowSamples.Count,
                PassedFirstAttempts = passed,
                Stability = flowSamples.Count == 0 ? null : (double)passed / flowSamples.Count,
                RealDeviceEvidence = flowSamples.Count > 0,
            });
        }

        result.DiagnosticRerunsIgnored = ignoredDiagnostics;
        result.Stability = BuildRate(
            firstAttempts,
            static sample => string.Equals(sample.Outcome, MauiFlowRunOutcomes.Passed, StringComparison.Ordinal),
            independentDeviceRuns: firstAttempts.Count > 0,
            thresholds.ConfidenceLevel);
        result.State = firstAttempts.Count == 0 ? "missing" : "measured";
        return result;
    }

    private static MauiQualificationHumanDecisionOutcomes BuildHumanDecisionOutcomes(
        IEnumerable<MauiQualificationExecutionSample> samples)
    {
        var result = new MauiQualificationHumanDecisionOutcomes();
        foreach (var decision in samples.Select(static sample => sample.HumanDecision))
        {
            switch (decision?.Trim().ToLowerInvariant())
            {
                case "approved":
                    result.Approved++;
                    break;
                case "rejected":
                    result.Rejected++;
                    break;
                case "expired":
                    result.Expired++;
                    break;
                case "abstained":
                    result.Abstained++;
                    break;
                case null:
                case "":
                    break;
                default:
                    result.Unresolved++;
                    break;
            }
        }
        return result;
    }

    private static MauiQualificationDurationMetric BuildDurationMetric(string operation, IEnumerable<double> values)
    {
        var sorted = values.Where(static value => !double.IsNaN(value) && !double.IsInfinity(value)).OrderBy(static value => value).ToArray();
        if (sorted.Length == 0)
        {
            return new MauiQualificationDurationMetric
            {
                Operation = operation,
                MissingReason = "No recorded samples were supplied.",
            };
        }

        return new MauiQualificationDurationMetric
        {
            State = "measured",
            Operation = operation,
            SampleCount = sorted.Length,
            P50Ms = MauiQualificationStatistics.Percentile(sorted, 0.50),
            P95Ms = MauiQualificationStatistics.Percentile(sorted, 0.95),
            MaxMs = sorted[^1],
        };
    }

    private static MauiQualificationTraceReportMetric BuildTraceReportMetric(
        IReadOnlyList<MauiQualificationExecutionSample> samples)
    {
        var reports = samples.Where(static sample => sample.ReportPresent == true).ToList();
        var expected = samples.Count(static sample =>
            NormalizeSource(sample.Source) == MauiQualificationSampleSources.DeviceBacked &&
            sample.DiagnosticRerun != true);
        var reportSizes = reports.Where(static sample => sample.ReportBytes is >= 0).Select(static sample => (double)sample.ReportBytes!.Value).OrderBy(static value => value).ToArray();
        var traceSizes = samples.Where(static sample => sample.TraceBytes is >= 0).Select(static sample => (double)sample.TraceBytes!.Value).OrderBy(static value => value).ToArray();
        return new MauiQualificationTraceReportMetric
        {
            State = reports.Count == 0 ? "missing" : "measured",
            ExpectedReportCount = expected,
            ReportPresent = reports.Count,
            ReportSchemaValid = reports.Count(static sample => sample.ReportSchemaValid == true),
            ReportComplete = reports.Count(static sample => sample.ReportComplete == true),
            ReportCompleteness = expected == 0 ? null : reports.Count(static sample => sample.ReportComplete == true) / (double)expected,
            TraceSampleCount = traceSizes.Length,
            ReportP50Bytes = reportSizes.Length == 0 ? null : MauiQualificationStatistics.Percentile(reportSizes, 0.50),
            ReportP95Bytes = reportSizes.Length == 0 ? null : MauiQualificationStatistics.Percentile(reportSizes, 0.95),
            TraceP50Bytes = traceSizes.Length == 0 ? null : MauiQualificationStatistics.Percentile(traceSizes, 0.50),
            TraceP95Bytes = traceSizes.Length == 0 ? null : MauiQualificationStatistics.Percentile(traceSizes, 0.95),
            MissingReason = reports.Count == 0 ? "No flow report size or completeness evidence was supplied." : null,
        };
    }

    private static MauiQualificationPrivacySecurityMetric BuildPrivacySecurityMetric(
        MauiPreviewQualificationInput input,
        IReadOnlyList<MauiQualificationExecutionSample> samples)
    {
        var supplied = input.PrivacySecurity;
        var corpus = input.Corpus?.SecurityCorpus;
        var escapes = samples.Count(static sample => sample.PrivacySecurityEscape == true) + (supplied?.EscapeCount ?? 0);
        var count = Math.Max(
            samples.Count(static sample => sample.PrivacySecurityEscape.HasValue),
            Math.Max(supplied?.TestCount ?? 0, corpus?.CaseCount ?? 0));
        var validCorpus = corpus?.Valid;
        return new MauiQualificationPrivacySecurityMetric
        {
            State = count == 0 || validCorpus == false ? "missing" : "measured",
            TestCount = count,
            EscapeCount = escapes,
            CanaryScanPassed = escapes == 0 && (supplied?.CanaryScanPassed ?? validCorpus ?? false),
            CaseIds = (supplied?.CaseIds ?? corpus?.CaseIds ?? [])
                .Select(MauiQualificationSanitizer.Fingerprint)
                .Where(static value => value is not null)
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            MissingReason = count == 0
                ? "No security/privacy adversarial corpus evidence was supplied."
                : validCorpus == false
                    ? "The security/privacy corpus was invalid."
                    : null,
        };
    }

    private static void AddCorpusGate(MauiPreviewQualificationReport report)
    {
        var valid = report.Corpus.ManifestValid == true && report.Corpus.CaseSchemaValid == true;
        var status = valid
            ? MauiPreviewQualificationStates.Pass
            : report.Corpus.ManifestValid == false || report.Corpus.CaseSchemaValid == false
                ? MauiPreviewQualificationStates.Fail
                : MauiPreviewQualificationStates.NotQualified;
        report.Gates.Add(new MauiQualificationGateResult
        {
            GateId = "corpus-contract",
            Status = status,
            Message = status == MauiPreviewQualificationStates.Pass
                ? "The deterministic corpus manifest and case schemas are valid."
                : "The deterministic corpus manifest and case-schema evidence is incomplete or invalid.",
            ReasonCodes = status == MauiPreviewQualificationStates.Pass
                ? []
                : ["corpus-manifest-or-schema-missing"],
        });
    }

    private static void AddFeatureFlagGate(MauiPreviewQualificationReport report)
    {
        var flags = report.FeatureFlags;
        var unsafeFlags = new List<string>();
        if (flags.AutoApplyRepair) unsafeFlags.Add("auto-apply-repair-enabled");
        if (flags.AutoApplySource) unsafeFlags.Add("auto-apply-source-enabled");
        if (flags.ModelProviderEnabled) unsafeFlags.Add("model-provider-enabled");
        if (flags.TelemetryEgressEnabled) unsafeFlags.Add("telemetry-egress-enabled");
        if (flags.RequiredPullRequestGate) unsafeFlags.Add("required-pr-gate-enabled");

        report.Gates.Add(new MauiQualificationGateResult
        {
            GateId = "preview-safety-flags",
            Status = unsafeFlags.Count == 0 ? MauiPreviewQualificationStates.Pass : MauiPreviewQualificationStates.Fail,
            Message = unsafeFlags.Count == 0
                ? "Preview flags retain proposal-only, no-provider, no-egress, advisory behavior."
                : "Unsafe preview flags contradict the engineering-preview contract.",
            ReasonCodes = unsafeFlags,
        });
    }

    private static void AddReviewGate(MauiPreviewQualificationReport report, MauiQualificationGateThresholds thresholds)
    {
        if (!thresholds.RequireRecordedReviews)
        {
            report.Gates.Add(new MauiQualificationGateResult
            {
                GateId = "independent-review",
                Status = MauiPreviewQualificationStates.Pass,
                Message = "Recorded review is disabled by the explicitly versioned policy.",
            });
            return;
        }

        var review = report.Review;
        var missing = new List<string>();
        if (!IsApproved(review.PlanReviewStatus)) missing.Add("plan-review-status-missing-or-not-approved");
        if (!IsApproved(review.RubberDuckReviewStatus)) missing.Add("rubber-duck-review-status-missing-or-not-approved");
        if (!IsApproved(review.IndependentReviewStatus)) missing.Add("independent-review-status-missing-or-not-approved");

        report.Gates.Add(new MauiQualificationGateResult
        {
            GateId = "independent-review",
            Status = missing.Count == 0 ? MauiPreviewQualificationStates.Pass : MauiPreviewQualificationStates.NotQualified,
            Message = missing.Count == 0
                ? "Plan, rubber-duck, and independent review statuses are recorded as approved."
                : "Recorded approved plan, rubber-duck, and independent review evidence is required.",
            ReasonCodes = missing,
        });
    }

    private static void AddRequiredEvidenceGate(
        MauiPreviewQualificationReport report,
        MauiPreviewQualificationInput input,
        IReadOnlyList<MauiQualificationExecutionSample> samples)
    {
        var supplied = input.Evidence;
        var inferredReportSchema = samples.Where(static sample => sample.ReportPresent == true).ToList();
        var facts = new (string Code, bool? Value)[]
        {
            ("corpus-manifest-evidence-missing", supplied?.CorpusManifestValid ?? report.Corpus.ManifestValid),
            ("case-schema-evidence-missing", supplied?.CaseSchemaValid ?? report.Corpus.CaseSchemaValid),
            ("report-schema-evidence-missing", supplied?.ReportSchemaValid ??
                (inferredReportSchema.Count == 0 ? null : inferredReportSchema.All(static sample => sample.ReportSchemaValid == true))),
            ("recording-validity-evidence-missing", supplied?.RecordingValid ??
                (samples.Any(static sample => sample.RecordingValid.HasValue)
                    ? samples.Where(static sample => sample.RecordingValid.HasValue).All(static sample => sample.RecordingValid == true)
                    : null)),
            ("first-attempt-evidence-missing", supplied?.FirstAttemptEvidencePresent ??
                (samples.Any(static sample => sample.FirstAttempt == true) ? true : null)),
            ("artifact-manifest-evidence-missing", supplied?.ArtifactManifestValid),
            ("artifact-reference-evidence-missing", supplied?.ArtifactReferencesComplete),
        };
        var failed = facts.Where(static fact => fact.Value == false).Select(static fact => fact.Code).ToList();
        var missing = facts.Where(static fact => !fact.Value.HasValue).Select(static fact => fact.Code).ToList();
        var status = failed.Count > 0
            ? MauiPreviewQualificationStates.Fail
            : missing.Count > 0
                ? MauiPreviewQualificationStates.NotQualified
                : MauiPreviewQualificationStates.Pass;
        report.Gates.Add(new MauiQualificationGateResult
        {
            GateId = "required-evidence",
            Status = status,
            Message = status == MauiPreviewQualificationStates.Pass
                ? "Required corpus, report, recording, first-attempt, and artifact evidence is present."
                : "Required report/schema/first-attempt/artifact evidence is missing or invalid.",
            ReasonCodes = [.. failed, .. missing],
        });
    }

    private static void AddRepairPrecisionGate(MauiPreviewQualificationReport report, MauiQualificationGateThresholds thresholds)
    {
        var metric = report.Metrics.RepairPrecision;
        var lower = metric.ConfidenceInterval?.Lower;
        var status = metric.Denominator < thresholds.MinimumRepairEvaluations
            ? MauiPreviewQualificationStates.NotQualified
            : lower >= thresholds.MinimumRepairPrecision
                ? MauiPreviewQualificationStates.Pass
                : MauiPreviewQualificationStates.Fail;
        report.Gates.Add(new MauiQualificationGateResult
        {
            GateId = "repair-precision",
            Status = status,
            Message = status == MauiPreviewQualificationStates.Pass
                ? "Repair precision meets the conservative Wilson lower-bound threshold."
                : "Repair precision lacks enough evaluations or misses the conservative lower-bound threshold.",
            ReasonCodes = status == MauiPreviewQualificationStates.Pass
                ? []
                : metric.Denominator < thresholds.MinimumRepairEvaluations
                    ? ["repair-evaluation-count-insufficient"]
                    : ["repair-precision-lower-bound-below-threshold"],
        });
    }

    private static void AddFalseHealGate(MauiPreviewQualificationReport report, MauiQualificationGateThresholds thresholds)
    {
        var metric = report.Metrics.FalseHeals;
        var status = metric.Denominator < thresholds.MinimumNoRepairEvaluations
            ? MauiPreviewQualificationStates.NotQualified
            : metric.Numerator <= thresholds.MaximumFalseHeals
                ? MauiPreviewQualificationStates.Pass
                : MauiPreviewQualificationStates.Fail;
        report.Gates.Add(new MauiQualificationGateResult
        {
            GateId = "zero-false-heals",
            Status = status,
            Message = status == MauiPreviewQualificationStates.Pass
                ? "No false heal was observed across the required no-repair denominator."
                : "No-repair evidence is insufficient or includes a false heal.",
            ReasonCodes = status == MauiPreviewQualificationStates.Pass
                ? []
                : metric.Denominator < thresholds.MinimumNoRepairEvaluations
                    ? ["no-repair-evaluation-count-insufficient"]
                    : ["false-heal-observed"],
        });
    }

    private static void AddClassificationAccuracyGate(
        MauiPreviewQualificationReport report,
        MauiQualificationGateThresholds thresholds)
    {
        var metric = report.Metrics.ClassificationAccuracy;
        var lower = metric.ConfidenceInterval?.Lower;
        var status = metric.Denominator < thresholds.MinimumClassificationEvaluations
            ? MauiPreviewQualificationStates.NotQualified
            : lower >= thresholds.MinimumClassificationAccuracy
                ? MauiPreviewQualificationStates.Pass
                : MauiPreviewQualificationStates.Fail;
        report.Gates.Add(new MauiQualificationGateResult
        {
            GateId = "classification-accuracy",
            Status = status,
            Message = status == MauiPreviewQualificationStates.Pass
                ? "Failure-class accuracy meets the conservative Wilson lower-bound threshold."
                : "Failure-class accuracy lacks enough labeled evaluations or misses the conservative lower-bound threshold.",
            ReasonCodes = status == MauiPreviewQualificationStates.Pass
                ? []
                : metric.Denominator < thresholds.MinimumClassificationEvaluations
                    ? ["classification-evaluation-count-insufficient"]
                    : ["classification-accuracy-lower-bound-below-threshold"],
        });
    }

    private static void AddSelectorStabilityGate(MauiPreviewQualificationReport report, MauiQualificationGateThresholds thresholds)
    {
        var metric = report.Metrics.SelectorStability;
        var status = metric.Denominator < thresholds.MinimumSelectorObservations
            ? MauiPreviewQualificationStates.NotQualified
            : metric.Value >= thresholds.MinimumSelectorStability
                ? MauiPreviewQualificationStates.Pass
                : MauiPreviewQualificationStates.Fail;
        report.Gates.Add(new MauiQualificationGateResult
        {
            GateId = "selector-stability",
            Status = status,
            Message = status == MauiPreviewQualificationStates.Pass
                ? "Selector stability meets the declared platform-scope threshold."
                : "Declared-platform real-device selector-stability evidence is insufficient or below threshold.",
            ReasonCodes = status == MauiPreviewQualificationStates.Pass
                ? []
                : metric.Denominator < thresholds.MinimumSelectorObservations
                    ? ["selector-stability-device-evidence-insufficient"]
                    : ["selector-stability-below-threshold"],
        });
    }

    private static void AddCalibrationGate(MauiPreviewQualificationReport report, MauiQualificationGateThresholds thresholds)
    {
        var metric = report.Metrics.Calibration;
        var status = !metric.ProbabilityLikeConfidenceDisplayed
            ? MauiPreviewQualificationStates.Pass
            : metric.Ece is null
                ? MauiPreviewQualificationStates.NotQualified
                : metric.Ece <= thresholds.MaximumCalibrationEce
                    ? MauiPreviewQualificationStates.Pass
                    : MauiPreviewQualificationStates.Fail;
        report.Gates.Add(new MauiQualificationGateResult
        {
            GateId = "confidence-calibration",
            Status = status,
            Message = status == MauiPreviewQualificationStates.Pass
                ? metric.ProbabilityLikeConfidenceDisplayed
                    ? "Probability-like confidence meets the ECE threshold."
                    : "No probability-like confidence is displayed; calibration is not applicable."
                : "Probability-like confidence lacks calibration evidence or exceeds the ECE threshold.",
            ReasonCodes = status == MauiPreviewQualificationStates.Pass
                ? []
                : metric.Ece is null
                    ? ["calibration-evidence-missing"]
                    : ["calibration-ece-above-threshold"],
        });
    }

    private static void AddPrivacySecurityGate(MauiPreviewQualificationReport report)
    {
        var metric = report.Metrics.PrivacySecurityEscapes;
        var status = metric.EscapeCount > 0 || metric.CanaryScanPassed == false
            ? MauiPreviewQualificationStates.Fail
            : metric.TestCount == 0 || metric.CanaryScanPassed is null
                ? MauiPreviewQualificationStates.NotQualified
                : MauiPreviewQualificationStates.Pass;
        report.Gates.Add(new MauiQualificationGateResult
        {
            GateId = "privacy-security-escapes",
            Status = status,
            Message = status == MauiPreviewQualificationStates.Pass
                ? "No privacy or security escape was observed by the adversarial corpus."
                : "Privacy/security evidence is missing or an escape was observed.",
            ReasonCodes = status == MauiPreviewQualificationStates.Pass
                ? []
                : status == MauiPreviewQualificationStates.Fail
                    ? ["privacy-or-security-escape-observed"]
                    : ["privacy-security-corpus-evidence-missing"],
        });
    }

    private static void AddHostPerformanceGate(MauiPreviewQualificationReport report, MauiQualificationGateThresholds thresholds)
    {
        var operations = report.Metrics.RuntimeOverhead.HostOperations ?? [];
        var measured = operations.Where(static operation => operation.State == "measured" && operation.P95Ms.HasValue).ToList();
        var regression = measured.Where(operation => operation.P95Ms > thresholds.HostOperationP95BudgetMs).ToList();
        var status = measured.Count == 0
            ? MauiPreviewQualificationStates.NotQualified
            : regression.Count > 0
                ? MauiPreviewQualificationStates.Fail
                : MauiPreviewQualificationStates.Pass;
        report.Gates.Add(new MauiQualificationGateResult
        {
            GateId = "deterministic-host-performance",
            Status = status,
            Message = status == MauiPreviewQualificationStates.Pass
                ? "Measured deterministic host operations remain within the p95 budget."
                : "Deterministic host performance evidence is missing or exceeds its p95 budget.",
            ReasonCodes = status == MauiPreviewQualificationStates.Pass
                ? []
                : regression.Count > 0
                    ? ["host-operation-p95-budget-exceeded"]
                    : ["host-performance-evidence-missing"],
        });
    }

    private static void AddDeviceOverheadGate(MauiPreviewQualificationReport report)
    {
        var metric = report.Metrics.RuntimeOverhead.DeviceOverhead;
        var status = metric?.State == "measured"
            ? MauiPreviewQualificationStates.Pass
            : MauiPreviewQualificationStates.NotQualified;
        report.Gates.Add(new MauiQualificationGateResult
        {
            GateId = "android-device-overhead",
            Status = status,
            Message = status == MauiPreviewQualificationStates.Pass
                ? "Android device-overhead evidence is present."
                : "Android device-overhead evidence remains missing until real pilot artifacts are supplied.",
            ReasonCodes = status == MauiPreviewQualificationStates.Pass ? [] : ["android-device-overhead-evidence-missing"],
        });
    }

    private static void AddAndroidFirstAttemptGate(
        MauiPreviewQualificationReport report,
        string platform,
        MauiQualificationGateThresholds thresholds)
    {
        if (!string.Equals(platform, Android, StringComparison.Ordinal))
        {
            report.Gates.Add(new MauiQualificationGateResult
            {
                GateId = "android-tier1-first-attempts",
                Status = MauiPreviewQualificationStates.NotQualified,
                Message = "This engineering-preview policy requires an explicit Android platform scope.",
                ReasonCodes = ["android-platform-scope-missing"],
            });
            return;
        }

        var metric = report.Metrics.FlakeFirstAttemptStability;
        var reasons = new List<string>();
        if (metric.Flows.Count == 0)
            reasons.Add("tier1-flow-declaration-missing");
        foreach (var flow in metric.Flows)
        {
            if (!flow.RealDeviceEvidence)
                reasons.Add("android-real-device-evidence-missing");
            if (flow.CleanFirstAttempts < thresholds.MinimumCleanFirstAttemptsPerTier1Flow)
                reasons.Add("android-clean-first-attempt-count-insufficient");
            if (flow.Stability < thresholds.MinimumFirstAttemptStability)
                reasons.Add("android-first-attempt-stability-below-threshold");
        }

        var status = reasons.Count == 0
            ? MauiPreviewQualificationStates.Pass
            : reasons.Any(static code => code == "android-first-attempt-stability-below-threshold") &&
              metric.Flows.All(flow => flow.CleanFirstAttempts >= thresholds.MinimumCleanFirstAttemptsPerTier1Flow)
                ? MauiPreviewQualificationStates.Fail
                : MauiPreviewQualificationStates.NotQualified;
        report.Gates.Add(new MauiQualificationGateResult
        {
            GateId = "android-tier1-first-attempts",
            Status = status,
            Message = status == MauiPreviewQualificationStates.Pass
                ? "Every declared Tier-1 flow has enough clean real-device first attempts at the stability threshold."
                : "Android qualification requires at least 100 clean real-device first attempts per declared Tier-1 flow.",
            ReasonCodes = reasons.Distinct(StringComparer.Ordinal).ToList(),
        });
    }

    private static MauiQualificationGateThresholds NormalizeThresholds(MauiQualificationGateThresholds source)
    {
        return new MauiQualificationGateThresholds
        {
            PolicyVersion = MauiQualificationSanitizer.FingerprintOrUnknown(source.PolicyVersion),
            ConfidenceLevel = source.ConfidenceLevel is <= 0 or >= 1 ? 0.95 : source.ConfidenceLevel,
            MinimumRepairPrecision = Math.Clamp(source.MinimumRepairPrecision, 0, 1),
            MinimumRepairEvaluations = Math.Max(1, source.MinimumRepairEvaluations),
            MinimumNoRepairEvaluations = Math.Max(1, source.MinimumNoRepairEvaluations),
            MaximumFalseHeals = Math.Max(0, source.MaximumFalseHeals),
            MinimumSelectorStability = Math.Clamp(source.MinimumSelectorStability, 0, 1),
            MinimumSelectorObservations = Math.Max(1, source.MinimumSelectorObservations),
            MinimumClassificationAccuracy = Math.Clamp(source.MinimumClassificationAccuracy, 0, 1),
            MinimumClassificationEvaluations = Math.Max(1, source.MinimumClassificationEvaluations),
            MaximumCalibrationEce = Math.Clamp(source.MaximumCalibrationEce, 0, 1),
            MinimumCleanFirstAttemptsPerTier1Flow = Math.Max(1, source.MinimumCleanFirstAttemptsPerTier1Flow),
            MinimumFirstAttemptStability = Math.Clamp(source.MinimumFirstAttemptStability, 0, 1),
            HostOperationP95BudgetMs = Math.Max(1, source.HostOperationP95BudgetMs),
            RequireRealAndroidDeviceEvidence = source.RequireRealAndroidDeviceEvidence,
            RequireRecordedReviews = source.RequireRecordedReviews,
        };
    }

    private static bool IsRealDeviceSample(MauiQualificationExecutionSample sample) =>
        string.Equals(NormalizeSource(sample.Source), MauiQualificationSampleSources.DeviceBacked, StringComparison.Ordinal) &&
        sample.RealDevice == true &&
        sample.DeviceEvidenceKind is "physical-device" or "real-device";

    private static bool IsInPlatformScope(MauiQualificationExecutionSample sample, string platform) =>
        string.Equals(NormalizePlatform(sample.Platform), platform, StringComparison.Ordinal);

    private static bool IsInfrastructureOutcome(string? outcome) =>
        string.Equals(outcome, MauiFlowRunOutcomes.InfrastructureError, StringComparison.Ordinal);

    private static bool IsApproved(string? status) =>
        status is not null &&
        (string.Equals(status, "approved", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(status, "passed", StringComparison.OrdinalIgnoreCase));

    private static string NormalizePlatform(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "android" => "android",
            "ios" => "ios",
            "maccatalyst" => "maccatalyst",
            "macos" => "macos",
            "windows" => "windows",
            "wpf" => "wpf",
            "gtk" => "gtk",
            _ => "unknown",
        };

    private static string NormalizeSource(string? value) =>
        MauiQualificationSampleSources.IsKnown(value) ? value! : "unknown";

    private static string NormalizeFailureClass(string? value) =>
        MauiFlowFailureClassifier.IsKnownFailureClass(value) ? value! : "unknown";

    private static string AggregateStatus(IEnumerable<MauiQualificationGateResult> gates)
    {
        if (gates.Any(static gate => gate.Status == MauiPreviewQualificationStates.Fail))
            return MauiPreviewQualificationStates.Fail;
        if (gates.Any(static gate => gate.Status != MauiPreviewQualificationStates.Pass))
            return MauiPreviewQualificationStates.NotQualified;
        return MauiPreviewQualificationStates.Pass;
    }

    private static MauiQualificationFingerprints Sanitize(MauiQualificationFingerprints? source) => new()
    {
        CorpusVersion = MauiQualificationSanitizer.FingerprintOrUnknown(source?.CorpusVersion),
        CorpusFingerprint = MauiQualificationSanitizer.FingerprintOrUnknown(source?.CorpusFingerprint),
        RepositoryCommit = MauiQualificationSanitizer.FingerprintOrUnknown(source?.RepositoryCommit),
        TestingPackageVersion = MauiQualificationSanitizer.FingerprintOrUnknown(source?.TestingPackageVersion),
        PackageId = MauiQualificationSanitizer.FingerprintOrUnknown(source?.PackageId),
        PackageFingerprint = MauiQualificationSanitizer.FingerprintOrUnknown(source?.PackageFingerprint),
        ToolVersion = MauiQualificationSanitizer.FingerprintOrUnknown(source?.ToolVersion),
        ToolFingerprint = MauiQualificationSanitizer.FingerprintOrUnknown(source?.ToolFingerprint),
        PolicyVersion = MauiQualificationSanitizer.FingerprintOrUnknown(source?.PolicyVersion),
        PolicyFingerprint = MauiQualificationSanitizer.FingerprintOrUnknown(source?.PolicyFingerprint),
    };

    private static MauiQualificationPlatformProfile Sanitize(MauiQualificationPlatformProfile source) => new()
    {
        Platform = NormalizePlatform(source.Platform),
        Scope = MauiQualificationSanitizer.FingerprintOrUnknown(source.Scope),
        DeviceEvidenceKind = source.DeviceEvidenceKind is
            "physical-device" or "real-device" or "emulator" or "simulator" or "desktop-host" or "none"
                ? source.DeviceEvidenceKind
                : "unknown",
        RealDevice = source.RealDevice,
        DeviceFingerprint = MauiQualificationSanitizer.FingerprintOrUnknown(source.DeviceFingerprint),
        RuntimeFingerprint = MauiQualificationSanitizer.FingerprintOrUnknown(source.RuntimeFingerprint),
        BuildFingerprint = MauiQualificationSanitizer.FingerprintOrUnknown(source.BuildFingerprint),
        PackageFingerprint = MauiQualificationSanitizer.FingerprintOrUnknown(source.PackageFingerprint),
        SeedFingerprint = MauiQualificationSanitizer.FingerprintOrUnknown(source.SeedFingerprint),
        BackendStateFingerprint = MauiQualificationSanitizer.FingerprintOrUnknown(source.BackendStateFingerprint),
        FirstAttemptMode = MauiQualificationSanitizer.FingerprintOrUnknown(source.FirstAttemptMode),
    };

    private static MauiQualificationAppleQaEvidence? SanitizeAppleQa(MauiQualificationAppleQaEvidence? source)
    {
        if (source is null)
            return null;

        return new MauiQualificationAppleQaEvidence
        {
            ContractVersion = string.Equals(
                source.ContractVersion,
                MauiAppleFlowQaManifestReader.AdapterContractVersion,
                StringComparison.Ordinal)
                    ? MauiAppleFlowQaManifestReader.AdapterContractVersion
                    : "unknown",
            Platform = NormalizePlatform(source.Platform),
            Experimental = source.Experimental,
            Backend = source.Backend is "appkit" ? "appkit" : source.Backend is null ? null : "unknown",
            OfficialCoverage = source.OfficialCoverage,
            MacCatalystEquivalent = source.MacCatalystEquivalent,
            SpikeStatus = source.SpikeStatus is
                "proved" or "not-proved" or "pending-spike" or "proof-incomplete" ? source.SpikeStatus : "unknown",
            ForegroundProof = source.ForegroundProof,
            AuthenticatedTransport = source.AuthenticatedTransport,
            Receipt = source.Receipt,
            Cancellation = source.Cancellation,
            Parity = source.Parity,
            AppProject = MauiQualificationSanitizer.FingerprintOrUnknown(source.AppProject),
            AppSourceDigest = MauiQualificationSanitizer.FingerprintOrUnknown(source.AppSourceDigest),
            PackageDigest = MauiQualificationSanitizer.FingerprintOrUnknown(source.PackageDigest),
            FlowDigests = (source.FlowDigests ?? [])
                .Take(128)
                .Select(MauiQualificationSanitizer.Fingerprint)
                .Where(static value => value is not null)
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            FirstAttemptCount = Math.Clamp(source.FirstAttemptCount, 0, 4_096),
            CleanAttemptCount = Math.Clamp(source.CleanAttemptCount, 0, 4_096),
            ArtifactCount = Math.Clamp(source.ArtifactCount, 0, 4_096),
            OmissionCount = Math.Clamp(source.OmissionCount, 0, 4_096),
            XcodeVersion = MauiQualificationSanitizer.FingerprintOrUnknown(source.XcodeVersion),
            SimulatorRuntime = MauiQualificationSanitizer.FingerprintOrUnknown(source.SimulatorRuntime),
            DeviceIdFingerprint = MauiQualificationSanitizer.FingerprintOrUnknown(source.DeviceIdFingerprint),
            DeviceProfile = MauiQualificationSanitizer.FingerprintOrUnknown(source.DeviceProfile),
            ResetFingerprint = MauiQualificationSanitizer.FingerprintOrUnknown(source.ResetFingerprint),
            SeedFingerprint = MauiQualificationSanitizer.FingerprintOrUnknown(source.SeedFingerprint),
            BackendStateFingerprint = MauiQualificationSanitizer.FingerprintOrUnknown(source.BackendStateFingerprint),
        };
    }

    private static MauiQualificationReviewEvidence Sanitize(MauiQualificationReviewEvidence? source) => new()
    {
        PlanId = MauiQualificationSanitizer.FingerprintOrUnknown(source?.PlanId),
        PlanRevision = source?.PlanRevision,
        PlanReviewStatus = NormalizeReviewStatus(source?.PlanReviewStatus) ?? "missing",
        RubberDuckReviewStatus = NormalizeReviewStatus(source?.RubberDuckReviewStatus) ?? "missing",
        IndependentReviewStatus = NormalizeReviewStatus(source?.IndependentReviewStatus) ?? "missing",
        ReviewedAt = source?.ReviewedAt,
        ReviewerFingerprints = (source?.ReviewerFingerprints ?? [])
            .Select(MauiQualificationSanitizer.Fingerprint)
            .Where(static value => value is not null)
            .Cast<string>()
            .ToList(),
        ArtifactRefs = (source?.ArtifactRefs ?? [])
            .Select(MauiQualificationSanitizer.Fingerprint)
            .Where(static value => value is not null)
            .Cast<string>()
            .ToList(),
    };

    private static MauiPreviewFeatureFlags Sanitize(MauiPreviewFeatureFlags? source) => new()
    {
        Schema = 1,
        PolicyVersion = "preview-flags-v1",
        WorkbenchEnabled = source?.WorkbenchEnabled == true,
        AgentAuthoringEnabled = source?.AgentAuthoringEnabled == true,
        RepairProposalsEnabled = source?.RepairProposalsEnabled == true,
        SourceProposalsEnabled = source?.SourceProposalsEnabled == true,
        TraceImportExportEnabled = source?.TraceImportExportEnabled == true,
        AutoApplyRepair = source?.AutoApplyRepair == true,
        AutoApplySource = source?.AutoApplySource == true,
        ModelProviderEnabled = source?.ModelProviderEnabled == true,
        TelemetryEgressEnabled = source?.TelemetryEgressEnabled == true,
        RequiredPullRequestGate = source?.RequiredPullRequestGate == true,
        KillSwitches = (source?.KillSwitches ?? [])
            .Where(static value => value is "workbench" or "agent-authoring" or "repair-proposals" or "source-proposals" or "trace-import-export")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToList(),
    };

    private static MauiQualificationCorpusSummary Sanitize(MauiQualificationCorpusSummary? source) => new()
    {
        Version = MauiQualificationSanitizer.FingerprintOrUnknown(source?.Version),
        ManifestFingerprint = MauiQualificationSanitizer.FingerprintOrUnknown(source?.ManifestFingerprint),
        StaticOnly = source?.StaticOnly,
        ManifestValid = source?.ManifestValid,
        CaseSchemaValid = source?.CaseSchemaValid,
        CuratedCases = Math.Max(0, source?.CuratedCases ?? 0),
        GeneratedCases = Math.Max(0, source?.GeneratedCases ?? 0),
        DeviceBackedCases = Math.Max(0, source?.DeviceBackedCases ?? 0),
        CuratedRepairPositiveCases = Math.Max(0, source?.CuratedRepairPositiveCases ?? 0),
        CuratedNoRepairCases = Math.Max(0, source?.CuratedNoRepairCases ?? 0),
        GeneratedNoRepairCases = Math.Max(0, source?.GeneratedNoRepairCases ?? 0),
        CuratedClassificationLabeledCases = Math.Max(0, source?.CuratedClassificationLabeledCases ?? 0),
        ProvenanceComplete = source?.ProvenanceComplete,
        ProvenanceSourceCounts = (source?.ProvenanceSourceCounts ?? [])
            .Where(static item => item is not null)
            .GroupBy(
                static item => MauiQualificationCorpusProvenanceSourceKinds.Normalize(item.SourceKind),
                StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(static group => new MauiQualificationCorpusProvenanceCount
            {
                SourceKind = group.Key,
                Count = group.Sum(static item => Math.Max(0, item.Count)),
            })
            .ToList(),
        MutationSeed = source?.MutationSeed,
        GeneratorVersion = MauiQualificationSanitizer.FingerprintOrUnknown(source?.GeneratorVersion),
        Errors = (source?.Errors ?? []).Select(static _ => "corpus-validation-error").Distinct(StringComparer.Ordinal).ToList(),
        SecurityCorpus = source?.SecurityCorpus is null ? null : new MauiQualificationSecurityCorpusSummary
        {
            Version = MauiQualificationSanitizer.FingerprintOrUnknown(source.SecurityCorpus.Version),
            ManifestFingerprint = MauiQualificationSanitizer.FingerprintOrUnknown(source.SecurityCorpus.ManifestFingerprint),
            Valid = source.SecurityCorpus.Valid,
            CaseCount = Math.Max(0, source.SecurityCorpus.CaseCount),
            PassedCount = Math.Max(0, source.SecurityCorpus.PassedCount),
            CaseIds = source.SecurityCorpus.CaseIds.Select(MauiQualificationSanitizer.Fingerprint).Where(static value => value is not null).Cast<string>().ToList(),
            Errors = source.SecurityCorpus.Errors.Select(static _ => "security-corpus-validation-error").Distinct(StringComparer.Ordinal).ToList(),
        },
    };

    private static MauiQualificationRuntimeOverheadMetric SanitizeRuntimeOverhead(
        MauiQualificationRuntimeOverheadMetric? source)
    {
        var host = (source?.HostOperations ?? [])
            .Take(32)
            .Select(SanitizeDuration)
            .ToList();
        return new MauiQualificationRuntimeOverheadMetric
        {
            HostOperations = host,
            DeviceOverhead = SanitizeDuration(source?.DeviceOverhead),
        };
    }

    private static MauiQualificationDurationMetric SanitizeDuration(MauiQualificationDurationMetric? source)
    {
        var state = source?.State == "measured" ? "measured" : "missing";
        return new MauiQualificationDurationMetric
        {
            State = state,
            Operation = MauiQualificationSanitizer.SafeOperation(source?.Operation),
            SampleCount = Math.Clamp(source?.SampleCount ?? 0, 0, 1_000_000),
            P50Ms = SafeNonNegative(source?.P50Ms),
            P95Ms = SafeNonNegative(source?.P95Ms),
            MaxMs = SafeNonNegative(source?.MaxMs),
            MissingReason = state == "missing" ? "measurement-evidence-missing" : null,
        };
    }

    private static double? SafeNonNegative(double? value) =>
        value.HasValue && double.IsFinite(value.Value) && value.Value >= 0 ? value : null;

    private static MauiQualificationArtifactReference Sanitize(MauiQualificationArtifactReference source) => new()
    {
        Kind = MauiQualificationSanitizer.SafeKind(source.Kind),
        Digest = MauiQualificationSanitizer.FingerprintOrUnknown(source.Digest),
        Reference = MauiQualificationSanitizer.FingerprintOrUnknown(source.Reference),
        Redacted = source.Redacted ?? true,
    };

    private static List<MauiQualificationExclusion> BuildExclusions(MauiPreviewQualificationInput input)
    {
        var result = BuildDeclaredExclusions(input.Exclusions);
        var excludedSamples = input.Samples?.Count(static sample => sample?.Excluded == true) ?? 0;
        if (excludedSamples > 0)
        {
            result.Add(new MauiQualificationExclusion
            {
                Kind = "excluded-sample",
                Count = excludedSamples,
                Reason = "explicit-sample-exclusion",
            });
        }
        return result;
    }

    private static List<MauiQualificationExclusion> BuildDeclaredExclusions(
        IEnumerable<MauiQualificationExclusion>? exclusions) =>
        (exclusions ?? [])
            .Where(static exclusion => exclusion is not null)
            .Select(static exclusion => new MauiQualificationExclusion
            {
                Kind = MauiQualificationSanitizer.SafeKind(exclusion.Kind),
                Count = Math.Max(0, exclusion.Count),
                Reason = "declared-exclusion",
            })
            .ToList();

    private static string? NormalizeReviewStatus(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "approved" => "approved",
            "passed" => "passed",
            "rejected" => "rejected",
            "pending" => "pending",
            _ => null,
        };

    private static void AddReason(MauiPreviewQualificationReport report, string code, string severity, string message)
    {
        if (report.Reasons.Any(reason => string.Equals(reason.Code, code, StringComparison.Ordinal)))
            return;
        report.Reasons.Add(new MauiQualificationReason { Code = code, Severity = severity, Message = message });
    }
}

/// <summary>Statistical helpers with fixed, documented calculations for release evidence.</summary>
public static class MauiQualificationStatistics
{
    private const double Z95 = 1.959963984540054;

    /// <summary>
    /// Returns a two-sided Wilson score interval. The qualification gate compares its lower bound
    /// with precision thresholds; callers must not substitute a point estimate.
    /// </summary>
    public static MauiQualificationConfidenceInterval WilsonInterval(int successes, int trials, double confidenceLevel = 0.95)
    {
        if (trials <= 0)
            throw new ArgumentOutOfRangeException(nameof(trials));
        if (successes < 0 || successes > trials)
            throw new ArgumentOutOfRangeException(nameof(successes));

        // The release contract intentionally fixes 95% Wilson intervals. Retaining the requested
        // value in output makes a malformed caller visible while avoiding an unreviewed inverse-CDF implementation.
        var z = Z95;
        var p = (double)successes / trials;
        var z2 = z * z;
        var denominator = 1 + z2 / trials;
        var center = (p + z2 / (2 * trials)) / denominator;
        var margin = z * Math.Sqrt((p * (1 - p) / trials) + (z2 / (4d * trials * trials))) / denominator;
        return new MauiQualificationConfidenceInterval
        {
            ConfidenceLevel = confidenceLevel,
            Lower = Math.Clamp(center - margin, 0, 1),
            Upper = Math.Clamp(center + margin, 0, 1),
        };
    }

    /// <summary>Calculates deterministic linear-interpolated percentiles over an already bounded sample set.</summary>
    public static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        ArgumentNullException.ThrowIfNull(sortedValues);
        if (sortedValues.Count == 0)
            throw new ArgumentException("At least one value is required.", nameof(sortedValues));
        var bounded = Math.Clamp(percentile, 0, 1);
        var index = (sortedValues.Count - 1) * bounded;
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        if (lower == upper)
            return sortedValues[lower];
        return sortedValues[lower] + ((sortedValues[upper] - sortedValues[lower]) * (index - lower));
    }

    /// <summary>Calculates ECE and Brier score over ten equal-width buckets by default.</summary>
    public static MauiQualificationCalibrationMetric CalculateCalibration(
        IEnumerable<(double Confidence, bool Outcome)> samples,
        int bucketCount = 10)
    {
        ArgumentNullException.ThrowIfNull(samples);
        bucketCount = Math.Clamp(bucketCount, 1, 100);
        var values = samples
            .Where(static sample => !double.IsNaN(sample.Confidence) && !double.IsInfinity(sample.Confidence))
            .Select(static sample => (Confidence: Math.Clamp(sample.Confidence, 0, 1), sample.Outcome))
            .ToArray();
        if (values.Length == 0)
            return new MauiQualificationCalibrationMetric();

        var buckets = Enumerable.Range(0, bucketCount)
            .Select(index => new List<(double Confidence, bool Outcome)>())
            .ToArray();
        foreach (var sample in values)
        {
            var index = Math.Min(bucketCount - 1, (int)(sample.Confidence * bucketCount));
            buckets[index].Add(sample);
        }

        var result = new MauiQualificationCalibrationMetric
        {
            State = "measured",
            ProbabilityLikeConfidenceDisplayed = true,
            SampleCount = values.Length,
            Brier = values.Average(static sample =>
            {
                var outcome = sample.Outcome ? 1d : 0d;
                return Math.Pow(sample.Confidence - outcome, 2);
            }),
        };
        var ece = 0d;
        for (var index = 0; index < bucketCount; index++)
        {
            var bucket = buckets[index];
            var lower = (double)index / bucketCount;
            var upper = (double)(index + 1) / bucketCount;
            var meanConfidence = bucket.Count == 0 ? (double?)null : bucket.Average(static sample => sample.Confidence);
            var empiricalRate = bucket.Count == 0 ? (double?)null : bucket.Count(static sample => sample.Outcome) / (double)bucket.Count;
            if (meanConfidence.HasValue && empiricalRate.HasValue)
                ece += bucket.Count / (double)values.Length * Math.Abs(meanConfidence.Value - empiricalRate.Value);
            result.Buckets.Add(new MauiQualificationCalibrationBucket
            {
                LowerInclusive = lower,
                UpperInclusive = upper,
                SampleCount = bucket.Count,
                MeanConfidence = meanConfidence,
                EmpiricalRate = empiricalRate,
            });
        }
        result.Ece = ece;
        return result;
    }
}

/// <summary>Validates untrusted qualification evidence without retaining its raw contents.</summary>
public static class MauiPreviewQualificationInputValidator
{
    /// <summary>Validates bounds and known sample origins before evaluation.</summary>
    public static MauiPreviewQualificationInputValidation Validate(MauiPreviewQualificationInput? input)
    {
        var result = new MauiPreviewQualificationInputValidation();
        if (input is null)
        {
            result.Errors.Add(new("input-missing", "Qualification input is required."));
            return result;
        }
        if (input.Schema != 1)
            result.Errors.Add(new("input-schema-invalid", "Qualification input schema must be 1."));
        foreach (var sample in input.Samples ?? [])
        {
            if (sample is null)
            {
                result.Errors.Add(new("sample-null", "Qualification samples cannot contain null entries."));
                continue;
            }
            if (!MauiQualificationSampleSources.IsKnown(sample.Source))
                result.Errors.Add(new("sample-source-invalid", "Qualification samples require a known source."));
            if (sample.ProbabilityLikeConfidence is < 0 or > 1)
                result.Errors.Add(new("sample-confidence-invalid", "Probability-like confidence must be between zero and one."));
            if (sample.TimeToDiagnosisMs is < 0 || sample.TraceBytes is < 0 || sample.ReportBytes is < 0 || sample.RuntimeOverheadMs is < 0)
                result.Errors.Add(new("sample-measurement-invalid", "Qualification measurements cannot be negative."));
        }
        return result;
    }

    /// <summary>Parses a bounded input JSON document while returning safe fixed diagnostics.</summary>
    public static MauiPreviewQualificationInputParseResult ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > 1_048_576)
            return new MauiPreviewQualificationInputParseResult { ErrorCode = "qualification-input-missing-or-too-large" };
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return new MauiPreviewQualificationInputParseResult { ErrorCode = "qualification-input-not-object" };
            var input = JsonSerializer.Deserialize(json, MauiTestingJsonContext.Default.MauiPreviewQualificationInput);
            var validation = Validate(input);
            return new MauiPreviewQualificationInputParseResult
            {
                Input = input,
                ErrorCode = validation.Errors.Count == 0 ? null : validation.Errors[0].Code,
            };
        }
        catch (JsonException)
        {
            return new MauiPreviewQualificationInputParseResult { ErrorCode = "qualification-input-invalid-json" };
        }
    }
}

/// <summary>Small schema-shaped validation result for qualification reports without a schema runtime dependency.</summary>
public sealed class MauiPreviewQualificationReportValidation
{
    public List<string> Errors { get; } = [];
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Validates required v1 report fields before a host publishes a qualification artifact.</summary>
public static class MauiPreviewQualificationReportValidator
{
    /// <summary>Checks the stable report envelope and safety invariants.</summary>
    public static MauiPreviewQualificationReportValidation Validate(MauiPreviewQualificationReport? report)
    {
        var result = new MauiPreviewQualificationReportValidation();
        if (report is null)
        {
            result.Errors.Add("report-required");
            return result;
        }
        if (report.Schema != 1) result.Errors.Add("schema-must-be-1");
        if (!string.Equals(report.Kind, "maui-preview-qualification", StringComparison.Ordinal))
            result.Errors.Add("kind-invalid");
        if (!string.Equals(report.ContractVersion, "preview-qualification-v1", StringComparison.Ordinal))
            result.Errors.Add("contract-version-invalid");
        if (report.GeneratedAt == default) result.Errors.Add("generated-at-required");
        if (report.Status is not MauiPreviewQualificationStates.Pass and not MauiPreviewQualificationStates.Fail and not MauiPreviewQualificationStates.NotQualified)
            result.Errors.Add("status-invalid");
        if (string.IsNullOrWhiteSpace(report.Platform)) result.Errors.Add("platform-required");
        if (report.Fingerprints is null) result.Errors.Add("fingerprints-required");
        if (report.Profiles is null) result.Errors.Add("profiles-required");
        if (report.FeatureFlags is null) result.Errors.Add("feature-flags-required");
        if (report.Review is null) result.Errors.Add("review-required");
        if (report.Corpus is null) result.Errors.Add("corpus-required");
        if (report.Metrics is null) result.Errors.Add("metrics-required");
        if (report.Thresholds is null) result.Errors.Add("thresholds-required");
        if (report.Gates is null) result.Errors.Add("gates-required");
        if (report.Reasons is null) result.Errors.Add("reasons-required");
        if (report.ArtifactRefs is null) result.Errors.Add("artifact-refs-required");
        if (report.Exclusions is null) result.Errors.Add("exclusions-required");
        if (report.FeatureFlags is { } flags &&
            (flags.AutoApplyRepair || flags.AutoApplySource ||
             flags.ModelProviderEnabled || flags.TelemetryEgressEnabled))
        {
            result.Errors.Add("preview-safety-flags-invalid");
        }
        return result;
    }
}

/// <summary>Bounded qualification-input validation findings.</summary>
public sealed class MauiPreviewQualificationInputValidation
{
    public List<MauiPreviewQualificationInputValidationError> Errors { get; } = [];
}

/// <summary>A safe fixed-text qualification-input validation finding.</summary>
public sealed record MauiPreviewQualificationInputValidationError(string Code, string Message);

/// <summary>Parse result for a qualification evidence input document.</summary>
public sealed class MauiPreviewQualificationInputParseResult
{
    public MauiPreviewQualificationInput? Input { get; init; }
    public string? ErrorCode { get; init; }
    public bool Ok => Input is not null && ErrorCode is null;
}

/// <summary>Shared redaction helpers for qualification reports and artifact ingestion.</summary>
public static class MauiQualificationSanitizer
{
    /// <summary>Returns a SHA-256 fingerprint rather than copying caller-provided text into a report.</summary>
    public static string? Fingerprint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) &&
            value.Length == 71 &&
            value.AsSpan(7).ToString().All(Uri.IsHexDigit))
        {
            return "sha256:" + value[7..].ToLowerInvariant();
        }
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    /// <summary>Returns a non-secret placeholder when a required fingerprint fact was not supplied.</summary>
    public static string FingerprintOrUnknown(string? value) => Fingerprint(value) ?? "unknown";

    /// <summary>Allows only a bounded kind token to appear in a report.</summary>
    public static string SafeKind(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "flow-pilot-manifest" => "flow-pilot-manifest",
            "apple-flow-qa-manifest" => "apple-flow-qa-manifest",
            "flow-digest" => "flow-digest",
            "flow-run-report" => "flow-run-report",
            "mauitrace" => "mauitrace",
            "test-results" => "test-results",
            "host-diagnostic" => "host-diagnostic",
            "json" => "json",
            "package-digest" => "package-digest",
            "app-digest" => "app-digest",
            "failure-evidence" => "failure-evidence",
            "diagnostic-rerun" => "diagnostic-rerun",
            "prerequisite" => "prerequisite",
            "host-platform" => "host-platform",
            "android-host-diagnostics" => "android-host-diagnostics",
            "fixture-initialization-diagnostic" => "fixture-initialization-diagnostic",
            "android-fixture-initialization" => "android-fixture-initialization",
            "qualification-report" => "qualification-report",
            "infrastructure-first-attempt" => "infrastructure-first-attempt",
            "excluded-sample" => "excluded-sample",
            "report" => "report",
            "evidence" => "evidence",
            "audit" => "audit",
            "model-projection" => "model-projection",
            "artifact" => "artifact",
            _ => "unknown",
        };
    }

    /// <summary>Allows only fixed operation labels in a report; external labels become <c>unknown</c>.</summary>
    public static string SafeOperation(string? value) => value switch
    {
        "flow-markdown-parse" => "flow-markdown-parse",
        "flow-validate" => "flow-validate",
        "report-serialize-redaction" => "report-serialize-redaction",
        "fingerprint" => "fingerprint",
        "candidate-generation" => "candidate-generation",
        "candidate-ranking" => "candidate-ranking",
        "qualification-gate" => "qualification-gate",
        "time-to-diagnosis" => "time-to-diagnosis",
        "android-device-overhead" => "android-device-overhead",
        "parse" => "parse",
        _ => "unknown",
    };
}

/// <summary>Transition policy for proposal/grant fuzzing. It never auto-applies a proposal.</summary>
public static class MauiQualificationProposalTransitionPolicy
{
    /// <summary>Returns a fail-closed transition result without performing an apply operation.</summary>
    public static MauiQualificationProposalTransitionResult Evaluate(MauiQualificationProposalTransition? transition)
    {
        if (transition?.ApplyRequested == true)
        {
            return new MauiQualificationProposalTransitionResult
            {
                Allowed = false,
                AutomaticApplyAllowed = false,
                ReasonCode = "automatic-apply-prohibited",
            };
        }
        if (transition?.HumanApprovalRecorded != true || transition.GrantValid != true)
        {
            return new MauiQualificationProposalTransitionResult
            {
                Allowed = false,
                AutomaticApplyAllowed = false,
                ReasonCode = "human-approval-or-grant-missing",
            };
        }
        return new MauiQualificationProposalTransitionResult
        {
            Allowed = true,
            AutomaticApplyAllowed = false,
            ReasonCode = "host-mediated-review-only",
        };
    }
}
