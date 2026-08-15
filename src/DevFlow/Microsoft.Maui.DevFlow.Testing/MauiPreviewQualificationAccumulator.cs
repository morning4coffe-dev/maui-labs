using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Microsoft.Maui.DevFlow.Testing;

/// <summary>Contract version for accumulated qualification evidence.</summary>
public static class MauiQualificationAccumulationContract
{
    public const string Kind = "maui-preview-qualification-accumulation";
    public const string ContractVersion = "preview-qualification-accumulation-v1";
}

/// <summary>One qualification run considered for accumulation.</summary>
public sealed class MauiQualificationAccumulatedRun
{
    [JsonPropertyName("runFingerprint")] public string RunFingerprint { get; set; } = string.Empty;
    [JsonPropertyName("generatedAt")] public DateTimeOffset GeneratedAt { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = MauiPreviewQualificationStates.NotQualified;
    [JsonPropertyName("accepted")] public bool Accepted { get; set; }
    [JsonPropertyName("reasonCodes")] public List<string> ReasonCodes { get; set; } = [];
}

/// <summary>Merged qualification evidence across independent runs.</summary>
public sealed class MauiQualificationAccumulation
{
    [JsonPropertyName("schema")] public int Schema { get; set; } = 1;
    [JsonPropertyName("kind")] public string Kind { get; set; } = MauiQualificationAccumulationContract.Kind;
    [JsonPropertyName("contractVersion")] public string ContractVersion { get; set; } = MauiQualificationAccumulationContract.ContractVersion;
    [JsonPropertyName("generatedAt")] public DateTimeOffset GeneratedAt { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = MauiPreviewQualificationStates.NotQualified;
    [JsonPropertyName("platform")] public string? Platform { get; set; }
    [JsonPropertyName("policyVersion")] public string? PolicyVersion { get; set; }
    [JsonPropertyName("corpusFingerprint")] public string? CorpusFingerprint { get; set; }
    [JsonPropertyName("consideredRuns")] public int ConsideredRuns { get; set; }
    [JsonPropertyName("acceptedRuns")] public int AcceptedRuns { get; set; }
    [JsonPropertyName("rejectedRuns")] public int RejectedRuns { get; set; }
    [JsonPropertyName("distinctEvidenceRuns")] public int DistinctEvidenceRuns { get; set; }
    [JsonPropertyName("runs")] public List<MauiQualificationAccumulatedRun> Runs { get; set; } = [];
    [JsonPropertyName("metrics")] public Dictionary<string, MauiQualificationRateMetric> Metrics { get; set; } = [];
    [JsonPropertyName("thresholds")] public MauiQualificationGateThresholds Thresholds { get; set; } = new();
    [JsonPropertyName("gates")] public List<MauiQualificationGateResult> Gates { get; set; } = [];
}

/// <summary>
/// Merges <c>metrics.*.numerator</c> and <c>metrics.*.denominator</c> across independent
/// qualification runs so a gate that needs many trials can be satisfied by many separate jobs,
/// machines, and days rather than by one long in-process loop.
/// </summary>
/// <remarks>
/// This is deliberately fail-closed. A run is rejected unless it agrees with the accumulation on
/// contract version, platform, policy version, corpus fingerprint, and every threshold that a
/// merged gate is judged against. Runs are also deduplicated by an evidence fingerprint that
/// excludes wall-clock time, so re-running the identical static corpus can never manufacture
/// additional independent trials.
/// </remarks>
public static class MauiPreviewQualificationAccumulator
{
    /// <summary>Rate metrics that merge additively across runs.</summary>
    public static readonly string[] MergedMetricNames =
    [
        "recordingValidity",
        "selectorStability",
        "repairPrecision",
        "repairRecall",
        "falseHeals",
        "abstention",
        "classificationAccuracy",
    ];

