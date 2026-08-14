using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Microsoft.Maui.DevFlow.Testing;

/// <summary>Bounded inputs for the deterministic static qualification corpus runner.</summary>
public sealed class MauiPreviewQualificationCorpusRunRequest
{
    public required string CorpusRoot { get; init; }
    public string Platform { get; init; } = "android";
    public int MutationSeed { get; init; } = 20260802;
    public int GeneratedNoRepairEvaluations { get; init; } = 300;
}

/// <summary>Static corpus execution result. Generated samples are never represented as device runs.</summary>
public sealed class MauiPreviewQualificationCorpusRunResult
{
    public MauiQualificationCorpusSummary Summary { get; init; } = new();
    public List<MauiQualificationCorpusCaseResult> Cases { get; init; } = [];
    public List<MauiQualificationExecutionSample> Samples { get; init; } = [];
    public MauiQualificationPrivacySecurityMetric PrivacySecurity { get; init; } = new();
}

/// <summary>A safe outcome for one curated static corpus case.</summary>
public sealed class MauiQualificationCorpusCaseResult
{
    public string? CaseId { get; init; }
    public string? Source { get; init; }
    public string? Kind { get; init; }
    public string? Disposition { get; init; }
    public bool SchemaValid { get; init; }
    public bool Passed { get; init; }
    public bool RepairEligible { get; init; }
    public List<string> DiagnosticIds { get; init; } = [];
    public List<string> CandidateKinds { get; init; } = [];
    public List<string> IneligibilityCodes { get; init; } = [];
}

/// <summary>
/// Runs selector, repair, and source-policy corpus cases without a device. Fixtures are evaluated
/// by deterministic rules and their expected outcomes are compared; no fixture text is emitted.
/// </summary>
public static class MauiPreviewQualificationCorpusRunner
{
    public const string GeneratorVersion = "qualification-no-repair-generator-v1";
    private const int MaxCorpusFileBytes = 1_048_576;

