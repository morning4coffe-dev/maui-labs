# Inspector selector-health corpus

Static fixtures for the DevFlow repair/advisory evaluator. `MauiPreviewQualificationCorpusRunner`
loads `corpus-manifest.json`, validates every case against
`schemas/selector-health-corpus-v1.json`, replays the fixture through the shipping evaluation
logic, and emits qualification samples.

**This corpus is static. Nothing in it is a device run.** Every number derived from it should be
read as "the evaluator behaves this way on hand-written fixtures", never as "the product behaves
this way on real applications".

## Case anatomy

| Field | Required | Meaning |
| --- | --- | --- |
| `schema` | yes | Always `1`. |
| `id` | yes | Stable case id, also the file name. |
| `kind` | yes | `selector-health` or `source-advisory`. |
| `disposition` | yes | `repair`, `no-repair`, `advisory`, or `no-proposal`. |
| `expectedFailureClass` | no | Hand-assigned ground truth (see below). |
| `provenance` | yes | Who labeled the case, when, how, and from what source. |
| `fixture` | yes | The evaluator input. |
| `expect` | yes | Expected diagnostic ids, candidate kinds, and repair eligibility. |

The case root is `additionalProperties: false`, so an unrecognized field fails the corpus rather
than being silently ignored.

## Provenance

Before this scheme existed, **who labeled a case and on what evidence was unrecorded.** Every case
now carries:

```json
"provenance": {
  "labeledBy": "devflow-maintainers",
  "labeledOn": "2026-08-15",
  "method": "hand-authored",
  "sourceKind": "synthetic",
  "reviewStatus": "unreviewed",
  "derivedFrom": "repair-positive-unique-locator-drift",
  "notes": "..."
}
```

| Field | Values | Notes |
| --- | --- | --- |
| `labeledBy` | free text, ≤128 chars | Team or role, not an individual identity. |
| `labeledOn` | `YYYY-MM-DD` | |
| `method` | `hand-authored`, `adapted-from-case`, `derived-from-replay`, `derived-from-incident` | `adapted-from-case` **must** set `derivedFrom`. |
| `sourceKind` | `synthetic`, `observed-local-run`, `observed-ci-run`, `reported-issue` | Only the two `observed-*` values mean a real run was involved. |
| `reviewStatus` | `unreviewed`, `peer-reviewed` | |

`TryReadProvenance` fails closed: a case with a missing or malformed provenance block is a corpus
error (`corpus-case-provenance-invalid`), not a warning.

The report surfaces `corpus.provenanceComplete` and `corpus.provenanceSourceCounts`. **Today every
case is `synthetic`**, so `provenanceSourceCounts` is a single `synthetic` bucket. That is the point:
the field makes the absence of observed evidence visible in the artifact instead of leaving it to be
inferred.

## Curated versus generated denominators

The runner also generates no-repair mutants from the curated seeds. These are **not independent
trials** — they are deterministic permutations (seed `20260802`) of a small seed set, so a clean
sweep across them is roughly one piece of evidence repeated, not N.

The report therefore splits every rate metric by source (`metrics.*.sourceCounts`) and splits the
corpus summary:

| Field | Current value |
| --- | --- |
| `curatedCases` | 58 |
| `curatedRepairPositiveCases` | 31 |
| `curatedNoRepairCases` | 16 |
| `generatedNoRepairCases` | 300 |
| `curatedClassificationLabeledCases` | 45 |

So the false-heal metric reads `0/316` = **16 curated + 300 generated**, and never `316` independent
observations.

## `expectedFailureClass` ground truth

`expectedFailureClass` is the class **a maintainer expects the product to report**, judged from the
scenario alone, drawn from the closed `MauiFlowFailureClasses` set. It is compared against the class
the shipping `MauiFlowFailureClassifier` actually produces to build
`metrics.classificationMatrix` and `metrics.classificationAccuracy`.

Rules:

- **The ground truth never feeds the classifier.** `BuildFailureFacts` projects the fixture onto
  `MauiFlowFailureFacts` using fixture structure only and does not read `expectedFailureClass`.
- **Generated mutants are deliberately unlabeled.** Machine-generating both the input and its
  expected label would make the metric tautological, so the classification denominator is curated
  cases only.
- **Cases that do not model a runtime replay failure are unlabeled** (source advisories, grant/
  redaction policy cases, and the baseline healthy cases). `curatedClassificationLabeledCases`
  versus `curatedCases` shows that coverage gap explicitly.

### Known limitation

Where a fixture carries a `failure` field, that value is passed through as
`MauiFlowFailureFacts.FailureClass` — the class the replayer stamped at failure time. The classifier
honours a known stamp before it falls back to inference. For those cases the metric is measuring
**stamp-to-report mapping fidelity**, not inference from raw evidence. Cases without a `failure`
field (for example `no-repair-route-state-drift`) exercise the inference path, including the
route/checkpoint downgrade.

### Class skew

31 of the 45 labeled cases are `locator-not-found` repair positives. A single pooled accuracy number
is therefore dominated by one class; read `metrics.classificationMatrix.perClass` for the per-class
precision and recall instead.

## Adding a case

1. Add `cases/<id>.json` with the full schema above, including `provenance`.
2. Add the id to `corpus-manifest.json`.
3. Run `dotnet test src/DevFlow/Microsoft.Maui.DevFlow.Tests --filter FullyQualifiedName~PreviewQualificationTests`.
4. If the case changes a reported metric, regenerate `baselines/qualification.json` (see
   `baselines/README.md`).

Notes:

- Any id containing `virtualized` force-adds the `target-virtualized-unscoped` ineligibility code.
- `expect.candidateKinds` must match the evaluated candidate list exactly, in order.
- `SelectorHealthTests.Corpus_ContainsStaticSchemaAndAtLeastQuarterNoRepairCases` requires
  `noRepairCount * 4 >= caseCount`. At 16 no-repair and 58 total there are 6 cases of headroom;
  adding more non-no-repair cases needs matching no-repair cases.
- Never commit a contiguous secret-shaped literal, even as a fixture value. Use obvious
  placeholders.
