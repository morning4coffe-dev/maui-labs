using System.Globalization;
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
    public string? ExpectedFailureClass { get; init; }
    public string? ObservedFailureClass { get; init; }
    public bool? FailureClassInferred { get; init; }
    public string? ProvenanceMethod { get; init; }
    public string? ProvenanceSourceKind { get; init; }
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

    /// <summary>Case-root keys the schema permits; anything else fails the corpus.</summary>
    private static readonly HashSet<string> KnownCaseRootProperties = new(StringComparer.Ordinal)
    {
        "schema", "id", "kind", "disposition", "fixture", "expect", "provenance", "expectedFailureClass",
    };


    /// <summary>Loads, validates, and deterministically evaluates the repository corpus.</summary>
    public static MauiPreviewQualificationCorpusRunResult Run(MauiPreviewQualificationCorpusRunRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<string>();
        var cases = new List<MauiQualificationCorpusCaseResult>();
        var samples = new List<MauiQualificationExecutionSample>();
        var noRepairFixtures = new List<(string Id, JsonElement Fixture)>();
        var fixtureShapes = new List<(string Kind, string ProvenanceMethod, SortedSet<string> Shape)>();
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
        // The manifest alone does not pin what the cases say. Two runs whose case *contents* differ
        // would otherwise share a fingerprint, and the accumulator relies on that fingerprint to
        // conclude that the static evidence in both runs is the same evidence. Everything under the
        // root is hashed — cases, schemas, and the privacy/security corpus — because all of it
        // feeds a published number.
        summary.ManifestFingerprint = Hash(Encoding.UTF8.GetBytes(
            Hash(manifestBytes) + "|" + HashCorpusTree(root)));

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
        var provenanceCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
        {
            if (!TryReadEntry(root, entry, ids, out var fixture, out var metadata, out var entryError))
            {
                errors.Add(entryError ?? "corpus-case-invalid");
                continue;
            }

            // A case adapted from another case is one piece of evidence restated. It is reported, but it
            // never counts toward a gate's minimum-evaluation requirement.
            var caseSource = metadata.ProvenanceMethod == MauiQualificationCorpusProvenanceMethods.AdaptedFromCase
                ? MauiQualificationSampleSources.CuratedDerived
                : MauiQualificationSampleSources.Curated;
            var evaluation = EvaluateFixture(fixture, metadata.Id);
            var passed = MatchesExpectations(fixture, evaluation);
            var caseResult = new MauiQualificationCorpusCaseResult
            {
                CaseId = MauiQualificationSanitizer.Fingerprint(metadata.Id),
                Source = caseSource,
                Kind = metadata.Kind,
                Disposition = metadata.Disposition,
                SchemaValid = true,
                Passed = passed,
                RepairEligible = evaluation.RepairEligible,
                ExpectedFailureClass = metadata.ExpectedFailureClass,
                ObservedFailureClass = metadata.ExpectedFailureClass is null ? null : evaluation.ObservedFailureClass,
                FailureClassInferred = metadata.ExpectedFailureClass is null ? null : evaluation.FailureClassInferred,
                ProvenanceMethod = metadata.ProvenanceMethod,
                ProvenanceSourceKind = metadata.ProvenanceSourceKind,
                DiagnosticIds = evaluation.DiagnosticIds,
                CandidateKinds = evaluation.CandidateKinds,
                IneligibilityCodes = evaluation.IneligibilityCodes,
            };
            cases.Add(caseResult);
            fixtureShapes.Add((metadata.Kind, metadata.ProvenanceMethod, FixtureShape(fixture)));
            provenanceCounts[metadata.ProvenanceSourceKind] =
                provenanceCounts.GetValueOrDefault(metadata.ProvenanceSourceKind) + 1;
            if (!passed)
                errors.Add("corpus-case-expectation-mismatch");
            if (passed && string.Equals(metadata.Disposition, "no-repair", StringComparison.Ordinal))
                noRepairFixtures.Add((metadata.Id, fixture.Clone()));

            samples.Add(new MauiQualificationExecutionSample
            {
                SampleId = MauiQualificationSanitizer.Fingerprint(metadata.Id),
                Source = caseSource,
                Category = metadata.Kind,
                Platform = request.Platform,
                NoRepairExpected = string.Equals(metadata.Disposition, "no-repair", StringComparison.Ordinal),
                RepairProposed = evaluation.RepairEligible,
                RepairExpected = string.Equals(metadata.Disposition, "repair-eligible", StringComparison.Ordinal),
                RepairCorrect = evaluation.RepairEligible ? passed : null,
                FalseHeal = string.Equals(metadata.Disposition, "no-repair", StringComparison.Ordinal) && evaluation.RepairEligible,
                Abstained = string.Equals(metadata.Disposition, "no-repair", StringComparison.Ordinal) && !evaluation.RepairEligible,
                // Ground truth is hand-assigned per case; unlabeled advisory cases stay out of the
                // classification denominator instead of being scored against a fabricated label.
                ExpectedFailureClass = metadata.ExpectedFailureClass,
                ObservedFailureClass = metadata.ExpectedFailureClass is null ? null : evaluation.ObservedFailureClass,
                FailureClassInferred = metadata.ExpectedFailureClass is null ? null : evaluation.FailureClassInferred,
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
        summary.CuratedRepairPositiveCases = cases.Count(static item =>
            string.Equals(item.Disposition, "repair-eligible", StringComparison.Ordinal));
        summary.CuratedDerivedCases = cases.Count(static item =>
            string.Equals(item.ProvenanceMethod, "adapted-from-case", StringComparison.Ordinal));
        summary.CuratedNoRepairCases = cases.Count(static item =>
            string.Equals(item.Disposition, "no-repair", StringComparison.Ordinal));
        summary.GeneratedNoRepairCases = samples.Count(static item =>
            item.Source == MauiQualificationSampleSources.Generated && item.NoRepairExpected == true);
        summary.CuratedClassificationLabeledCases = cases.Count(static item => item.ExpectedFailureClass is not null);
        // The curated-versus-derived split is self-declared. A case that copies a seed and simply
        // does not say so is counted as independent evidence, which is exactly the inflation this
        // corpus exists to disclose. Two cases whose *evaluated projection* is identical produce
        // the same evidence whatever their provenance says, so count the ones that collide without
        // declaring a seed. This is a disclosure, not a rejection: legitimately similar cases exist.
        summary.UndeclaredProjectionCollisions = cases
            .Where(static item => item.SchemaValid &&
                !string.Equals(item.ProvenanceMethod, "adapted-from-case", StringComparison.Ordinal))
            .GroupBy(EvaluationProjection, StringComparer.Ordinal)
            .Sum(static group => Math.Max(0, group.Count() - 1));
        // The projection above compares evaluation *outputs*, so a clone that perturbs an
        // evidence-neutral fixture value until its diagnostics differ escapes it. This second
        // counter compares fixture *shape* — the set of key paths, values ignored — and counts a
        // case whose shape contains another same-kind case's shape, so neither changing a value
        // nor bolting on an extra key escapes it. Neither counter proves a case is original;
        // together they make an undeclared restatement of an existing seed something a reviewer
        // has to argue for rather than something that passes unremarked. A nonzero value is not by
        // itself wrong — genuinely distinct cases can ask a strictly wider version of the same
        // question — which is why this is a floor to hold, not a gate to pass.
        summary.UndeclaredShapeCollisions = CountShapeContainments(fixtureShapes);
        summary.ProvenanceComplete = cases.Count > 0 && cases.All(static item =>
            !string.IsNullOrEmpty(item.ProvenanceMethod) && !string.IsNullOrEmpty(item.ProvenanceSourceKind));
        summary.ProvenanceSourceCounts = provenanceCounts
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => new MauiQualificationCorpusProvenanceCount
            {
                SourceKind = pair.Key,
                Count = pair.Value,
            })
            .ToList();
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
        if (required.ValueKind != JsonValueKind.Array ||
            !required.EnumerateArray().Any(static item => string.Equals(item.GetString(), "provenance", StringComparison.Ordinal)))
        {
            errors.Add("corpus-schema-provenance-required");
        }
        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
        {
            errors.Add("corpus-schema-properties-missing");
            return;
        }
        if (!properties.TryGetProperty("provenance", out _))
            errors.Add("corpus-schema-provenance-property-missing");
        if (!properties.TryGetProperty("expectedFailureClass", out _))
            errors.Add("corpus-schema-expected-failure-class-property-missing");
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
        if (!TryReadProvenance(fixture, out var provenanceMethod, out var provenanceSourceKind))
        {
            error = "corpus-case-provenance-invalid";
            return false;
        }
        // The schema declares additionalProperties:false at the case root; enforce it here so a
        // typo such as "expectedFailureclass" fails the corpus instead of silently dropping the
        // case out of the classification denominator.
        foreach (var property in fixture.EnumerateObject())
        {
            if (!KnownCaseRootProperties.Contains(property.Name))
            {
                error = "corpus-case-unknown-property";
                return false;
            }
        }
        string? expectedFailureClass = null;
        if (fixture.TryGetProperty("expectedFailureClass", out var expectedClass))
        {
            if (expectedClass.ValueKind != JsonValueKind.String ||
                !MauiFlowFailureClassifier.IsKnownFailureClass(expectedClass.GetString()))
            {
                error = "corpus-case-expected-failure-class-invalid";
                return false;
            }
            expectedFailureClass = expectedClass.GetString();
        }

        metadata = new CorpusEntry(id, kind, disposition, expectedFailureClass, provenanceMethod, provenanceSourceKind);
        return true;
    }

    /// <summary>
    /// Reads the required per-case provenance record. The corpus fails closed when a case does not
    /// record who labeled it, when, how, and from what kind of source.
    /// </summary>
    private static bool TryReadProvenance(JsonElement document, out string method, out string sourceKind)
    {
        method = string.Empty;
        sourceKind = string.Empty;
        if (!document.TryGetProperty("provenance", out var provenance) || provenance.ValueKind != JsonValueKind.Object)
            return false;
        if (!TryGetString(provenance, "labeledBy", out var labeledBy) || labeledBy.Length > 128)
            return false;
        if (!TryGetString(provenance, "labeledOn", out var labeledOn) || !IsIsoDate(labeledOn))
            return false;
        if (!TryGetString(provenance, "method", out var candidateMethod) ||
            !MauiQualificationCorpusProvenanceMethods.IsKnown(candidateMethod))
        {
            return false;
        }
        if (!TryGetString(provenance, "sourceKind", out var candidateSource) ||
            MauiQualificationCorpusProvenanceSourceKinds.Normalize(candidateSource) ==
                MauiQualificationCorpusProvenanceSourceKinds.Unknown)
        {
            return false;
        }
        if (!TryGetString(provenance, "reviewStatus", out var reviewStatus) ||
            reviewStatus is not ("unreviewed" or "peer-reviewed"))
        {
            return false;
        }
        // A derived case must name the case it came from, otherwise the artifact cannot show that
        // its denominator is one seed restated rather than independent evidence.
        if (candidateMethod == MauiQualificationCorpusProvenanceMethods.AdaptedFromCase &&
            (!TryGetString(provenance, "derivedFrom", out var derivedFrom) ||
             derivedFrom.Length is 0 or > 128))
        {
            return false;
        }
        method = candidateMethod;
        sourceKind = candidateSource;
        return true;
    }

    private static bool IsIsoDate(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

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

        var classification = MauiFlowFailureClassifier.Classify(BuildFailureFacts(fixture));
        return new CorpusEvaluation(
            diagnostics.OrderBy(static value => value, StringComparer.Ordinal).ToList(),
            candidates.Distinct(StringComparer.Ordinal).ToList(),
            ineligibility.OrderBy(static value => value, StringComparer.Ordinal).ToList(),
            repairEligible,
            classification.FailureClass,
            // The classifier reports which input decided the answer. Anything other than "inferred"
            // means the class was read off a fixture field that already named it -- a stamped
            // failure class, a terminal outcome, or an otherFailures flag. Those are correct by
            // construction and must never be presented as evidence that classification works.
            classification.Basis == MauiFlowClassificationBases.Inferred);
    }

    /// <summary>
    /// Projects a corpus fixture onto the same replay facts a live run supplies, so the shipping
    /// <see cref="MauiFlowFailureClassifier"/> produces the observed class. No expected label is read
    /// here: the corpus ground truth never feeds the classifier under measurement.
    /// </summary>
    private static MauiFlowFailureFacts BuildFailureFacts(JsonElement fixture)
    {
        var facts = new MauiFlowFailureFacts();
        if (TryGetString(fixture, "terminalOutcome", out var terminalOutcome))
            facts.TerminalOutcome = terminalOutcome;
        if (TryGetString(fixture, "failure", out var recordedClass))
            facts.FailureClass = recordedClass;
        if (fixture.TryGetProperty("otherFailures", out var otherFailures) && otherFailures.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in otherFailures.EnumerateArray().Select(static item => item.GetString()))
            {
                switch (value)
                {
                    case MauiFlowFailureClasses.UnknownCompletion:
                        facts.CompletionCertain = false;
                        break;
                    case MauiFlowFailureClasses.AgentDisconnected:
                        facts.AgentDisconnected = true;
                        break;
                    case MauiFlowFailureClasses.Transport:
                        facts.TransportFailure = true;
                        break;
                    case MauiFlowFailureClasses.ActionRejected:
                        facts.ActionRejected = true;
                        break;
                    case MauiFlowFailureClasses.ResetFailed:
                        facts.ResetFailed = true;
                        break;
                    case MauiFlowFailureClasses.CapabilityMissing:
                        facts.CapabilityMissing = true;
                        break;
                    case MauiFlowFailureClasses.FlowInvalid:
                        facts.FlowInvalid = true;
                        break;
                    case MauiFlowFailureClasses.SchemaUnsupported:
                        facts.SchemaUnsupported = true;
                        break;
                }
            }
        }
        facts.LegacyFailureKind = InferLegacyFailureKind(fixture);
        if (TryGetString(fixture, "checkpoint", out var checkpoint))
        {
            facts.CheckpointVerified = true;
            facts.CheckpointMatches = string.Equals(checkpoint, "all-match", StringComparison.Ordinal);
            facts.RouteMatches = facts.CheckpointMatches;
        }
        if (fixture.TryGetProperty("checkpointMismatches", out var mismatches) && mismatches.ValueKind == JsonValueKind.Array)
        {
            facts.CheckpointVerified = true;
            facts.CheckpointMatches = mismatches.GetArrayLength() == 0;
            if (mismatches.EnumerateArray().Any(static item =>
                string.Equals(item.GetString(), "route-login", StringComparison.Ordinal)))
            {
                facts.RouteMatches = false;
            }
        }
        if (fixture.TryGetProperty("recordedRoute", out var recordedRoute) &&
            fixture.TryGetProperty("observedRoute", out var observedRoute))
        {
            facts.CheckpointVerified = true;
            facts.RouteMatches = string.Equals(recordedRoute.GetString(), observedRoute.GetString(), StringComparison.Ordinal);
        }
        if (TryGetString(fixture, "phase", out var phase))
            facts.BeforeDispatch = string.Equals(phase, "pre-dispatch", StringComparison.Ordinal);
        return facts;
    }

    /// <summary>Derives the legacy replay failure kind from observable fixture structure only.</summary>
    private static string? InferLegacyFailureKind(JsonElement fixture)
    {
        if (fixture.TryGetProperty("assertion", out var assertion) && assertion.ValueKind == JsonValueKind.Object &&
            assertion.TryGetProperty("expected", out var expected) && assertion.TryGetProperty("actual", out var actual) &&
            !string.Equals(expected.GetString(), actual.GetString(), StringComparison.Ordinal))
        {
            return FlowFailureKinds.Assertion;
        }
        if (fixture.TryGetProperty("candidates", out var scored) && scored.ValueKind == JsonValueKind.Array &&
            scored.GetArrayLength() > 1)
        {
            return FlowFailureKinds.Ambiguous;
        }
        if (fixture.TryGetProperty("elements", out var elements) && elements.ValueKind == JsonValueKind.Array)
        {
            var ids = elements.EnumerateArray()
                .Where(static element => element.ValueKind == JsonValueKind.Object)
                .Select(static element => element.TryGetProperty("automationId", out var value) ? value.GetString() : null)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            if (ids.Count != ids.Distinct(StringComparer.Ordinal).Count())
                return FlowFailureKinds.Ambiguous;
        }
        if (fixture.TryGetProperty("candidate", out _) ||
            fixture.TryGetProperty("oldSelector", out _) ||
            fixture.TryGetProperty("recordedAutomationId", out _) ||
            fixture.TryGetProperty("recorded", out _) ||
            fixture.TryGetProperty("selector", out _))
        {
            return FlowFailureKinds.NotFound;
        }
        return null;
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

    /// <summary>
    /// Everything about a case that changes what the evaluation observes. Case ids are excluded
    /// even though <c>EvaluateFixture</c> does sniff one substring out of them, because renaming a
    /// file is not new evidence; that id sniff is a quirk of the evaluator, not a property of the
    /// case. This projection compares evaluation outputs only — see <c>FixtureShape</c> for the
    /// complementary input-side check.
    /// </summary>
    private static string EvaluationProjection(MauiQualificationCorpusCaseResult item) =>
        string.Join('|',
            item.Kind,
            item.Disposition,
            item.RepairEligible,
            item.Passed,
            item.ExpectedFailureClass,
            item.ObservedFailureClass,
            item.FailureClassInferred,
            string.Join(',', item.DiagnosticIds.OrderBy(static id => id, StringComparer.Ordinal)),
            string.Join(',', item.CandidateKinds.OrderBy(static kind => kind, StringComparer.Ordinal)),
            string.Join(',', item.IneligibilityCodes.OrderBy(static code => code, StringComparer.Ordinal)));

    /// <summary>
    /// The set of key paths in a fixture, with all values discarded. Two cases with the same shape
    /// ask the evaluator the same question with different numbers in it; a case whose shape
    /// contains another's asks that same question plus something extra.
    /// </summary>
    private static SortedSet<string> FixtureShape(JsonElement fixture)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        Walk(fixture, string.Empty);
        return paths;

        void Walk(JsonElement element, string prefix)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                        Walk(property.Value, prefix.Length == 0 ? property.Name : prefix + "." + property.Name);
                    break;
                case JsonValueKind.Array:
                    // Element order and count are values, not shape; a clone cannot escape by
                    // shortening a list.
                    paths.Add(prefix + "[]");
                    foreach (var item in element.EnumerateArray())
                        Walk(item, prefix + "[]");
                    break;
                default:
                    paths.Add(prefix);
                    break;
            }
        }
    }

    private static int CountShapeContainments(
        List<(string Kind, string ProvenanceMethod, SortedSet<string> Shape)> shapes)
    {
        var count = 0;
        foreach (var kind in shapes
            .Where(static item => !string.Equals(item.ProvenanceMethod, "adapted-from-case", StringComparison.Ordinal))
            .GroupBy(static item => item.Kind, StringComparer.Ordinal))
        {
            var ordered = kind.Select(static item => item.Shape).OrderBy(static shape => shape.Count).ToList();
            for (var index = 1; index < ordered.Count; index++)
            {
                if (ordered.Take(index).Any(earlier => earlier.IsSubsetOf(ordered[index])))
                    count++;
            }
        }
        return count;
    }

    /// <summary>
    /// Hashes every evaluated file under the corpus root, not just the manifest or the case
    /// directory: the privacy/security corpus and the schemas also feed published numbers, and a
    /// fingerprint that ignores them lets those numbers change while the accumulator still calls
    /// two runs the same static evidence.
    /// <para>
    /// Two exclusions, both deliberate. <c>baselines/</c> holds the report generated *from* this
    /// fingerprint, so hashing it would make the fingerprint a fixed point that no regeneration
    /// ever reaches. Documentation (<c>*.md</c>) is excluded because it is not evaluated — every
    /// case is enumerated by the manifest and every fixture is JSON — and hashing prose would fail
    /// the baseline diff on a typo fix, teaching exactly the reflexive "just regenerate it" habit
    /// these gates exist to prevent. Anything that is read to produce a number is a
    /// <c>.json</c> file and is hashed.
    /// </para>
    /// Line endings are normalised so a CRLF checkout of an unchanged corpus hashes the same as an
    /// LF one.
    /// </summary>
    private static string HashCorpusTree(string root)
    {
        if (!Directory.Exists(root))
            return "no-corpus";
        var builder = new StringBuilder();
        foreach (var file in Directory.GetFiles(root, "*.json", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(static path => !path.StartsWith("baselines/", StringComparison.Ordinal))
            .OrderBy(static path => path, StringComparer.Ordinal))
        {
            builder.Append(file).Append('=');
            try
            {
                var bytes = File.ReadAllBytes(Path.Combine(root, file));
                builder.Append(Hash(Encoding.UTF8.GetBytes(
                    Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal))));
            }
            catch (IOException)
            {
                builder.Append("unreadable");
            }
            catch (UnauthorizedAccessException)
            {
                builder.Append("unreadable");
            }
            builder.Append(';');
        }
        return Hash(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private readonly record struct CorpusEntry(
        string Id,
        string Kind,
        string Disposition,
        string? ExpectedFailureClass,
        string ProvenanceMethod,
        string ProvenanceSourceKind);

    private readonly record struct CorpusEvaluation(
        List<string> DiagnosticIds,
        List<string> CandidateKinds,
        List<string> IneligibilityCodes,
        bool RepairEligible,
        string ObservedFailureClass,
        bool FailureClassInferred);

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
