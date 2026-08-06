# DevFlow human-authored tests

> Experimental preview. A plan describes intent and safety; only a committed Markdown flow is
> executable.

For release-like/uninstrumented builds and OS-owned UI, use the separate
[Appium black-box smoke lane](appium-smoke-testing.md). It is not a DevFlow flow executor and
cannot qualify or repair a semantic flow.

For repeatable Android, Windows, iOS Simulator/Mac Catalyst, and separately labeled experimental
AppKit host handoff commands, see [platform flow QA](flow-qa.md). Physical iOS remains
unavailable/pending until a signed-device harness exists; Simulator evidence never certifies it.
Apple Test Agent
status is explicitly reported as pending until a returned macOS capability and Tier-1 artifact set
is reviewed; it is never reported as a passing platform result from Windows or source-only checks.

Open `maui devflow inspect`, select **Tests**, then follow **Goal**, **Steps**, and **Review**.

1. Enter the required Goal, then choose **Record steps**. This is the only required field before
   recording. Optional metadata is separated into collapsed identity, outcome, setup/safety, and
   review groups.
2. Stop recording to open **Review**. The draft remains available even when it cannot yet
   be saved as a managed test.
3. Review the compact recorded-step list. Select one step to rename, reorder, remove, or edit its
   selector. **Record more steps** appends another recording to the existing draft.
4. Add an expected result to the selected step: target exists, property equals, route equals, or
   observation-only page changed. Secret-shaped values are not prefilled or persisted.
5. Choose **Check test**, inspect **Review changes**, then explicitly choose **Save test**.
   Save never runs a test.

### Work with your coding agent

From **Goal**, expand **Create this test with your agent** and copy the prepared prompt into your
coding-agent chat. The agent prepares the complete draft and expected results, then its request
appears under **Requests**. Review the test before choosing **Save test**; running it is always a
second, separate **Allow one run** decision.

After a failure, **Results** offers **Diagnose this failure with your agent**. The agent can explain
the bounded failure and prepare an inert control-update suggestion, but it cannot apply the
repair. Copying the prompt creates a ten-minute, run-bound handoff containing the exact failed run,
target, and read capabilities. The agent calls `maui_test_failure` directly; it does not create a
draft, search for a “latest” run, migrate a flow, or infer that omitted checkpoint facts are
mismatches. Only an explicitly eligible pre-dispatch missing-selector result may continue to one
inert selector proposal. **Repair** keeps review, validation, approval, and apply under human control. In
**Improve**, the equivalent agent prompt asks for a read-only quality review of fragile controls,
missing expected results, and incomplete coverage.

The five workflow tabs reveal only the current usable action. Missing prerequisites link back to
the tab that can satisfy them instead of showing disabled future controls. Recording/recovery,
additional outcome checks, run/evidence details, compatibility replay, diagnostic import, and
technical trace data stay collapsed until they become relevant or the user explicitly opens them.

The tab row also enforces prerequisites: Steps requires a Goal; Review requires recorded steps; Run
requires a saved test with an expected result; Agent requests requires request history; Repair
requires a failed local result; Improve requires a loaded flow; and Source requires a selected
source-mapped control. Disabled tabs explain their unlock condition and are skipped by keyboard tab
navigation. Results unlocks only after a run produces a result. Use the separate **Import result** toolbar action for
read-only diagnostic-result import; it cannot unlock or bypass Goal, Steps, Review, or Run.

The plan sidecar is:

```text
maui-tests/<flow-base>.maui-plan.json
```

It uses [test-plan-v1](spec/schemas/maui-test-plan-v1.json), includes the canonical flow
filename/digest, and preserves additive unknown fields. Save/commit uses the loaded flow digest,
plan revision, and plan digest for optimistic concurrency. A stale revision requires explicit
overwrite confirmation.

All local writes are confined to the registered project’s top-level `maui-tests` directory, reject
symbolic links/reparse points, cap files at 1 MB, and stage both flow and plan before replacing the
committed bundle. Browser hosts download artifacts when a workspace cannot be resolved. VS Code and
Canvas may save the same bounded bundle through the host bridge; no host gets an arbitrary path.

Saving or committing never starts replay. Run/Trace and **Improve** are separate, read-only
diagnostic surfaces; they never rewrite the loaded flow, select a fallback, apply a repair, or
write application source.

