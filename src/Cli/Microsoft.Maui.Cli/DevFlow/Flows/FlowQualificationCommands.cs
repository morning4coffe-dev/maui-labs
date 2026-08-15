using System.CommandLine;
using System.Text.Json;
using Microsoft.Maui.Cli.DevFlow;
using Microsoft.Maui.DevFlow.Testing;

namespace Microsoft.Maui.Cli.DevFlow.Flows;

/// <summary>Read-only CLI adapter for deterministic engineering-preview qualification evidence.</summary>
internal static class FlowQualificationCommands
{
    internal static Command Create(
        Option<bool> jsonOption,
        Option<bool> noJsonOption,
        Option<string> platformOption,
        IDevFlowOutputWriter output,
        Action markError)
    {
        var command = new Command(
            "qualify",
            "Evaluate deterministic DevFlow preview gates from a static corpus and optional redacted evidence; never replays or applies changes.");
        var corpusOption = new Option<string>("--corpus")
        {
            Description = "Path to tests/DevFlow/InspectorCorpus (default: that repository-relative path).",
            DefaultValueFactory = _ => Path.Combine("tests", "DevFlow", "InspectorCorpus"),
        };
        var resultsOption = new Option<string[]>("--results")
        {
            Description = "One or more qualification-evidence JSON files. They are parsed read-only and never replayed.",
            AllowMultipleArgumentsPerToken = true,
        };
        var manifestsOption = new Option<string[]>("--artifact-manifest")
        {
            Description = "One or more redacted flow-pilot manifest JSON files. Referenced reports are not read or replayed.",
            AllowMultipleArgumentsPerToken = true,
        };
        var tierOneOption = new Option<string[]>("--tier1-flow")
        {
            Description = "Declared Tier-1 flow digest or identifier. Repeat for each required flow; values are fingerprinted in output.",
            AllowMultipleArgumentsPerToken = true,
        };
        var outputOption = new Option<string?>("--output", "-o")
        {
            Description = "Optional qualification JSON output path. The command writes only this explicit path.",
        };
        var seedOption = new Option<int>("--mutation-seed")
        {
            Description = "Deterministic seed for generated no-repair evaluations.",
            DefaultValueFactory = _ => 20260802,
        };
        var generatedOption = new Option<int>("--generated-no-repair")
        {
            Description = "Number of deterministic generated no-repair evaluations (minimum 300 for the preview gate).",
            DefaultValueFactory = _ => 300,
        };
        var failOnNonPassOption = new Option<bool>("--fail-on-non-pass")
        {
            Description = "Return a nonzero exit code when the result is fail or not-qualified. Default is advisory.",
        };
        var accumulateOption = new Option<string?>("--accumulate")
        {
            Description = "Directory of prior run-*.json qualification reports. Merges metric numerators and denominators across independent runs, records this run into the directory, and writes accumulated.json.",
        };
        var baselineOption = new Option<string?>("--baseline")
        {
            Description = "Path to a committed baseline qualification.json. Fails when a gated metric regresses below the baseline.",
        };
        command.Add(corpusOption);
        command.Add(resultsOption);
        command.Add(manifestsOption);
        command.Add(tierOneOption);
        command.Add(outputOption);
        command.Add(seedOption);
        command.Add(generatedOption);
        command.Add(failOnNonPassOption);
        command.Add(accumulateOption);
        command.Add(baselineOption);
        command.SetAction(async (ctx, ct) =>
        {
            var json = output.ResolveJsonMode(ctx.GetValue(jsonOption), ctx.GetValue(noJsonOption));
            var corpusPath = ctx.GetValue(corpusOption)!;
            var platform = ctx.GetValue(platformOption)!;
            var run = MauiPreviewQualificationCorpusRunner.Run(new MauiPreviewQualificationCorpusRunRequest
            {
                CorpusRoot = corpusPath,
                Platform = platform,
                MutationSeed = ctx.GetValue(seedOption),
                GeneratedNoRepairEvaluations = Math.Max(0, ctx.GetValue(generatedOption)),
            });
            var evidence = CreateBaseInput(platform, run);
            foreach (var file in ctx.GetValue(resultsOption) ?? [])
                await MergeEvidenceFileAsync(evidence, file, ct);
            foreach (var file in ctx.GetValue(manifestsOption) ?? [])
                await MergeArtifactManifestAsync(evidence, file, ct);
            foreach (var flow in ctx.GetValue(tierOneOption) ?? [])
            {
                if (!string.IsNullOrWhiteSpace(flow))
                    evidence.Tier1Flows.Add(flow);
            }
            evidence.Tier1Flows = evidence.Tier1Flows.Distinct(StringComparer.Ordinal).ToList();

            var report = MauiPreviewQualificationGateEvaluator.Evaluate(evidence);
            var outputPath = ctx.GetValue(outputOption);
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                try
                {
                    await WriteAtomicAsync(outputPath, report, ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    output.WriteError("Qualification report could not be written to the explicit output path.", json, "WriteError");
                    markError();
                    return;
                }
            }

            MauiQualificationAccumulation? accumulation = null;
            var accumulateDirectory = ctx.GetValue(accumulateOption);
            if (!string.IsNullOrWhiteSpace(accumulateDirectory))
            {
                try
                {
                    accumulation = await AccumulateAsync(accumulateDirectory, report, ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    output.WriteError("Accumulated qualification evidence could not be read or written.", json, "AccumulateError");
                    markError();
                    return;
                }
            }

            MauiQualificationBaselineComparison? baseline = null;
            var baselinePath = ctx.GetValue(baselineOption);
            if (!string.IsNullOrWhiteSpace(baselinePath))
            {
                baseline = CompareBaseline(baselinePath, report, accumulation);
                if (!baseline.Ok)
                    markError();
            }

            output.WriteResult(report, json, value =>
            {
                Console.WriteLine($"Qualification: {value.Status}");
                Console.WriteLine($"Static corpus: {value.Corpus.CuratedCases} curated ({value.Corpus.CuratedRepairPositiveCases} repair-positive, {value.Corpus.CuratedNoRepairCases} no-repair), {value.Corpus.GeneratedCases} generated no-repair cases");
                Console.WriteLine($"False heals: {Describe(value.Metrics.FalseHeals)}");
                Console.WriteLine($"Repair precision: {Describe(value.Metrics.RepairPrecision)}");
                Console.WriteLine($"Classification accuracy: {Describe(value.Metrics.ClassificationAccuracy)}");
                foreach (var gate in value.Gates.Where(static gate => gate.Status != MauiPreviewQualificationStates.Pass))
                    Console.WriteLine($"{gate.Status}: {gate.GateId} ({string.Join(", ", gate.ReasonCodes)})");
                if (accumulation is not null)
                {
                    Console.WriteLine($"Accumulated: {accumulation.Status} over {accumulation.AcceptedRuns} accepted of {accumulation.ConsideredRuns} runs ({accumulation.RejectedRuns} rejected)");
                    foreach (var name in MauiPreviewQualificationAccumulator.MergedMetricNames)
                    {
                        if (accumulation.Metrics.TryGetValue(name, out var merged) && merged.Denominator > 0)
                            Console.WriteLine($"  {name}: {Describe(merged)}");
                    }
                }
                foreach (var regression in baseline?.Regressions ?? [])
                    Console.WriteLine($"regression: {regression}");
            });
            if (ctx.GetValue(failOnNonPassOption) && report.Status != MauiPreviewQualificationStates.Pass)
                markError();
        });
        return command;
    }

    private static string Describe(MauiQualificationRateMetric metric)
    {
        var split = metric.SourceCounts.Count == 0
            ? string.Empty
            : " [" + string.Join(", ", metric.SourceCounts.Select(static item => $"{item.Source} {item.Numerator}/{item.Denominator}")) + "]";
        return $"{metric.Numerator}/{metric.Denominator}{split}";
    }

    /// <summary>
    /// Merges this run into a directory of prior runs. Merging separate runs is what "at least N
    /// clean first attempts" actually requires: N independent trials, not N iterations inside one
    /// warm process. Duplicate evidence is rejected rather than counted twice.
    /// </summary>
    private static async Task<MauiQualificationAccumulation> AccumulateAsync(
        string directory,
        MauiPreviewQualificationReport report,
        CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(directory);
        Directory.CreateDirectory(full);
        var fingerprint = MauiPreviewQualificationAccumulator.ComputeEvidenceFingerprint(report);
        var runPath = Path.Combine(full, "run-" + fingerprint.Replace("sha256:", string.Empty, StringComparison.Ordinal)[..32] + ".json");
        await WriteAtomicAsync(runPath, report, cancellationToken).ConfigureAwait(false);

        var reports = MauiPreviewQualificationAccumulator.ReadDirectory(full, out var errors);
        var accumulation = MauiPreviewQualificationAccumulator.Accumulate(reports, DateTimeOffset.UtcNow);
        foreach (var error in errors.Distinct(StringComparer.Ordinal))
        {
            accumulation.Gates.Add(new MauiQualificationGateResult
            {
                GateId = "accumulated-run-readable",
                Status = MauiPreviewQualificationStates.NotQualified,
                Message = "At least one accumulated run file could not be read.",
                ReasonCodes = [error],
            });
            accumulation.Status = MauiPreviewQualificationStates.NotQualified;
        }

        var accumulatedPath = Path.Combine(full, "accumulated.json");
        var payload = JsonSerializer.Serialize(accumulation, MauiTestingJsonContext.Default.MauiQualificationAccumulation);
        var temporary = accumulatedPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, payload, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, accumulatedPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
        return accumulation;
    }

    /// <summary>
    /// Compares gated metrics against a committed baseline. Only regressions fail: a run may
    /// improve on the baseline, but a drop in repair precision, selector stability, classification
    /// accuracy, or false-heal cleanliness is a failure even when the overall status is unchanged.
    /// </summary>
    private static MauiQualificationBaselineComparison CompareBaseline(
        string baselinePath,
        MauiPreviewQualificationReport report,
        MauiQualificationAccumulation? accumulation)
    {
        var comparison = new MauiQualificationBaselineComparison();
        if (!TryReadText(baselinePath, out var text))
        {
            comparison.Regressions.Add("baseline-unreadable");
            return comparison;
        }
        MauiPreviewQualificationReport? baseline;
        try
        {
            baseline = JsonSerializer.Deserialize(text, MauiTestingJsonContext.Default.MauiPreviewQualificationReport);
        }
        catch (JsonException)
        {
            comparison.Regressions.Add("baseline-unparsable");
            return comparison;
        }
        if (baseline is null)
        {
            comparison.Regressions.Add("baseline-unparsable");
            return comparison;
        }

        CompareRate(comparison, "repairPrecision", baseline.Metrics.RepairPrecision, report.Metrics.RepairPrecision);
        CompareRate(comparison, "selectorStability", baseline.Metrics.SelectorStability, report.Metrics.SelectorStability);
        CompareRate(comparison, "classificationAccuracy", baseline.Metrics.ClassificationAccuracy, report.Metrics.ClassificationAccuracy);
        CompareRate(
            comparison,
            "flakeFirstAttemptStability",
            baseline.Metrics.FlakeFirstAttemptStability.Stability,
            report.Metrics.FlakeFirstAttemptStability.Stability);

        // False heals are a count, not a rate: any increase is a regression, and the denominator
        // must not shrink either or a clean sweep could be faked by evaluating fewer cases.
        if (report.Metrics.FalseHeals.Numerator > baseline.Metrics.FalseHeals.Numerator)
            comparison.Regressions.Add($"falseHeals numerator {baseline.Metrics.FalseHeals.Numerator} -> {report.Metrics.FalseHeals.Numerator}");
        if (report.Metrics.FalseHeals.Denominator < baseline.Metrics.FalseHeals.Denominator)
            comparison.Regressions.Add($"falseHeals denominator {baseline.Metrics.FalseHeals.Denominator} -> {report.Metrics.FalseHeals.Denominator}");

        foreach (var baselineGate in baseline.Gates.Where(static gate => gate.Status == MauiPreviewQualificationStates.Pass))
        {
            var current = report.Gates.FirstOrDefault(gate => gate.GateId == baselineGate.GateId);
            if (current is null)
                comparison.Regressions.Add($"gate {baselineGate.GateId} missing");
            else if (current.Status != MauiPreviewQualificationStates.Pass)
                comparison.Regressions.Add($"gate {baselineGate.GateId} pass -> {current.Status}");
        }

        if (accumulation is not null)
        {
            foreach (var name in MauiPreviewQualificationAccumulator.MergedMetricNames)
            {
                if (accumulation.Metrics.TryGetValue(name, out var merged) && merged.Denominator > 0)
                    CompareRate(comparison, "accumulated." + name, BaselineMetric(baseline, name), merged);
            }
        }
        return comparison;
    }

    private static MauiQualificationRateMetric? BaselineMetric(MauiPreviewQualificationReport baseline, string name) => name switch
    {
        "recordingValidity" => baseline.Metrics.RecordingValidity,
        "selectorStability" => baseline.Metrics.SelectorStability,
        "repairPrecision" => baseline.Metrics.RepairPrecision,
        "repairRecall" => baseline.Metrics.RepairRecall,
        "falseHeals" => null,
        "abstention" => null,
        "classificationAccuracy" => baseline.Metrics.ClassificationAccuracy,
        _ => null,
    };

    private static void CompareRate(
        MauiQualificationBaselineComparison comparison,
        string name,
        MauiQualificationRateMetric? baseline,
        MauiQualificationRateMetric? current)
    {
        if (baseline is null || current is null || baseline.Denominator == 0)
            return;
        var baselineRate = (double)baseline.Numerator / baseline.Denominator;
        var currentRate = current.Denominator == 0 ? 0d : (double)current.Numerator / current.Denominator;
        if (current.Denominator == 0)
        {
            comparison.Regressions.Add($"{name} evidence disappeared (baseline {baseline.Numerator}/{baseline.Denominator})");
            return;
        }
        if (currentRate + 1e-9 < baselineRate)
            comparison.Regressions.Add($"{name} {baseline.Numerator}/{baseline.Denominator} -> {current.Numerator}/{current.Denominator}");
    }

    private sealed class MauiQualificationBaselineComparison
    {
        public List<string> Regressions { get; } = [];

        public bool Ok => Regressions.Count == 0;
    }

    private static MauiPreviewQualificationInput CreateBaseInput(
        string platform,
        MauiPreviewQualificationCorpusRunResult run)
    {
        var assembly = typeof(FlowQualificationCommands).Assembly;
        var testingAssembly = typeof(MauiFlowRunner).Assembly;
        var input = new MauiPreviewQualificationInput
        {
            Platform = platform,
            Corpus = run.Summary,
            Samples = run.Samples,
            PrivacySecurity = run.PrivacySecurity,
            RuntimeOverhead = MauiPreviewQualificationPerformance.MeasureDeterministicHostOperations(iterations: 10),
            Evidence = new MauiQualificationRequiredEvidence
            {
                CorpusManifestValid = run.Summary.ManifestValid,
                CaseSchemaValid = run.Summary.CaseSchemaValid,
                // Static evidence intentionally does not fabricate run-report, recording,
                // first-attempt, or artifact-manifest facts.
            },
            Fingerprints = new MauiQualificationFingerprints
            {
                CorpusVersion = run.Summary.Version,
                CorpusFingerprint = run.Summary.ManifestFingerprint,
                TestingPackageVersion = testingAssembly.GetName().Version?.ToString(),
                ToolVersion = assembly.GetName().Version?.ToString(),
                PolicyVersion = "preview-qualification-policy-v1",
            },
            Profiles =
            [
                new MauiQualificationPlatformProfile
                {
                    Platform = platform,
                    Scope = "static-corpus",
                    DeviceEvidenceKind = "none",
                    RealDevice = false,
                    FirstAttemptMode = "not-run",
                },
            ],
        };
        return input;
    }

    private static async Task MergeEvidenceFileAsync(
        MauiPreviewQualificationInput target,
        string file,
        CancellationToken cancellationToken)
    {
        if (!TryReadText(file, out var text))
        {
            target.Evidence ??= new MauiQualificationRequiredEvidence();
            target.Evidence.ReportSchemaValid = false;
            return;
        }
        var parsed = MauiPreviewQualificationInputValidator.ParseJson(text);
        if (!parsed.Ok || parsed.Input is null)
        {
            target.Evidence ??= new MauiQualificationRequiredEvidence();
            target.Evidence.ReportSchemaValid = false;
            return;
        }
        Merge(target, parsed.Input);
        await Task.CompletedTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task MergeArtifactManifestAsync(
        MauiPreviewQualificationInput target,
        string file,
        CancellationToken cancellationToken)
    {
        if (!TryReadText(file, out var text))
        {
            target.Evidence ??= new MauiQualificationRequiredEvidence();
            target.Evidence.ArtifactManifestValid = false;
            return;
        }
        var parsed = MauiPreviewQualificationArtifactManifestReader.ParseJson(text);
        Merge(target, parsed.Input);
        if (!parsed.Ok)
        {
            target.Evidence ??= new MauiQualificationRequiredEvidence();
            target.Evidence.ArtifactManifestValid = false;
        }
        await Task.CompletedTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void Merge(MauiPreviewQualificationInput target, MauiPreviewQualificationInput source)
    {
        if (!string.IsNullOrWhiteSpace(source.Platform))
            target.Platform = source.Platform;
        target.Samples.AddRange(source.Samples ?? []);
        target.Profiles.AddRange(source.Profiles ?? []);
        target.ArtifactRefs.AddRange(source.ArtifactRefs ?? []);
        target.Exclusions.AddRange(source.Exclusions ?? []);
        target.Tier1Flows.AddRange(source.Tier1Flows ?? []);
        if (source.AppleQa is not null)
            target.AppleQa = source.AppleQa;
        if (source.FeatureFlags is not null)
            target.FeatureFlags = source.FeatureFlags;
        if (source.Review is not null)
            target.Review = source.Review;
        if (source.RuntimeOverhead is not null)
            target.RuntimeOverhead = source.RuntimeOverhead;
        if (source.PrivacySecurity is not null)
            target.PrivacySecurity = source.PrivacySecurity;
        // The local corpus runner remains authoritative. Optional evidence cannot replace its
        // manifest/case counts or turn generated static samples into stronger evidence.
        MergeEvidence(target, source.Evidence);
        MergeFingerprints(target.Fingerprints, source.Fingerprints);
    }

    private static void MergeEvidence(MauiPreviewQualificationInput target, MauiQualificationRequiredEvidence? source)
    {
        if (source is null)
            return;
        target.Evidence ??= new MauiQualificationRequiredEvidence();
        if (source.CorpusManifestValid.HasValue) target.Evidence.CorpusManifestValid = source.CorpusManifestValid;
        if (source.CaseSchemaValid.HasValue) target.Evidence.CaseSchemaValid = source.CaseSchemaValid;
        if (source.ReportSchemaValid.HasValue) target.Evidence.ReportSchemaValid = source.ReportSchemaValid;
        if (source.RecordingValid.HasValue) target.Evidence.RecordingValid = source.RecordingValid;
        if (source.FirstAttemptEvidencePresent.HasValue) target.Evidence.FirstAttemptEvidencePresent = source.FirstAttemptEvidencePresent;
        if (source.ArtifactManifestValid.HasValue) target.Evidence.ArtifactManifestValid = source.ArtifactManifestValid;
        if (source.ArtifactReferencesComplete.HasValue) target.Evidence.ArtifactReferencesComplete = source.ArtifactReferencesComplete;
    }

    private static void MergeFingerprints(MauiQualificationFingerprints target, MauiQualificationFingerprints source)
    {
        target.CorpusVersion ??= source.CorpusVersion;
        target.CorpusFingerprint ??= source.CorpusFingerprint;
        target.RepositoryCommit ??= source.RepositoryCommit;
        target.TestingPackageVersion ??= source.TestingPackageVersion;
        target.PackageId ??= source.PackageId;
        target.PackageFingerprint ??= source.PackageFingerprint;
        target.ToolVersion ??= source.ToolVersion;
        target.ToolFingerprint ??= source.ToolFingerprint;
        target.PolicyVersion ??= source.PolicyVersion;
        target.PolicyFingerprint ??= source.PolicyFingerprint;
    }

    private static bool TryReadText(string path, out string text)
    {
        text = string.Empty;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 1_048_576)
                return false;
            text = File.ReadAllText(info.FullName);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static async Task WriteAtomicAsync(
        string outputPath,
        MauiPreviewQualificationReport report,
        CancellationToken cancellationToken)
    {
        var target = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(target);
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("An output directory is required.", nameof(outputPath));
        Directory.CreateDirectory(directory);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(report, MauiTestingJsonContext.Default.MauiPreviewQualificationReport);
            await File.WriteAllTextAsync(temporary, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }
}
