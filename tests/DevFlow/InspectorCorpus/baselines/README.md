# Committed qualification baseline

`qualification.json` is a real `maui devflow flow qualify` report for the static corpus on
`--platform android`, unedited apart from a `generatedAt` pinned to the epoch so the file does not
churn. It is committed so CI can fail on a **regression** rather than only on a threshold breach.

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
- the `falseHeals` **denominator** or **independentEvaluations** falls below the baseline — otherwise
  a clean sweep could be manufactured by evaluating fewer cases, or by relabelling the independent
  ones as derived; `abstention` is guarded the same way;
- any of `corpus.curatedCases`, `curatedRepairPositiveCases`, `curatedNoRepairCases`,
  `curatedClassificationLabeledCases`, or `generatedNoRepairCases` falls below the baseline;
- `corpus.curatedOriginalCases` (curated minus derived) falls below the baseline — `curatedDerivedCases`
  itself is deliberately *not* frozen, because losing disclosure is not a regression but growing
  clones while originals stay flat is;
- `corpus.undeclaredProjectionCollisions` or `corpus.undeclaredShapeCollisions` rises above the
  baseline;
- `corpus.securityCorpus.caseCount`, `corpus.securityCorpus.passedCount`, or
  `metrics.privacySecurityEscapes.testCount` falls below the baseline — the privacy-security gate
  reports `pass` on three cases as happily as on eighteen, so without these floors deleting fifteen
  of them changes no gate status and no rate;
- `abstention`'s **numerator** falls below the baseline — it counts *correct* abstentions, so
  unlike `falseHeals` it must not shrink;
- a metric that had evidence in the baseline has none in the current run;
- a gate that was `pass` in the baseline is no longer `pass`, or has disappeared;
- with `--accumulate`, any accumulated metric regresses against its per-run baseline counterpart —
  including `accumulated.falseHeals` and `accumulated.abstention`, which are compared as counts.

A metric with **no** baseline evidence (denominator 0) is not protected by the rate comparison — it
has nothing to fall below. `selectorStability` and `flakeFirstAttemptStability` are in that state
today, so the diff currently guards neither. That is a consequence of having no device evidence at
all, not a design choice, and it stops being true the moment a real run is committed.

Improvements never fail. `.github/workflows/ci-devflow.yml` runs this on every DevFlow PR.

`PreviewQualificationTests.Baseline_MatchesAFreshlyGeneratedReport` regenerates the report from the
corpus on disk and asserts that the committed `corpus` block, the committed `status`, and every
metric the diff gates on (`repairPrecision`, `repairRecall`, `falseHeals`, `abstention`,
`classificationAccuracy`, `classificationMatrix`, `selectorStability`, `recordingValidity`,
`privacySecurityEscapes`) match exactly. It deliberately does not compare `metrics.runtimeOverhead`,
`generatedAt`, or the assembly-derived fingerprints, which are machine dependent.

Be clear about what that test does and does not do: it catches a **hand-edited or stale** baseline.
It does **not** catch a corpus weakened and re-baselined in the same commit — regenerating the file
makes both artifacts consistent again. What catches that is the set of monotone floors above:
`corpus.*`, `independentEvaluations`, and the gate-status comparison. Those floors are only as wide
as the fields listed, and a field with no floor is unprotected: the privacy/security corpus was
exactly that gap until its counts were floored, and any *future* evidence surface added to the
report will be too until someone adds it to the list. Reviewing what a change removes is still a
human job.

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

The reference run is the newest member of the **largest self-valid cohort** — runs grouped by
contract version, platform, policy version, and corpus fingerprint, excluding any run that would
reject itself. Picking the oldest file instead meant one leftover run from a superseded corpus
rejected every current run as a fingerprint mismatch, and the merge reported a near-empty result
with exit 0. Cohort voting is not a defence against forged input — enough forged runs would win the
vote — but a stale file is an accident that happens, and forgery is already outside what any
self-reported file can be checked for.