    /// <summary>Loads, validates, and deterministically evaluates the repository corpus.</summary>
    public static MauiPreviewQualificationCorpusRunResult Run(MauiPreviewQualificationCorpusRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<string>();
        var cases = new List<MauiQualificationCorpusCaseResult>();
        var samples = new List<MauiQualificationExecutionSample>();
        var noRepairFixtures = new List<(string Id, JsonElement Fixture)>();
        var summary = new MauiQualificationCorpusSummary
        {
            Version = "selector-health-corpus-v1",
            StaticOnly = true,
            MutationSeed = request.MutationSeed,
            GeneratorVersion = GeneratorVersion,
        };

        string root;
        try
        {
            root = Path.GetFullPath(request.CorpusRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            errors.Add("corpus-root-invalid");
            return Complete(summary, cases, samples, errors, null);
        }

        var manifestPath = Path.Combine(root, "corpus-manifest.json");
        var schemaPath = Path.Combine(root, "schemas", "selector-health-corpus-v1.json");
        if (!TryReadObject(manifestPath, out var manifest, out var manifestBytes, out var manifestError))
        {
            errors.Add(manifestError ?? "corpus-manifest-invalid");
            return Complete(summary, cases, samples, errors, null);
        }
        summary.ManifestFingerprint = Hash(manifestBytes);

        if (!TryReadObject(schemaPath, out var schema, out _, out var schemaError))
            errors.Add(schemaError ?? "corpus-schema-invalid");
        else
            ValidateSchema(schema, errors);

        ValidateManifest(manifest, errors);
        if (!manifest.TryGetProperty("cases", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            errors.Add("corpus-cases-missing");
            return Complete(summary, cases, samples, errors, null);
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
        {
            if (!TryReadEntry(root, entry, ids, out var fixture, out var metadata, out var entryError))
            {
                errors.Add(entryError ?? "corpus-case-invalid");
                continue;
            }

            var evaluation = EvaluateFixture(fixture, metadata.Id);
            var passed = MatchesExpectations(fixture, evaluation);
            var caseResult = new MauiQualificationCorpusCaseResult
            {
                CaseId = MauiQualificationSanitizer.Fingerprint(metadata.Id),
                Source = MauiQualificationSampleSources.Curated,
                Kind = metadata.Kind,
                Disposition = metadata.Disposition,
                SchemaValid = true,
                Passed = passed,
                RepairEligible = evaluation.RepairEligible,
                DiagnosticIds = evaluation.DiagnosticIds,
                CandidateKinds = evaluation.CandidateKinds,
                IneligibilityCodes = evaluation.IneligibilityCodes,
            };
            cases.Add(caseResult);
            if (!passed)
                errors.Add("corpus-case-expectation-mismatch");
            if (passed && string.Equals(metadata.Disposition, "no-repair", StringComparison.Ordinal))
                noRepairFixtures.Add((metadata.Id, fixture.Clone()));

            samples.Add(new MauiQualificationExecutionSample
            {
                SampleId = MauiQualificationSanitizer.Fingerprint(metadata.Id),
                Source = MauiQualificationSampleSources.Curated,
                Category = metadata.Kind,
                Platform = request.Platform,
                NoRepairExpected = string.Equals(metadata.Disposition, "no-repair", StringComparison.Ordinal),
                RepairProposed = evaluation.RepairEligible,
                RepairExpected = string.Equals(metadata.Disposition, "repair-eligible", StringComparison.Ordinal),
                RepairCorrect = evaluation.RepairEligible ? passed : null,
                FalseHeal = string.Equals(metadata.Disposition, "no-repair", StringComparison.Ordinal) && evaluation.RepairEligible,
                Abstained = string.Equals(metadata.Disposition, "no-repair", StringComparison.Ordinal) && !evaluation.RepairEligible,
            });
        }

        if (noRepairFixtures.Count == 0)
        {
            errors.Add("no-repair-corpus-base-missing");
        }
        else
        {
            var count = Math.Max(0, request.GeneratedNoRepairEvaluations);
            var random = new DeterministicRandom((uint)request.MutationSeed);
            for (var index = 0; index < count; index++)
            {
                var basis = noRepairFixtures[(int)(random.Next() % (uint)noRepairFixtures.Count)];
                var generatedId = $"generated:{basis.Id}:{index}:{request.MutationSeed}";
                using var generated = GenerateNoRepairFixture(
                    basis.Fixture,
                    generatedId,
                    (int)(random.Next() % 4));
                var evaluation = EvaluateFixture(generated.RootElement, generatedId);
                if (evaluation.RepairEligible)
                    errors.Add("generated-no-repair-false-heal");
                samples.Add(new MauiQualificationExecutionSample
                {
                    SampleId = MauiQualificationSanitizer.Fingerprint(generatedId),
                    Source = MauiQualificationSampleSources.Generated,
                    Category = "generated-no-repair-production-pipeline",
                    Platform = request.Platform,
                    DeviceEvidenceKind = "not-a-device-run",
                    RealDevice = false,
                    NoRepairExpected = true,
                    RepairProposed = evaluation.RepairEligible,
                    RepairCorrect = null,
                    FalseHeal = evaluation.RepairEligible,
                    Abstained = !evaluation.RepairEligible,
                });
            }
        }

        var security = MauiQualificationSecurityCorpusRunner.Run(root);
        summary.SecurityCorpus = security.Summary;
        summary.CuratedCases = cases.Count;
        summary.GeneratedCases = samples.Count(static item => item.Source == MauiQualificationSampleSources.Generated);
        summary.DeviceBackedCases = 0;
        summary.ManifestValid = errors.All(static error => !error.StartsWith("corpus-", StringComparison.Ordinal) &&
            !error.StartsWith("no-repair-", StringComparison.Ordinal));
        summary.CaseSchemaValid = cases.Count > 0 && cases.All(static item => item.SchemaValid);
        summary.Errors = errors.ToList();

        var privacy = new MauiQualificationPrivacySecurityMetric
        {
            State = security.Summary.Valid == true ? "measured" : "missing",
            TestCount = security.Summary.CaseCount,
            EscapeCount = security.Summary.Valid == true && security.Summary.PassedCount == security.Summary.CaseCount ? 0 : 1,
            CanaryScanPassed = security.Summary.Valid == true && security.Summary.PassedCount == security.Summary.CaseCount,
            CaseIds = security.Summary.CaseIds.ToList(),
            MissingReason = security.Summary.Valid == true ? null : "Security/privacy corpus validation failed.",
        };
        return new MauiPreviewQualificationCorpusRunResult
        {
            Summary = summary,
            Cases = cases,
            Samples = samples,
            PrivacySecurity = privacy,
        };
    }

    private static MauiPreviewQualificationCorpusRunResult Complete(
        MauiQualificationCorpusSummary summary,
        List<MauiQualificationCorpusCaseResult> cases,
        List<MauiQualificationExecutionSample> samples,
        List<string> errors,
        MauiQualificationSecurityCorpusSummary? security)
    {
        summary.ManifestValid = false;
        summary.CaseSchemaValid = false;
        summary.Errors = errors;
        summary.SecurityCorpus = security;
        return new MauiPreviewQualificationCorpusRunResult
        {
            Summary = summary,
            Cases = cases,
            Samples = samples,
            PrivacySecurity = new MauiQualificationPrivacySecurityMetric
            {
                MissingReason = "Security/privacy corpus did not run because the primary corpus was invalid.",
            },
        };
    }

    private static void ValidateManifest(JsonElement manifest, List<string> errors)
    {
        if (!HasInt(manifest, "schema", 1)) errors.Add("corpus-manifest-schema-invalid");
        if (!HasString(manifest, "name")) errors.Add("corpus-manifest-name-missing");
        if (!HasString(manifest, "schemaFile")) errors.Add("corpus-manifest-schema-file-missing");
        if (!HasBoolean(manifest, "staticOnly", true)) errors.Add("corpus-manifest-not-static");
        if (!HasBoolean(manifest, "noEmulatorRequired", true)) errors.Add("corpus-manifest-device-requirement-invalid");
    }

    private static void ValidateSchema(JsonElement schema, List<string> errors)
    {
        if (!schema.TryGetProperty("$schema", out var dialect) || dialect.ValueKind != JsonValueKind.String)
            errors.Add("corpus-schema-dialect-missing");
        if (!schema.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array ||
            !required.EnumerateArray().Any(static item => string.Equals(item.GetString(), "expect", StringComparison.Ordinal)))
        {
            errors.Add("corpus-schema-expect-required");
        }
        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            errors.Add("corpus-schema-properties-missing");
    }

    private static bool TryReadEntry(
        string root,
        JsonElement entry,
        HashSet<string> ids,
        out JsonElement fixture,
        out CorpusEntry metadata,
        out string? error)
    {
        fixture = default;
        metadata = default;
        error = null;
        if (entry.ValueKind != JsonValueKind.Object ||
            !TryGetString(entry, "id", out var id) ||
            !TryGetString(entry, "file", out var file) ||
            !TryGetString(entry, "kind", out var kind) ||
            !TryGetString(entry, "disposition", out var disposition))
        {
            error = "corpus-entry-required-field-missing";
            return false;
        }
        if (!ids.Add(id))
        {
            error = "corpus-entry-id-duplicate";
            return false;
        }
        if (!IsKnownKind(kind) || !IsKnownDisposition(disposition))
        {
            error = "corpus-entry-kind-or-disposition-invalid";
            return false;
        }
        string? readError = null;
        if (!TryResolveUnderRoot(root, file, out var path) ||
            !TryReadObject(path, out fixture, out _, out readError))
        {
            error = readError ?? "corpus-case-path-invalid";
            return false;
        }
        if (!HasInt(fixture, "schema", 1) ||
            !TryGetString(fixture, "id", out var fixtureId) ||
            !string.Equals(fixtureId, id, StringComparison.Ordinal) ||
            !TryGetString(fixture, "kind", out var fixtureKind) ||
            !string.Equals(fixtureKind, kind, StringComparison.Ordinal) ||
            !TryGetString(fixture, "disposition", out var fixtureDisposition) ||
            !string.Equals(fixtureDisposition, disposition, StringComparison.Ordinal) ||
            !fixture.TryGetProperty("fixture", out var fixturePayload) ||
            fixturePayload.ValueKind != JsonValueKind.Object ||
            !fixture.TryGetProperty("expect", out var expect) ||
            expect.ValueKind != JsonValueKind.Object ||
            !expect.TryGetProperty("diagnosticIds", out var diagnostics) ||
            diagnostics.ValueKind != JsonValueKind.Array ||
            !ValidateExpectedShape(expect, diagnostics))
        {
            error = "corpus-case-schema-invalid";
            return false;
        }

        metadata = new CorpusEntry(id, kind, disposition);
        return true;
    }

    private static CorpusEvaluation EvaluateFixture(JsonElement document, string id)
    {
        var fixture = document.GetProperty("fixture");
        var diagnostics = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<string>();
        var ineligibility = new HashSet<string>(StringComparer.Ordinal);
        var repairEligible = false;

        if (fixture.TryGetProperty("elements", out var elements) && elements.ValueKind == JsonValueKind.Array)
        {
            var ids = elements.EnumerateArray()
                .Where(static element => element.ValueKind == JsonValueKind.Object)
                .Select(static element => element.TryGetProperty("automationId", out var value) ? value.GetString() : null)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToList();
            if (ids.Count != ids.Distinct(StringComparer.Ordinal).Count())
                diagnostics.Add(MauiSelectorHealthDiagnosticIds.DuplicateAutomationId);
            else if (ids.Count == 1)
                candidates.Add("automation-id");
        }

        if (fixture.TryGetProperty("selector", out var selector) && selector.ValueKind == JsonValueKind.Object)
        {
            if (selector.TryGetProperty("automationId", out _))
                candidates.Add("automation-id");
            if (selector.TryGetProperty("text", out _))
            {
                diagnostics.Add(MauiSelectorHealthDiagnosticIds.LocalizedOrDynamicText);
                candidates.Clear();
                ineligibility.Add("localized-or-dynamic-text");
            }
            if (selector.TryGetProperty("id", out _))
            {
                diagnostics.Add(MauiSelectorHealthDiagnosticIds.MissingDurableId);
                diagnostics.Add(MauiSelectorHealthDiagnosticIds.RuntimeIdOrTypeIndex);
                candidates.Clear();
                ineligibility.Add("runtime-id-selector");
            }
            if (selector.TryGetProperty("typeIndex", out _))
            {
                diagnostics.Add(MauiSelectorHealthDiagnosticIds.RuntimeIdOrTypeIndex);
                diagnostics.Add(MauiSelectorHealthDiagnosticIds.TemplateOrVirtualization);
                candidates.Clear();
                ineligibility.Add("type-index-selector");
            }
        }
        if (fixture.TryGetProperty("assertions", out var assertions) &&
            assertions.ValueKind == JsonValueKind.Array && assertions.GetArrayLength() == 0)
        {
            diagnostics.Add(MauiSelectorHealthDiagnosticIds.MissingHardPostcondition);
        }
        if (fixture.TryGetProperty("action", out _) &&
            !fixture.TryGetProperty("assertions", out _) &&
            !fixture.TryGetProperty("assertion", out _))
        {
            diagnostics.Add(MauiSelectorHealthDiagnosticIds.MissingHardPostcondition);
        }
        if (fixture.TryGetProperty("acceptanceCriterion", out _) &&
            fixture.TryGetProperty("hardAssertion", out var hardAssertion) &&
            hardAssertion.ValueKind == JsonValueKind.False)
        {
            diagnostics.Add(MauiSelectorHealthDiagnosticIds.AcceptanceCriterionUncovered);
        }
        if (fixture.TryGetProperty("assertion", out var assertion) && assertion.ValueKind == JsonValueKind.Object &&
            assertion.TryGetProperty("expected", out var expected) && assertion.TryGetProperty("actual", out var actual) &&
            !string.Equals(expected.GetString(), actual.GetString(), StringComparison.Ordinal))
        {
            diagnostics.Add(MauiSelectorHealthDiagnosticIds.MissingHardPostcondition);
            candidates.Clear();
        }
        if (fixture.TryGetProperty("recordedAutomationId", out _) && fixture.TryGetProperty("liveAutomationId", out _))
        {
            diagnostics.Add(MauiSelectorHealthDiagnosticIds.SourceAnchor);
            candidates.Add("automation-id");
        }
        if (fixture.TryGetProperty("recordedRoute", out var recordedRoute) &&
            fixture.TryGetProperty("observedRoute", out var observedRoute) &&
            !string.Equals(recordedRoute.GetString(), observedRoute.GetString(), StringComparison.Ordinal))
        {
            diagnostics.Add(MauiSelectorHealthDiagnosticIds.SourceAnchor);
            candidates.Clear();
            ineligibility.Add("checkpoint-route-mismatch");
        }
        if (fixture.TryGetProperty("recorded", out var recorded) && fixture.TryGetProperty("live", out var live) &&
            recorded.ValueKind == JsonValueKind.Object && live.ValueKind == JsonValueKind.Object &&
            recorded.TryGetProperty("type", out var recordedType) && live.TryGetProperty("type", out var liveType) &&
            !string.Equals(recordedType.GetString(), liveType.GetString(), StringComparison.Ordinal))
        {
            diagnostics.Add(MauiSelectorHealthDiagnosticIds.RuntimeIdOrTypeIndex);
            candidates.Add("role-type-ancestor");
        }
        if (fixture.TryGetProperty("requiredPlatforms", out _) &&
            fixture.TryGetProperty("androidCandidateKinds", out var androidKinds) &&
            fixture.TryGetProperty("windowsCandidateKinds", out var windowsKinds) &&
            androidKinds.GetRawText() != windowsKinds.GetRawText())
        {
            diagnostics.Add(MauiSelectorHealthDiagnosticIds.RequiredPlatform);
            candidates.Clear();
        }

        EvaluateRepairFixture(fixture, diagnostics, candidates, ineligibility, ref repairEligible);
        EvaluateSourceFixture(fixture, diagnostics, ineligibility);
        if (id.Contains("virtualized", StringComparison.Ordinal))
            ineligibility.Add("target-virtualized-unscoped");

        return new CorpusEvaluation(
            diagnostics.OrderBy(static value => value, StringComparer.Ordinal).ToList(),
            candidates.Distinct(StringComparer.Ordinal).ToList(),
            ineligibility.OrderBy(static value => value, StringComparer.Ordinal).ToList(),
            repairEligible);
    }

    private static JsonDocument GenerateNoRepairFixture(
        JsonElement source,
        string generatedId,
        int mutation)
    {
        var root = JsonNode.Parse(source.GetRawText())?.AsObject()
            ?? throw new InvalidOperationException("The no-repair corpus fixture could not be cloned.");
        root["id"] = generatedId;
        var fixture = root["fixture"]?.AsObject()
            ?? throw new InvalidOperationException("The no-repair corpus fixture payload is missing.");
        fixture["generatedMutation"] = mutation;
        if (fixture.ContainsKey("candidate") || fixture.ContainsKey("candidates"))
        {
            switch (mutation)
            {
                case 0:
                    fixture["failure"] = MauiFlowFailureClasses.ActionRejected;
                    break;
                case 1:
                    fixture["phase"] = "post-dispatch";
                    break;
                case 2:
                    fixture["trust"] = MauiArtifactTrustStates.Untrusted;
                    break;
                default:
                    fixture["unique"] = false;
                    break;
            }
        }
        return JsonDocument.Parse(root.ToJsonString());
    }

    private static void EvaluateRepairFixture(
        JsonElement fixture,
        HashSet<string> diagnostics,
        List<string> candidates,
        HashSet<string> ineligibility,
        ref bool repairEligible)
    {
        if (fixture.TryGetProperty("candidate", out _))
        {
            candidates.Add("automation-id");
            repairEligible = fixture.TryGetProperty("failure", out var failure) &&
                string.Equals(failure.GetString(), MauiFlowFailureClasses.LocatorNotFound, StringComparison.Ordinal) &&
                fixture.TryGetProperty("phase", out var phase) &&
                string.Equals(phase.GetString(), "pre-dispatch", StringComparison.Ordinal) &&
                fixture.TryGetProperty("trust", out var trust) &&
                string.Equals(trust.GetString(), "current-local-run", StringComparison.Ordinal) &&
                fixture.TryGetProperty("unique", out var unique) &&
                unique.ValueKind == JsonValueKind.True &&
                fixture.TryGetProperty("checkpoint", out var checkpoint) &&
                string.Equals(checkpoint.GetString(), "all-match", StringComparison.Ordinal) &&
                fixture.TryGetProperty("oracle", out var oracle) &&
                string.Equals(oracle.GetString(), "independent-success", StringComparison.Ordinal);
            if (!repairEligible)
                ineligibility.Add("repair-evidence-incomplete");
        }
        if (fixture.TryGetProperty("candidates", out var candidatesValue) && candidatesValue.ValueKind == JsonValueKind.Array)
        {
            var scored = candidatesValue.EnumerateArray().ToArray();
            if (scored.Length > 0 && scored[0].TryGetProperty("kind", out var kind))
                candidates.Add(kind.GetString() ?? "unknown");
            if (scored.Length > 1 && scored[0].TryGetProperty("score", out var firstScore) &&
                scored[1].TryGetProperty("score", out var secondScore) &&
                Math.Abs(firstScore.GetDouble() - secondScore.GetDouble()) < 0.05)
            {
                diagnostics.Add(MauiSelectorHealthDiagnosticIds.DuplicateAutomationId);
                ineligibility.Add("candidate-scores-too-close");
            }
        }
        if (fixture.TryGetProperty("trustStates", out _))
            ineligibility.Add("artifact-not-locally-reproduced");
        if (fixture.TryGetProperty("checkpointMismatches", out var mismatches) && mismatches.ValueKind == JsonValueKind.Array)
        {
            foreach (var mismatch in mismatches.EnumerateArray().Select(static item => item.GetString()))
            {
                switch (mismatch)
                {
                    case "route-login":
                        ineligibility.Add("checkpoint-route-mismatch");
                        break;
                    case "modal":
                        ineligibility.Add("checkpoint-modal-mismatch");
                        break;
                    case "locale":
                        ineligibility.Add("checkpoint-locale-mismatch");
                        diagnostics.Add(MauiSelectorHealthDiagnosticIds.LocalizedOrDynamicText);
                        break;
                    case "theme":
                        ineligibility.Add("checkpoint-theme-mismatch");
                        diagnostics.Add(MauiSelectorHealthDiagnosticIds.LocalizedOrDynamicText);
                        break;
                    case "seed":
                        ineligibility.Add("checkpoint-seed-mismatch");
                        break;
                    case "backend-state":
                        ineligibility.Add("checkpoint-backend-state-mismatch");
                        break;
                    case "orientation":
                        ineligibility.Add("checkpoint-orientation-mismatch");
                        break;
                    case "display":
                        ineligibility.Add("checkpoint-display-mismatch");
                        break;
                }
            }
        }
        if (fixture.TryGetProperty("sideEffectPolicy", out var policy) &&
            string.Equals(policy.GetString(), MauiFlowSideEffectPolicies.NonReplayable, StringComparison.Ordinal))
        {
            diagnostics.Add(MauiSelectorHealthDiagnosticIds.MissingHardPostcondition);
            ineligibility.Add("side-effect-policy-repair-prohibited");
        }
        if (fixture.TryGetProperty("otherFailures", out var failures) && failures.ValueKind == JsonValueKind.Array)
        {
            foreach (var failure in failures.EnumerateArray().Select(static item => item.GetString()))
                ineligibility.Add("blocking-failure-" + failure);
        }
        if (fixture.TryGetProperty("grantFailures", out _))
        {
            ineligibility.Add("approval-grant-invalid");
            ineligibility.Add("proposal-stale");
        }
        if (fixture.TryGetProperty("history", out _))
            ineligibility.Add("repair-history-invalid");
        if (fixture.TryGetProperty("validation", out _))
        {
            diagnostics.Add(MauiSelectorHealthDiagnosticIds.MissingHardPostcondition);
            ineligibility.Add("validation-failed");
            ineligibility.Add("verification-failed");
            ineligibility.Add("rollback-failed");
        }
    }

    private static void EvaluateSourceFixture(
        JsonElement fixture,
        HashSet<string> diagnostics,
        HashSet<string> ineligibility)
    {
        if (fixture.TryGetProperty("language", out var language) &&
            string.Equals(language.GetString(), "CSharp", StringComparison.Ordinal) &&
            fixture.TryGetProperty("source", out var csharp))
        {
            var source = csharp.GetString() ?? string.Empty;
            if (source.Contains("DataTemplate", StringComparison.Ordinal))
            {
                diagnostics.Add("DFCS003");
                ineligibility.Add("template-or-repeater");
                ineligibility.Add("collection-lambda-or-factory");
            }
            else if (CountLiteralAutomationIds(source) > 1)
            {
                diagnostics.Add("DFCS002");
                ineligibility.Add("automation-id-duplicate-project");
            }
            else if (source.Contains("new Button", StringComparison.Ordinal) &&
                !source.Contains("AutomationId", StringComparison.Ordinal))
            {
                diagnostics.Add("DFCS001");
                ineligibility.Add("source-proposal-required");
            }
        }
        if (fixture.TryGetProperty("xaml", out var xamlValue))
        {
            var xaml = xamlValue.GetString() ?? string.Empty;
            if (xaml.Contains("DataTemplate", StringComparison.Ordinal) || xaml.Contains("ItemTemplate", StringComparison.Ordinal))
            {
                diagnostics.Add("DFXAML003");
                ineligibility.Add("template-or-style");
                ineligibility.Add("repeater-or-virtualized");
            }
            else if (CountLiteralAutomationIds(xaml) > 1)
            {
                diagnostics.Add("DFXAML002");
                ineligibility.Add("automation-id-duplicate-project");
            }
            else if (xaml.Contains("<Button", StringComparison.Ordinal) &&
                !xaml.Contains("AutomationId", StringComparison.Ordinal))
            {
                diagnostics.Add("DFXAML001");
                ineligibility.Add("source-proposal-required");
            }
        }
    }

    private static bool MatchesExpectations(JsonElement document, CorpusEvaluation evaluation)
    {
        var expect = document.GetProperty("expect");
        var expectedDiagnostics = ReadStringArray(expect, "diagnosticIds");
        var expectedCandidates = ReadStringArray(expect, "candidateKinds");
        var expectedReasons = ReadStringArray(expect, "ineligibilityCodes");
        var diagnosticsMatch = expectedDiagnostics.SequenceEqual(evaluation.DiagnosticIds, StringComparer.Ordinal);
        var candidatesMatch = expectedCandidates.SequenceEqual(evaluation.CandidateKinds, StringComparer.Ordinal);
        var reasonsMatch = expectedReasons.All(evaluation.IneligibilityCodes.Contains);
        var eligibilityMatch = !expect.TryGetProperty("repairEligible", out var eligibility) ||
            eligibility.ValueKind is not JsonValueKind.True and not JsonValueKind.False ||
            evaluation.RepairEligible == eligibility.GetBoolean();
        return diagnosticsMatch && candidatesMatch && reasonsMatch && eligibilityMatch;
    }

    private static int CountLiteralAutomationIds(string source)
    {
        var marker = "AutomationId";
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(marker, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += marker.Length;
        }
        return count;
    }

    private static bool TryReadObject(string path, out JsonElement root, out byte[] bytes, out string? error)
    {
        root = default;
        bytes = [];
        error = null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxCorpusFileBytes)
            {
                error = "corpus-file-missing-or-too-large";
                return false;
            }
            bytes = File.ReadAllBytes(path);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "corpus-json-not-object";
                return false;
            }
            root = document.RootElement.Clone();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            error = "corpus-json-unreadable";
            return false;
        }
    }