Recording retains only successful semantic interactions that can be replayed. A tap on a plain
page or layout container with no interactive role or tap gesture is ignored rather than becoming
a fragile type/index step. Interactive controls and gesture-bearing containers remain recordable.

Run readiness separates admission blockers from verification limitations. A stale canonical
flow/plan digest binding must be reviewed and saved again before Run unlocks. A missing independent
business oracle is non-blocking for ordinary replay: the run may execute, but its result is reported
as not independently verified.

## Restricted AI test authoring

`maui devflow mcp --profile test-agent` can create broker-owned drafts through a restricted typed
protocol. It never receives broad app automation, SecureStorage, raw files/network/CDP/source
access, generic action invocation, or repair/source apply authority. Every effectful request names
the exact agent process and uses a human-issued grant bound to the target/build/seed and current
plan/flow revision/digest. The agent may request bounded exploration, but only a human Workbench or
host action can issue a grant. See [test-agent.md](test-agent.md).

## Selector health

In the Inspector, Improve starts with a single **Scan test** action only after a flow exists.
Live-tree input, filters, and coverage stay in optional expanders; findings are not shown before a
scan.

The Improve tab runs the deterministic `MauiSelectorHealthAnalyzer` against the loaded flow/plan,
optional bounded live tree, and optional retained run history. It groups stable findings by
severity, category, step, source, and platform. **Rescan** is explicit; a flow/plan edit marks the
previous result stale.

`MauiElementFingerprint` is value-free: it retains identity/context, source/topology, collection,
and normalized geometry facts, but never rendered text, entered values, or screenshots. Candidate
priority is fixed: unique app-owned AutomationId, scoped stable item key, authoritative native id,
role/type under a stable ancestor, current source plus topology, and locale-bound exact text.
Runtime IDs, coordinates, type/index alone, screenshot similarity, ambiguous matches, and
unscoped virtualized rows are rejected.

For repeated controls, keep the child `AutomationId` and bind an app-owned stable model ID on the
item-template root:

```csharp
border.SetBinding(
    Microsoft.Maui.DevFlow.Agent.Core.DevFlowTest.StableItemKeyProperty,
    nameof(TodoItem.Id));
```

DevFlow propagates that identity to the realized child controls and records the executable
composite `AutomationId + collectionScope + stableItemKey`. This avoids visible text and
type/index ordering while still identifying one repeated item after reset or virtualization.
The raw app item ID is SHA-256 pseudonymized inside the in-app agent before it reaches a flow,
report, Inspector, or MCP response. The item ID must remain stable for the reset seed declared by
the test.

Candidate rank is a transparent deterministic rule score, not a probability. Calibration is
always `uncalibrated` in this preview. The stable diagnostic IDs are `DFSH001` through `DFSH011`:
duplicate/missing IDs, fragile selector forms, localization, template/virtualization, source
anchor, native divergence, platform parity, assertion gaps, acceptance-criterion coverage, and
route/platform coverage. No model or LLM ranks candidates. See the Testing package README for the
full ID table and score weights.

Synthetic baseline, mutation, and explicit no-repair fixtures live under
[`tests/DevFlow/InspectorCorpus/`](../../tests/DevFlow/InspectorCorpus/). They require no emulator;
at least one quarter are product-regression/no-repair cases so diagnostic work cannot be mistaken
for automatic healing.

## Imported run reports and evidence

Imported `flow-run.json` and `.mauitrace` v1 files are diagnostics, not executable test inputs.
The broker assigns each accepted import a fresh opaque `imported-artifact` identity, retains only a
bounded redacted projection in memory, and returns a per-artifact capability token. It never
replays the file, writes it to the workspace, or appends repair history. Raw imported bytes have no
download route.

Every import starts `untrusted`. A trusted host may mark it `attested` only after independently
verifying caller-supplied provenance facts against configured repository, workflow, commit, and
digest policy; the core does not call CI, GitHub, or issuer APIs. ZIP/report entry hashes prove
integrity only. Neither matching embedded IDs nor embedded provenance can upgrade trust.