**Static evidence is counted once, not summed.** Every accumulated run must share a corpus
fingerprint — which covers the *contents* of every evaluated `.json` file under the corpus root,
not just the manifest, and excludes only `baselines/` (generated from the fingerprint, so hashing
it would never converge) and markdown — so its curated,
curated-derived, and generated counts are re-reads of the same files. Only `device-backed` counts
are summed across runs; an unrecognised source name is refused outright
(`accumulate-unknown-sample-source`) rather than quietly treated as one or the other. Running the
same static corpus 100 times — under 100 different `--mutation-seed` values, which do produce 100
distinct evidence fingerprints — still yields `independentEvaluations` of 1 for `repairPrecision`.
Accumulation is for real device runs; it cannot manufacture trials out of re-reading files.

**The source-name check is a spell-checker, not an authenticity check.** It rejects `null`, `""`,
`"   "`, `"Device-Backed"`, `"device-backed "` and any label the contract does not define. It does
**not** reject a fabricated `sourceCounts` entry that spells `device-backed` correctly — a
hand-written run file claiming 400 device-backed no-repair evaluations is merged verbatim. Nothing
in a self-reported JSON file can prove that a number came from a device. What the merge can do is
refuse the shapes that are obviously not evidence, and publish enough structure that a reviewer can
see what is being claimed. Trust the run files exactly as much as you trust the job that wrote them.

**Clean first attempts do accumulate**, because they are the one thing here that is a fresh device
observation per run. `accumulation.firstAttemptFlows` sums `cleanFirstAttempts` and
`passedFirstAttempts` per `flowId` across accepted runs, and `accumulated-tier1-first-attempts`
gates them at the same per-flow threshold as the single-run gate. A flow counts as device-backed
only if *every* contributing run said so. This is what makes 5 jobs × `--repeat 20` reach the ≥100
threshold without raising the cap. No run has produced any first-attempt evidence yet, so the
committed baseline reports none.

Because that sum is the only place new evidence enters, it carries the tightest checks:

- a run repeating one `flowId` in its flow list, or claiming more passes than clean attempts, is
  refused (`accumulate-incoherent-flow-evidence`) — otherwise pasting one 20/20 entry five times
  inside a single file reaches the 100-attempt threshold from 20 real attempts;
- a flow claiming `realDeviceEvidence` in a run that names no real device is refused
  (`accumulate-unattributed-device-evidence`);
- each merged flow publishes `contributingRuns` and `contributingDevices`, so 100 attempts arriving
  as 5 runs on 5 devices is legible as such — and 100 arriving as 5 runs on 1 device is too.

**Runs must be distinguishable to count.** `accumulate-duplicate-run` rejects any run whose
evidence fingerprint — contract version, platform, status, identity fingerprints, corpus summary,
platform profiles, merged rates, and per-flow first attempts — matches one already accepted. This
is what stops the same `qualification.json` being submitted five times. The practical consequence
is that CI shards must record what actually distinguishes them: a per-shard
`profiles[].deviceFingerprint`. Two shards that both report a bare 20/20 on an unnamed device are
indistinguishable evidence, and the second is dropped rather than added. That is the fail-closed
direction — it undercounts rather than inflates — but it means an accumulate job that forgets to
stamp device identity will silently plateau at one run's worth of attempts.

Say the uncomfortable part plainly: `deviceFingerprint` is a string the harness writes, and five
copies of one run file with that string edited will merge to 100 attempts. Stamping it is only
sound when the *harness* derives it from the device it actually drove. If a human types it, the sum
is not evidence, and `contributingDevices` will not know the difference. This is a reason to
generate run files from CI, never by hand — not a property the merge can enforce.

**Per-flow verdicts are not pooled.** Both `android-tier1-first-attempts` and its accumulated twin
judge each flow on its own evidence and take the worst verdict. A flow with a sufficient sample
that misses the stability threshold is a `fail` even when a sibling flow has not run yet; pooling
the reason codes used to downgrade that to `not-qualified`, which reports exit 0 on a measured
regression.
