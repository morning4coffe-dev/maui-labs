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
        command.Add(corpusOption);
        command.Add(resultsOption);
        command.Add(manifestsOption);
        command.Add(tierOneOption);
        command.Add(outputOption);
        command.Add(seedOption);
        command.Add(generatedOption);
        command.Add(failOnNonPassOption);
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

            output.WriteResult(report, json, static value =>
            {
                Console.WriteLine($"Qualification: {value.Status}");
                Console.WriteLine($"Static corpus: {value.Corpus.CuratedCases} curated, {value.Corpus.GeneratedCases} generated no-repair cases");
                foreach (var gate in value.Gates.Where(static gate => gate.Status != MauiPreviewQualificationStates.Pass))
                    Console.WriteLine($"{gate.Status}: {gate.GateId} ({string.Join(", ", gate.ReasonCodes)})");
            });
            if (ctx.GetValue(failOnNonPassOption) && report.Status != MauiPreviewQualificationStates.Pass)
                markError();
        });
        return command;
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