`attested` remains diagnostic-only. An artifact becomes `locally-reproduced` only when a **new**
broker-owned local run matches the current flow digest, app build/source and package fingerprints,
platform/device profile, and relevant failure code, step, and checkpoints. Only that state can
pass the future repair/source proposal gate, and it still requires separate human approval. The
Inspector **Trace** imports only through the same bounded broker trust route and displays the
isolated projection in captured/read-only mode. It never preserves raw bytes, adopts embedded IDs,
or starts a replay. **Reproduce locally** opens a separate broker preflight and requires normal
human confirmation; it does not auto-start or bind trust. A browser-only host that cannot establish
current source fingerprints says so rather than fabricating a locally-reproduced state. Repair
candidate generation, source proposals, and apply remain unavailable.

## Human-approved selector repair

In the Inspector, Repair presents one lifecycle action at a time: check the latest failed run,
create an inert proposal, preview it, run validation, approve, then apply. Reasons, proof, and
policy details stay collapsed until requested.

Repair is deliberately narrower than diagnostics. Only a **pre-dispatch** primary
`locator-not-found` from a current local run, or imported evidence that has been
`locally-reproduced`, can enter repair classification. The recorded pre-step checkpoint must
match the current build, agent instance, seed/backend state, route, window, modal, locale, theme,
orientation, display profile, and collection key. Unknown completion, infrastructure, capability,
state, secret/data, actionability, ambiguity, and assertion failures are never repairable.

The deterministic selector-health shortlist is the only candidate source. A candidate must resolve
exactly once now, match its value-free semantic fingerprint, pass kind/risk gates, and retain its
full score/rationale/evidence with `uncalibrated` calibration. The existing active selector remains
unchanged until a separate proposal passes preview, a human issues a bounded validation grant, and
the platform lifecycle performs a hard reset plus an in-memory override replay. Validation requires
unchanged hard assertions and an independent business oracle; it never commits a flow.

An optional plan `repairPolicy` can narrow candidate kinds, permit locale-bound exact text only
with its explicit `localization` risk flag, and set shortlist/score-gap bounds. It cannot allow
runtime IDs, coordinates, type/index selectors, ambiguity, stale source, platform divergence, or
unscoped template/virtualized rows.

Apply requires a single-use human approval grant bound to the proposal, patch digest, base flow
path/digest/revision, target, policy, and expiry. The host performs a selector-only compare-and-swap
write, preserving Markdown prose where feasible and atomically updating the flow, plan sidecar, and
redacted history. It then requires three clean reset/oracle verification replays. Failure enters
`rollback-required`; rollback writes a new revision restoring the previous selector.

History is append-only and hash-linked at:

```text
maui-tests/.devflow/<flow-id>.repair-history.jsonl
```

It is bounded, path-confined, and stores only redacted IDs/digests, candidate kinds/scores/risk
codes, and outcomes—never prompts, source text, selectors containing user text, screenshots, or
secrets. Repair never changes assertions, expected values, actions, order, app XAML/C#, or a
`.mauitrace` format. There is no automatic apply and no model call in the repair core.

## Reviewed XAML and C# AutomationId source proposals

In the Inspector, Source first asks for a selected mapped control. **Check source** validates the
new AutomationId before **Create source proposal** appears; preview, approval, and apply/download
are shown sequentially. Technical source, build, host, and safety facts stay collapsed.

XAML source proposals are not flow repairs. The only preview operation is adding or replacing a
static literal `AutomationId` on one exact direct element declaration. The pure eligibility
evaluator requires a current unambiguous source map and matching hash, a project-contained
non-generated/non-linked `.xaml` path with no reparse-point escape, a safe test-ID grammar, and
unique project and current-live scope evidence.

It rejects bindings/resources/markup extensions, conditional/generated declarations,
DataTemplate/ControlTemplate/style/setter/resource scopes, BindableLayout/repeaters/virtualized
items, and native/WebView synthetic nodes. IDs may not be visible text, localized text, user data,
or dynamically derived values. Advisory analyzer IDs `DFXAML001` (missing static interactive ID),
`DFXAML002` (duplicate ID), and `DFXAML003` (template/style/repeater ID) are diagnostics only;
they never offer an automatic code fix.

The source proposal carries an exact diff and digests, but history stores only redacted,
hash-linked IDs/digests/state. A human-issued single-use source grant binds the proposal, patch,
file hash, project identity, affected flow references, host, and expiry. Only an explicit local
host action can compare-and-swap atomically; there is no force apply, commit, merge, C# edit, or
automatic selector update.

## Prototype-study research assessment (local only)

