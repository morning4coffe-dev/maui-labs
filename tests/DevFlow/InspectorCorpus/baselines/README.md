# Committed qualification baseline

`qualification.json` is a real, unedited `maui devflow flow qualify` report for the static corpus on
`--platform android`. It is committed so CI can fail on a **regression** rather than only on a
threshold breach.

## What it currently says

```
status: not-qualified
corpus: 58 curated (31 repair-positive, 16 no-repair), 300 generated no-repair
falseHeals:             0/316   [curated 0/16, generated 0/300]
repairPrecision:        31/31   [curated 31/31]
repairRecall:           31/31   [curated 31/31]
classificationAccuracy: 42/45   [curated 42/45]
selectorStability:      0/0     (no device evidence)
flakeFirstAttemptStability: not measured (no device evidence)
```

**`not-qualified` is the correct answer, not a defect to be fixed.** The static corpus cannot
satisfy gates that require real-device evidence, an independent review record, or n≥100 repair
evaluations. Committing a passing baseline would have required either fabricating device evidence
or lowering a threshold; both are the failure mode this baseline exists to detect.

Gates that are `not-qualified` in the baseline and why:

| Gate | Reason |
| --- | --- |
| `independent-review` | No review record is attached to a static corpus run. |
| `required-evidence` | No run report, recording, first-attempt, or artifact manifest. |
| `repair-precision` | 31 evaluations, threshold is 100. |
| `classification-accuracy` | 45 labeled evaluations, threshold is 100. |
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
- the `falseHeals` **numerator** rises above the baseline (any escape is a regression);
- the `falseHeals` **denominator** falls below the baseline — otherwise a clean sweep could be
  manufactured by evaluating fewer cases;
- a metric that had evidence in the baseline has none in the current run;
- a gate that was `pass` in the baseline is no longer `pass`, or has disappeared.

Improvements never fail. `.github/workflows/ci-devflow.yml` runs this on every DevFlow PR.

## Regenerating

Only regenerate when a change is intended to move a number, and say so in the PR:

```powershell
dotnet build src/Cli/Microsoft.Maui.Cli/Microsoft.Maui.Cli.csproj
dotnet run --project src/Cli/Microsoft.Maui.Cli/Microsoft.Maui.Cli.csproj --framework net10.0 --no-build -- `
  devflow flow qualify --corpus tests/DevFlow/InspectorCorpus --platform android `
  --output tests/DevFlow/InspectorCorpus/baselines/qualification.json --no-json
node -e "const f='tests/DevFlow/InspectorCorpus/baselines/qualification.json';const fs=require('fs');fs.writeFileSync(f,JSON.stringify(JSON.parse(fs.readFileSync(f,'utf8')),null,2)+'\n')"
```

`generatedAt`, `metrics.runtimeOverhead`, and the assembly-derived fingerprints are machine and
time dependent and will churn on regeneration. The diff does not read them.

## Accumulating across runs

The stability gate needs ≥100 clean first attempts per Tier-1 flow, and the flow-QA harness
deliberately caps `--repeat` at 20. `--accumulate <dir>` merges numerators and denominators across
**separate** runs instead:

```powershell
maui devflow flow qualify --platform android --accumulate artifacts/qualification-accumulation
```

Each invocation writes `run-<fingerprint>.json` into the directory and rewrites
`accumulated.json`. A run is rejected from the merge when its contract version, platform, policy
version, corpus fingerprint, or any gated threshold disagrees with the reference run, and runs are
deduplicated by an evidence fingerprint that excludes wall-clock time — so re-running the same
static corpus 100 times still counts once.