    public static MauiQualificationAccumulation Accumulate(
        IEnumerable<MauiPreviewQualificationReport> reports,
        DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(reports);
        var ordered = reports
            .Where(static report => report is not null)
            .OrderBy(static report => report.GeneratedAt)
            .ToList();
        var accumulation = new MauiQualificationAccumulation
        {
            GeneratedAt = generatedAt,
            ConsideredRuns = ordered.Count,
        };
        if (ordered.Count == 0)
        {
            accumulation.Thresholds = new MauiQualificationGateThresholds();
            AddGates(accumulation);
            accumulation.Status = MauiPreviewQualificationStates.NotQualified;
            return accumulation;
        }

        var reference = ordered[0];
        accumulation.Platform = reference.Platform;
        accumulation.PolicyVersion = reference.Thresholds.PolicyVersion;
        accumulation.CorpusFingerprint = reference.Fingerprints.CorpusFingerprint;
        accumulation.Thresholds = reference.Thresholds;

        var accepted = new List<MauiPreviewQualificationReport>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var report in ordered)
        {
            var fingerprint = ComputeEvidenceFingerprint(report);
            var reasons = Incompatibilities(reference, report);
            if (!seen.Add(fingerprint))
                reasons.Add("accumulate-duplicate-run");
            var entry = new MauiQualificationAccumulatedRun
            {
                RunFingerprint = fingerprint,
                GeneratedAt = report.GeneratedAt,
                Status = report.Status,
                Accepted = reasons.Count == 0,
                ReasonCodes = reasons,
            };
            accumulation.Runs.Add(entry);
            if (entry.Accepted)
                accepted.Add(report);
        }

        accumulation.AcceptedRuns = accepted.Count;
        accumulation.RejectedRuns = accumulation.ConsideredRuns - accepted.Count;
        accumulation.DistinctEvidenceRuns = accumulation.Runs
            .Where(static run => run.Accepted)
            .Select(static run => run.RunFingerprint)
            .Distinct(StringComparer.Ordinal)
            .Count();

        foreach (var name in MergedMetricNames)
            accumulation.Metrics[name] = MergeRate(accepted.Select(report => Select(report.Metrics, name)));

        AddGates(accumulation);
        accumulation.Status = accumulation.Gates.Any(static gate => gate.Status == MauiPreviewQualificationStates.Fail)
            ? MauiPreviewQualificationStates.Fail
            : accumulation.Gates.All(static gate => gate.Status == MauiPreviewQualificationStates.Pass)
                ? MauiPreviewQualificationStates.Pass
                : MauiPreviewQualificationStates.NotQualified;
        return accumulation;
    }