The Inspector Results card can export a bounded local prototype-study journal for evaluating the
novice-first Goal → Steps → Review → Run → Results journey. It records only an allow-listed
set of lifecycle events in browser `sessionStorage`: timestamps, safe provenance enums, booleans,
bounded counts/durations, and locally pseudonymized run/proposal references. It does not record
Goals, flow text, selectors, typed values, source, screenshots, prompts, reviewer identity, URLs,
device serials, payloads, or secrets.

This is **not telemetry**. It has no network egress or upload path, is scoped to the current tab,
is explicitly downloadable as a file-only JSON document, and can be explicitly cleared. The
summary reports authoring and review timing, selector durability counts, in-session replay
stability, failure classifications and diagnosis proxy, repair decisions, Improve usage, and
missing/insufficient measurements. It is intentionally marked `localSessionOnly: true`.

Prototype-study evidence complements, but never contributes to, digest-bound flow-run reports,
device artifacts, or `maui devflow flow qualify` accounting. In particular, it does not change
`maui-preview-qualification-v1` required semantics, turn a browser session into device evidence,
or make a platform qualified. Use qualification reports and returned device artifacts for those
claims; use the local journal only to assess Workbench authoring and human-involvement outcomes.

## Android engineering-preview qualification

`maui devflow flow qualify` is a **read-only** evidence-accounting command. It validates the
static Inspector corpus, generates deterministic no-repair variants, measures bounded host
operations, and combines optional redacted qualification evidence and flow-pilot manifests. It
does not start a broker, connect to an app, replay a flow, extract an artifact, invoke a model, or
apply a repair/source proposal.

```powershell
maui devflow flow qualify `
  --platform android `
  --corpus tests/DevFlow/InspectorCorpus `
  --output artifacts/devflow/qualification.json `
  --json
```

The report uses
[preview-qualification-v1](spec/schemas/maui-preview-qualification-v1.json). It fingerprints
corpus/package/tool/policy/build data rather than copying content, distinguishes `curated`,
`generated`, and `device-backed` samples, and includes denominators, exclusions, Wilson 95%
confidence intervals, repair precision/recall, calibration buckets, artifact references, review
status, and feature-flag state.

The static corpus contributes deterministic policy evidence only. Its generated cases are **not**
independent runs and never count as Android device evidence. The command intentionally returns
`not-qualified`, rather than `pass`, when real-device reports, recording/schema completeness,
review records, or Android overhead evidence are absent. Use `--fail-on-non-pass` only for an
explicit caller policy; the preview workflow is advisory and is not a required PR gate.

The Android gate requires all of the following:

- repair precision at least 95% using the lower bound of a two-sided 95% Wilson interval;
- zero false heals across at least 300 explicit no-repair evaluations;
- at least 99% selector stability in the declared Android scope;
- ECE at most 0.05 before any probability-like confidence is displayed;
- zero privacy/security escapes;
- valid corpus/report/recording/first-attempt/artifact evidence and approved plan,
  rubber-duck, and independent-review records;
- at least 100 **clean, real-device, first-attempt** executions for every declared Tier-1 flow,
  with at least 99% first-attempt stability.

Only first attempts are counted. Diagnostic reruns never replace them. Infrastructure outcomes are
excluded only when they carry a recorded deterministic exclusion reason; otherwise they count in
the denominator. Emulator/AVD artifacts are retained as useful pilot evidence but explicitly
remain `not-qualified` for the real-device gate.

`MauiPreviewFeatureFlagConfiguration` defines local opt-in flags for the Workbench, agent
authoring, repair proposals, source proposals, and trace import/export, plus
`DEVFLOW_PREVIEW_KILL_SWITCHES` for disabling those named surfaces. Defaults are off. The
configuration always forces auto-repair apply, auto-source apply, model-provider use, telemetry
egress, and required-PR-gate state to `false`; a qualification report records the effective
state.

After source apply, every affected official target TFM buildable on the host must build. The host
then rebuilds/relaunches, confirms source remap and runtime uniqueness, replays affected flows,
and verifies an independent oracle. iOS and Mac Catalyst verification unavailable on Windows is
recorded as `pending-external-qa`; it is never reported as passed. The optional experimental
`net10.0-macos` AppKit target can also be `pending-external-qa`, but it never satisfies or
replaces the required Mac Catalyst target. Verification failure enters `rollback-required`, and
rollback restores the exact prior bytes atomically. Any selector change after a successful runtime
remap is a new, separately approved flow-repair proposal.