    private static bool TryResolveUnderRoot(string root, string relative, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
            relative.Contains("..", StringComparison.Ordinal) ||
            !relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        try
        {
            path = Path.GetFullPath(Path.Combine(root, relative));
            var resolved = Path.GetRelativePath(root, path);
            return !resolved.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(resolved);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static List<string> ReadStringArray(JsonElement value, string property) =>
        value.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString()!)
                .ToList()
            : [];

    private static bool ValidateExpectedShape(JsonElement expect, JsonElement diagnostics)
    {
        foreach (var diagnostic in diagnostics.EnumerateArray())
        {
            if (diagnostic.ValueKind != JsonValueKind.String || !IsKnownDiagnosticId(diagnostic.GetString()))
                return false;
        }
        foreach (var optionalArray in new[] { "candidateKinds", "ineligibilityCodes" })
        {
            if (expect.TryGetProperty(optionalArray, out var value) &&
                (value.ValueKind != JsonValueKind.Array ||
                 value.EnumerateArray().Any(static item => item.ValueKind != JsonValueKind.String)))
            {
                return false;
            }
        }
        return !expect.TryGetProperty("repairEligible", out var repairEligible) ||
            repairEligible.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }

    private static bool IsKnownDiagnosticId(string? value)
    {
        if (value is null)
            return false;
        return (value.Length == 7 &&
                value[..4] is "DFSH" or "DFCS" &&
                value[4..].All(static character => character is >= '0' and <= '9')) ||
            (value.Length == 9 &&
             value[..6] == "DFXAML" &&
             value[6..].All(static character => character is >= '0' and <= '9'));
    }

    private static bool TryGetString(JsonElement value, string property, out string result)
    {
        result = string.Empty;
        return value.TryGetProperty(property, out var item) &&
            item.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(result = item.GetString() ?? string.Empty);
    }

    private static bool HasString(JsonElement value, string property) =>
        TryGetString(value, property, out _);

    private static bool HasInt(JsonElement value, string property, int expected) =>
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.Number &&
        item.TryGetInt32(out var actual) && actual == expected;

    private static bool HasBoolean(JsonElement value, string property, bool expected) =>
        value.TryGetProperty(property, out var item) &&
        ((expected && item.ValueKind == JsonValueKind.True) || (!expected && item.ValueKind == JsonValueKind.False));

    private static bool IsKnownKind(string value) =>
        value is "baseline" or "mutation" or "no-repair" or "product-regression" or "repair-positive" or "repair-negative";

    private static bool IsKnownDisposition(string value) =>
        value is "diagnostic-only" or "no-repair" or "repair-eligible";

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private readonly record struct CorpusEntry(string Id, string Kind, string Disposition);

    private readonly record struct CorpusEvaluation(
        List<string> DiagnosticIds,
        List<string> CandidateKinds,
        List<string> IneligibilityCodes,
        bool RepairEligible);

    private struct DeterministicRandom
    {
        private uint _state;

        public DeterministicRandom(uint seed) => _state = seed == 0 ? 0x6d2b79f5u : seed;

        public uint Next()
        {
            var value = _state;
            value ^= value << 13;
            value ^= value >> 17;
            value ^= value << 5;
            _state = value;
            return value;
        }
    }
}