    /// <summary>Reads every qualification report in a directory, newest last.</summary>
    public static List<MauiPreviewQualificationReport> ReadDirectory(string directory, out List<string> errors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        errors = [];
        var reports = new List<MauiPreviewQualificationReport>();
        if (!Directory.Exists(directory))
            return reports;
        foreach (var file in Directory.GetFiles(directory, "run-*.json").OrderBy(static path => path, StringComparer.Ordinal))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.Length > 4_194_304)
                {
                    errors.Add("accumulate-run-file-too-large");
                    continue;
                }
                var parsed = JsonSerializer.Deserialize(
                    File.ReadAllText(file),
                    MauiTestingJsonContext.Default.MauiPreviewQualificationReport);
                if (parsed is null)
                {
                    errors.Add("accumulate-run-file-unreadable");
                    continue;
                }
                reports.Add(parsed);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
            {
                errors.Add("accumulate-run-file-unreadable");
            }
        }
        return reports;
    }

    /// <summary>
    /// Fingerprints the trial-bearing evidence of a run. Wall-clock time, host timing
    /// measurements, and size distributions are excluded on purpose: two runs that observed the
    /// same trials are one observation, and timing jitter must not be able to disguise a repeat
    /// as a fresh independent run.
    /// </summary>
    public static string ComputeEvidenceFingerprint(MauiPreviewQualificationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var payload = new StringBuilder()
            .Append(report.ContractVersion).Append('\n')
            .Append(report.Platform).Append('\n')
            .Append(report.Status).Append('\n')
            .Append(JsonSerializer.Serialize(report.Fingerprints, MauiTestingJsonContext.Default.MauiQualificationFingerprints)).Append('\n')
            .Append(JsonSerializer.Serialize(report.Profiles, MauiTestingJsonContext.Default.ListMauiQualificationPlatformProfile)).Append('\n')
            .Append(JsonSerializer.Serialize(report.Corpus, MauiTestingJsonContext.Default.MauiQualificationCorpusSummary)).Append('\n');
        foreach (var name in MergedMetricNames)
            AppendRate(payload, name, Select(report.Metrics, name));
        foreach (var flow in report.Metrics.FlakeFirstAttemptStability.Flows.OrderBy(static flow => flow.FlowId, StringComparer.Ordinal))
        {
            payload
                .Append("flow|").Append(flow.FlowId).Append('|')
                .Append(flow.CleanFirstAttempts).Append('|')
                .Append(flow.PassedFirstAttempts).Append('|')
                .Append(flow.Stability).Append('\n');
        }
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString())));
    }

    private static void AppendRate(StringBuilder builder, string name, MauiQualificationRateMetric metric)
    {
        builder
            .Append(name).Append('|')
            .Append(metric.Numerator).Append('/').Append(metric.Denominator).Append('|')
            .Append(metric.IndependentDeviceRuns);
        foreach (var count in (metric.SourceCounts ?? []).OrderBy(static count => count.Source, StringComparer.Ordinal))
            builder.Append('|').Append(count.Source).Append(':').Append(count.Numerator).Append('/').Append(count.Denominator);
        builder.Append('\n');
    }

    private static List<string> Incompatibilities(
        MauiPreviewQualificationReport reference,
        MauiPreviewQualificationReport candidate)
    {
        var reasons = new List<string>();
        if (!string.Equals(reference.ContractVersion, candidate.ContractVersion, StringComparison.Ordinal))
            reasons.Add("accumulate-contract-version-mismatch");
        if (!string.Equals(reference.Platform, candidate.Platform, StringComparison.Ordinal))
            reasons.Add("accumulate-platform-mismatch");
        if (!string.Equals(reference.Thresholds.PolicyVersion, candidate.Thresholds.PolicyVersion, StringComparison.Ordinal))
            reasons.Add("accumulate-policy-version-mismatch");
        if (!string.Equals(reference.Fingerprints.CorpusFingerprint, candidate.Fingerprints.CorpusFingerprint, StringComparison.Ordinal))
            reasons.Add("accumulate-corpus-fingerprint-mismatch");
        if (!ThresholdsMatch(reference.Thresholds, candidate.Thresholds))
            reasons.Add("accumulate-threshold-mismatch");
        return reasons;
    }

    private static bool ThresholdsMatch(MauiQualificationGateThresholds left, MauiQualificationGateThresholds right) =>
        left.ConfidenceLevel.Equals(right.ConfidenceLevel) &&
        left.MinimumRepairPrecision.Equals(right.MinimumRepairPrecision) &&
        left.MinimumRepairEvaluations == right.MinimumRepairEvaluations &&
        left.MinimumNoRepairEvaluations == right.MinimumNoRepairEvaluations &&
        left.MaximumFalseHeals == right.MaximumFalseHeals &&
        left.MinimumSelectorStability.Equals(right.MinimumSelectorStability) &&
        left.MinimumSelectorObservations == right.MinimumSelectorObservations &&
        left.MinimumClassificationAccuracy.Equals(right.MinimumClassificationAccuracy) &&
        left.MinimumClassificationEvaluations == right.MinimumClassificationEvaluations;

    private static MauiQualificationRateMetric Select(MauiQualificationMetrics metrics, string name) => name switch
    {
        "recordingValidity" => metrics.RecordingValidity,
        "selectorStability" => metrics.SelectorStability,
        "repairPrecision" => metrics.RepairPrecision,
        "repairRecall" => metrics.RepairRecall,
        "falseHeals" => metrics.FalseHeals,
        "abstention" => metrics.Abstention,
        "classificationAccuracy" => metrics.ClassificationAccuracy,
        _ => new MauiQualificationRateMetric(),
    };

    private static MauiQualificationRateMetric MergeRate(IEnumerable<MauiQualificationRateMetric> metrics)
    {
        var list = metrics.Where(static metric => metric is not null).ToList();
        var numerator = list.Sum(static metric => Math.Max(0, metric.Numerator));
        var denominator = list.Sum(static metric => Math.Max(0, metric.Denominator));
        var confidence = list
            .Select(static metric => metric.ConfidenceInterval?.ConfidenceLevel)
            .FirstOrDefault(static level => level is > 0 and < 1) ?? 0.95;
        var contributing = list.Where(static metric => metric.Denominator > 0).ToList();
        return new MauiQualificationRateMetric
        {
            State = denominator == 0 ? "missing" : "measured",
            Numerator = numerator,
            Denominator = denominator,
            Value = denominator == 0 ? null : (double)numerator / denominator,            ConfidenceInterval = denominator == 0
                ? null
                : MauiQualificationStatistics.WilsonInterval(numerator, denominator, confidence),
            SampleSources = list
                .SelectMany(static metric => metric.SampleSources ?? [])
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static source => source, StringComparer.Ordinal)
                .ToList(),
            SourceCounts = list
                .SelectMany(static metric => metric.SourceCounts ?? [])
                .GroupBy(static count => count.Source, StringComparer.Ordinal)
                .OrderBy(static group => group.Key, StringComparer.Ordinal)
                .Select(static group => new MauiQualificationRateSourceCount
                {
                    Source = group.Key,
                    Numerator = group.Sum(static count => Math.Max(0, count.Numerator)),
                    Denominator = group.Sum(static count => Math.Max(0, count.Denominator)),
                })
                .ToList(),
            // Independence is conjunctive: one pooled or non-device contributor makes the
            // merged total non-independent, exactly as it does within a single run.
            IndependentDeviceRuns = denominator == 0
                ? null
                : contributing.Count > 0 && contributing.All(static metric => metric.IndependentDeviceRuns == true),
        };
    }

    private static void AddGates(MauiQualificationAccumulation accumulation)
    {
        var thresholds = accumulation.Thresholds;
        AddRateGate(
            accumulation,
            "accumulated-repair-precision",
            "repairPrecision",
            thresholds.MinimumRepairEvaluations,
            thresholds.MinimumRepairPrecision,
            "repair-evaluation-count-insufficient",
            "repair-precision-lower-bound-below-threshold");
        AddRateGate(
            accumulation,
            "accumulated-selector-stability",
            "selectorStability",
            thresholds.MinimumSelectorObservations,
            thresholds.MinimumSelectorStability,
            "selector-observation-count-insufficient",
            "selector-stability-below-threshold",
            // Mirrors AddSelectorStabilityGate, which judges the point estimate. Using a different
            // rule here would make an accumulated result disagree with its own per-run gate.
            useLowerBound: false);
        AddRateGate(
            accumulation,
            "accumulated-classification-accuracy",
            "classificationAccuracy",
            thresholds.MinimumClassificationEvaluations,
            thresholds.MinimumClassificationAccuracy,
            "classification-evaluation-count-insufficient",
            "classification-accuracy-lower-bound-below-threshold");

        var falseHeals = accumulation.Metrics.GetValueOrDefault("falseHeals") ?? new MauiQualificationRateMetric();
        var falseHealStatus = falseHeals.Denominator < thresholds.MinimumNoRepairEvaluations
            ? MauiPreviewQualificationStates.NotQualified
            : falseHeals.Numerator <= thresholds.MaximumFalseHeals
                ? MauiPreviewQualificationStates.Pass
                : MauiPreviewQualificationStates.Fail;
        accumulation.Gates.Add(new MauiQualificationGateResult
        {
            GateId = "accumulated-zero-false-heals",
            Status = falseHealStatus,
            Message = falseHealStatus == MauiPreviewQualificationStates.Pass
                ? "No false heal was observed across the accumulated no-repair denominator."
                : "Accumulated no-repair evidence is insufficient or includes a false heal.",
            ReasonCodes = falseHealStatus == MauiPreviewQualificationStates.Pass
                ? []
                : falseHeals.Denominator < thresholds.MinimumNoRepairEvaluations
                    ? ["no-repair-evaluation-count-insufficient"]
                    : ["false-heal-observed"],
        });

        if (accumulation.RejectedRuns > 0)
        {
            accumulation.Gates.Add(new MauiQualificationGateResult
            {
                GateId = "accumulated-run-compatibility",
                Status = MauiPreviewQualificationStates.NotQualified,
                Message = "At least one run was excluded from accumulation because it did not agree with the reference run.",
                ReasonCodes = accumulation.Runs
                    .Where(static run => !run.Accepted)
                    .SelectMany(static run => run.ReasonCodes)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static code => code, StringComparer.Ordinal)
                    .ToList(),
            });
        }
    }

    private static void AddRateGate(
        MauiQualificationAccumulation accumulation,
        string gateId,
        string metricName,
        int minimumDenominator,
        double minimumLowerBound,
        string insufficientCode,
        string belowThresholdCode,
        bool useLowerBound = true)
    {
        var metric = accumulation.Metrics.GetValueOrDefault(metricName) ?? new MauiQualificationRateMetric();
        var observed = useLowerBound ? metric.ConfidenceInterval?.Lower : metric.Value;
        var status = metric.Denominator < minimumDenominator
            ? MauiPreviewQualificationStates.NotQualified
            : observed >= minimumLowerBound
                ? MauiPreviewQualificationStates.Pass
                : MauiPreviewQualificationStates.Fail;
        accumulation.Gates.Add(new MauiQualificationGateResult
        {
            GateId = gateId,
            Status = status,
            Message = status == MauiPreviewQualificationStates.Pass
                ? $"Accumulated {metricName} meets the conservative Wilson lower-bound threshold."
                : $"Accumulated {metricName} lacks enough evaluations or misses the conservative lower-bound threshold.",
            ReasonCodes = status == MauiPreviewQualificationStates.Pass
                ? []
                : metric.Denominator < minimumDenominator
                    ? [insufficientCode]
                    : [belowThresholdCode],
        });
    }
}