### Roslyn-proven C#

The Source tab's **C#** mode is advisory and accepts only a current mapped, project-contained,
non-generated/non-linked `.cs` declaration whose Roslyn semantic model resolves to a supported
MAUI actionable control. The accepted syntax subset is a direct object initializer or a direct
`.AutomationId = "literal"` assignment on one resolvable local or member. It creates one minimal
forward patch and one inverse patch without formatting or rewriting a wider document.
When a running element has no C# source map, VS Code may supply its active saved C# declaration
(file, line, column, and short hash); the broker still confines it to the registered project,
recomputes the hash, and proves the selected runtime/semantic control type match.

It rejects DataTemplate/ControlTemplate/repeater/item-factory/BindableLayout declarations,
collections/lambdas/factories, virtualized items, generated/linked/outside-project documents,
conditional/preprocessor branches, reflection/dynamic construction, Shell/native/WebView nodes,
and computed/bound/localized/user-derived or duplicate IDs. `DFCS001`, `DFCS002`, and `DFCS003`
are advisory diagnostics only and never provide auto-apply.

The broker never writes C# source. After a human grant it records `awaiting-host-apply`; VS Code
opens a native diff, applies the exact patch through the IDE, and acknowledges pre/post hashes and
patch digest. Browser hosts download a patch only, Canvas reports C# apply unsupported, and MCP
cannot approve, apply, acknowledge, or source-write. Failed verification requires a separately
granted IDE-mediated inverse-patch acknowledgment and a new redacted history event.

## Required platform completion matrix

An Android engineering pilot is not an all-platform preview or release. The required completion
gate has four independent runtime lanes:

| Platform | Current status | Completion evidence |
|---|---|---|
| Android | Engineering pilot; **not-qualified** without real-device evidence | Clean real-device first attempts, the Tier-1 corpus, reset/oracle, selector/repair/source, privacy, report, and artifact gates |
| iOS | Not yet qualified | macOS-hosted Simulator/device-agent lifecycle and the same shared semantic contracts |
| Mac Catalyst | Not yet qualified | macOS lifecycle, shared corpus, source remap/replay/oracle, and artifact fingerprints |
| Windows | Implemented second-platform contract; local QA is currently **infrastructure-blocked** until run from an active desktop, and is **not qualified** | Debug Windows build, owned-process-only reset/seed/relaunch, process-scoped agent/checkpoint verification, shared Tier-1 repetitions, UIA/dialog/WebView capability contracts, report/artifact parity, and source/repair/trust validation |

AppKit, WPF, and GTK are experimental results reported separately. The AppKit fixture is an
advisory `macos --experimental` host lane with an explicit `backend=appkit` manifest; it never
waives a required Android, iOS, Mac Catalyst, or Windows gate. Package-consumer compilation proves only API/package
compatibility and never reports runtime QA. The macOS package-consumer publish gate is likewise compile-only; a returned macOS QA handoff
remains required before Apple runtime qualification. The checked-in iOS Simulator and Mac Catalyst
commands are documented in [platform flow QA](flow-qa.md) and
[Apple XCTest QA handoff](apple-xctest-spike.md).

The Appium project is an independent black-box smoke lane for release-like builds and OS-owned UI.
It is not the canonical `MauiFlowRunner` kernel and can neither repair a flow nor turn an Android,
Apple, or Windows qualification status into pass. See
[Appium black-box smoke tests](appium-smoke-testing.md).

## Preview controls, privacy, and compatibility

Preview feature flags are opt-in and default off. `DEVFLOW_PREVIEW_KILL_SWITCHES` can disable the
Workbench, agent authoring, repair proposals, source proposals, or trace import/export. No flag
can enable auto-repair apply, auto-source apply, model-provider use, telemetry egress, or a
required PR gate; those states are forcibly false and recorded in qualification evidence.

Reports, imported artifacts, repair/source history, and evidence are redacted, bounded, and
diagnostic-only until a separately authorized local workflow reaches its explicit trust and
approval gate. See [evidence privacy and artifact trust](evidence.md) and the
[preview compatibility policy](compatibility.md). Public API and schema changes remain preview
changes, but intentional binary, source, or semantic breaks require the `breaking-change` label,
migration notes, and a reviewed baseline update.
