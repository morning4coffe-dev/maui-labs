# Committed qualification baseline

`qualification.json` is a real, unedited `maui devflow flow qualify` report for the static corpus on
`--platform android`. It is committed so CI can fail on a **regression** rather than only on a
threshold breach.

## What it currently says

```
status: not-qualified
corpus: 58 curated (31 repair-positive, 30 of them curated-derived; 16 no-repair), 300 generated
falseHeals:             0/316  (independent 16) [curated 0/16, generated 0/300]
repairPrecision:        31/31  (independent  1) [curated 1/1, curated-derived 30/30]
repairRecall:           31/31  (independent  1) [curated 1/1, curated-derived 30/30]
classificationAccuracy: 42/45  (independent  8) [curated 12/15, curated-derived 30/30]
selectorStability:      0/0    (no device evidence)
flakeFirstAttemptStability: not measured (no device evidence)
```

**Read the independent count, not the denominator.** `31/31` is one curated seed plus 30
`adapted-from-case` restatements of it; `42/45` is 8 genuinely inferred classifications plus 37 where
the fixture handed the classifier the answer. `tests/DevFlow/InspectorCorpus/README.md` explains both
in full. Every count gate compares `independentEvaluations`, and every lower-bound gate reads
`independentConfidenceInterval` — the interval over the independent subset alone, because the pooled
interval narrows every time a clone is added without any new fact being observed.

**`not-qualified` is the correct answer, not a defect to be fixed.** The static corpus cannot
satisfy gates that require real-device evidence, an independent review record, or n≥100 *independent*
repair evaluations. Committing a passing baseline would have required either fabricating device
evidence or lowering a threshold; both are the failure mode this baseline exists to detect.

Gates that are `not-qualified` in the baseline and why:

| Gate | Reason |
| --- | --- |
| `independent-review` | No review record is attached to a static corpus run. |
| `required-evidence` | No run report, recording, first-attempt, or artifact manifest. |
| `repair-precision` | **1** independent evaluation, threshold is 100. |
| `classification-accuracy` | **8** independent (genuinely inferred) evaluations, threshold is 100. |
| `zero-false-heals` | **16** independent no-repair evaluations, threshold is 300. |
| `selector-stability` | No device evidence. |
| `android-device-overhead` | No device evidence. |
| `android-tier1-first-attempts` | No Tier-1 flow declared and no device runs. |

## Regression diffing

```powershell
maui devflow flow qualify `
  --corpus tests/DevFlow/InspectorCorpus `
  --platform android `
  --baseline tests/DevFlow/InspectorCorpus/baselines/qualification.json
```

The comparison fails (nonzero exit) when:

- `repairPrecision`, `selectorStability`, `classificationAccuracy`, or
  `flakeFirstAttemptStability` **rate** drops below the baseline rate;
- any of those metrics' **denominator** or **independentEvaluations** falls below the baseline;
- the `falseHeals` **numerator** rises above the baseline (any escape is a regression);
- the `falseHeals` **denominator** falls below the baseline — otherwise a clean sweep could be
  manufactured by evaluating fewer cases;
- any of `corpus.curatedCases`, `curatedRepairPositiveCases`, `curatedNoRepairCases`,
  `curatedClassificationLabeledCases`, or `generatedNoRepairCases` falls below the baseline;
- a metric that had evidence in the baseline has none in the current run;
- a gate that was `pass` in the baseline is no longer `pass`, or has disappeared;
- with `--accumulate`, any accumulated metric regresses against its per-run baseline counterpart —
  including `accumulated.falseHeals` and `accumulated.abstention`, which are compared as counts.

Improvements never fail. `.github/workflows/ci-devflow.yml` runs this on every DevFlow PR.

The diff is monotone, so it cannot by itself detect a baseline that was *weakened* in the same
commit that weakened the corpus. `PreviewQualificationTests.Baseline_MatchesAFreshlyGeneratedReport`
closes that hole: it regenerates the report from the corpus on disk and asserts that the committed
`corpus` block, the committed `status`, and every metric the diff gates on (`repairPrecision`,
`repairRecall`, `falseHeals`, `abstention`, `classificationAccuracy`, `classificationMatrix`,
`selectorStability`, `recordingValidity`, `privacySecurityEscapes`) match exactly. A hand-lowered
baseline is a hard test failure. It deliberately does not compare `metrics.runtimeOverhead`,
`generatedAt`, or the assembly-derived fingerprints, which are machine dependent.

## Regenerating

Only regenerate when a change is intended to move a number, and say so in the PR:

```powershell
dotnet build src/Cli/Microsoft.Maui.Cli/Microsoft.Maui.Cli.csproj
dotnet run --project src/Cli/Microsoft.Maui.Cli/Microsoft.Maui.Cli.csproj --framework net10.0 --no-build -- `
  devflow flow qualify --corpus tests/DevFlow/InspectorCorpus --platform android `
  --output tests/DevFlow/InspectorCorpus/baselines/qualification.json --no-json
node -e "const f='tests/DevFlow/InspectorCorpus/baselines/qualification.json';const fs=require('fs');const d=JSON.parse(fs.readFileSync(f,'utf8'));d.generatedAt='1970-01-01T00:00:00+00:00';fs.writeFileSync(f,JSON.stringify(d,null,2)+'\n')"
```

`generatedAt` is pinned to the epoch so the file does not churn. `metrics.runtimeOverhead` and the
assembly-derived fingerprints are machine dependent; neither the diff nor the freshness test reads
them.

## Accumulating across runs

The stability gate needs ≥100 clean first attempts per Tier-1 flow, and the flow-QA harness
deliberately caps `--repeat` at 20. `--accumulate <dir>` merges evidence across **separate** runs
instead:

```powershell
maui devflow flow qualify --platform android --accumulate artifacts/qualification-accumulation
```

Each invocation writes `run-<fingerprint>.json` into the directory and rewrites `accumulated.json`.

A run is **rejected** from the merge when its contract version, platform, policy version, corpus
fingerprint, or any gated threshold disagrees with the reference run; when its own thresholds differ
from the compiled policy defaults (`accumulate-threshold-not-policy-default`); when it carries JSON
the contract does not model (`accumulate-unmodelled-evidence`); when a rate metric's source counts
do not add up to its totals (`accumulate-incoherent-metric`); or when its static evidence disagrees
with the reference under a matching corpus fingerprint (`accumulate-static-evidence-mismatch`).

**Static evidence is counted once, not summed.** Every accumulated run must share a corpus
fingerprint, so its curated, curated-derived, and generated counts are re-reads of the same files.
Only `device-backed` counts are summed across runs. Running the same static corpus 100 times — under
100 different `--mutation-seed` values, which do produce 100 distinct evidence fingerprints — still
yields `independentEvaluations` of 1 for `repairPrecision`. Accumulation is for real device runs;
it cannot manufacture trials out of re-reading files.
