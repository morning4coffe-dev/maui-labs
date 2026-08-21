---
name: maui-devflow-run-cli
description: >-
  Run committed DevFlow flows from the terminal with the `maui devflow flow`
  command family. USE FOR: choosing between validate, replay, run, reproduce,
  triage, and qualify; building and deploying a MAUI app for a run; exact
  target binding; output directory and cleanup policy; evidence flags; reading
  the exit status of a local run. DO NOT USE FOR: authoring or repairing a flow
  (use maui-devflow-test); promoting a recording (use maui-devflow-record);
  writing CI workflows (use maui-devflow-ci); diagnosing a red CI run from
  downloaded artifacts (use maui-devflow-ci-triage); interactive build, deploy,
  launch, and connection recovery outside a flow run (use maui-devflow-debug);
  or MCP-driven runs inside the restricted test-agent profile, which go through
  `maui_test_run`, not the CLI.
---

# MAUI DevFlow CLI Execution

## Purpose

Pick the right `maui devflow flow` verb for what the operator actually wants,
and run it with the narrowest options that still produce usable evidence.

## When to Use

- An operator wants to run, replay, reproduce, or qualify a committed flow from
  a terminal.
- A run needs an exact target, output directory, or evidence flag decided.
- The exit status or output of a local `maui devflow flow` run needs reading.

## When Not to Use

| Situation | Use instead |
| --- | --- |
| Authoring or repairing a flow | `maui-devflow-test` |
| Promoting an Inspector recording | `maui-devflow-record` |
| Writing the GitHub Actions workflow | `maui-devflow-ci` |
| Diagnosing a red CI run from artifacts | `maui-devflow-ci-triage` |
| Interactive build, deploy, launch, connection recovery | `maui-devflow-debug` |
| MCP runs in the restricted test-agent profile (`maui_test_run`) | `maui-devflow-test` |

## Command Reality — verified verbs only

`maui devflow flow` has exactly six subcommands. Nothing else exists.

| Verb | What it does | Drives the app? |
| --- | --- | --- |
| `validate` | Parses one `.md` flow and validates it | No |
| `replay` | Replays a flow against an already-running app | Yes |
| `run` | Builds, launches, binds exactly, and executes flow + plan | Yes |
| `reproduce` | Imports bounded evidence, runs a fresh local execution, stops after trust evaluation | Yes |
| `triage` | Deterministic diagnostic-only output from a manifest and report | No |
| `qualify` | Evaluates preview gates from a static corpus and optional redacted evidence | No |

**Commands that do not exist.** Do not write them into a script or a doc:

- **`maui build`, `maui run`, `maui deploy`** — no such top-level commands.
  `maui` has `doctor`, `device`, `profile`, `project`, `version`, `android`,
  `apple`, `port`, `devflow`, and `go`.
- **`maui android install`** exists but is **Android SDK setup** — it installs
  the SDK, JDK, and packages. It does **not** install an APK.
- **`maui devflow flow record`** — no such verb. Recording is an Inspector
  Workbench activity; see maui-devflow-record.

To build and deploy by hand, use the .NET SDK and the platform tools directly:

```bash
dotnet build src/Shop/Shop.csproj -f net10.0-android -c Debug
adb install -r <path-to-apk>
adb shell monkey -p com.contoso.shop -c android.intent.category.LAUNCHER 1
```

`maui devflow flow run` already does build, deploy, launch, and bind on the
supported local platform adapters — prefer it over hand assembly, and fall back
to the manual sequence only when the adapter does not cover the target.

## Inputs

- The committed `.md` flow path. The `<flow-base>.maui-plan.json` sidecar is
  **required** by `run`; `--plan` overrides its location.
- The app `.csproj` for `run` and `reproduce`.
- `-p/--platform`: `android`, `ios`, `maccatalyst`, `windows`. **`flow run`
  defaults to `android` when it is omitted**, regardless of `-f`. The TFM does
  not select the platform — pass both, and `--device <serial|udid>` for an
  exact device.
- `-f/--framework` only when the project has several matching TFMs.
- A new or empty `-o/--output` directory — first-attempt output is immutable
  and existing files are never overwritten.

## Workflow

### Validate first, always

```bash
maui devflow flow validate maui-tests/promo-reduces-total.md
```

Never send an unvalidated flow to a device.

### Replay against a running app

```bash
maui devflow flow replay maui-tests/promo-reduces-total.md \
  --evidence-on-failure artifacts/promo.mauitrace
```

`replay` stops at the first divergence unless `--continue-on-failure` is given.
Keep the default: a flow that keeps driving after a failure produces evidence
about a state nobody designed.

### Full local execution

```bash
maui devflow flow run maui-tests/promo-reduces-total.md \
  --project src/Shop/Shop.csproj --platform android -f net10.0-android -c Debug \
  --output artifacts/promo-run-1 \
  --cleanup stop --agent-wait-seconds 90 \
  --evidence-on-failure
```

- `--cleanup` is `none`, `stop` (default), or `uninstall`. `uninstall` removes
  only a package this invocation installed.
- `--evidence-screenshot` requires `--evidence-on-failure` and is an explicit
  opt-in: **screenshot pixels are never redacted** and may show real on-screen
  data. Ask before adding it.
- On Android, set `AndroidSdkDirectory` and `JavaSdkDirectory` when the machine
  holds more than one SDK. Auto-detection resolves through MSBuild, so it can
  pick up a path another project configured rather than the one the emulator and
  `adb` on `PATH` belong to. The symptom is not a missing-SDK error: the build
  succeeds against the wrong SDK and the run fails later at install or launch,
  which reads as a device problem.
- `flow run` refuses a package that is already installed
  (`android-preexisting-app-unsafe`). That is deliberate — it owns installation
  so app-private storage is empty at launch, which is what lets a post-run
  oracle claim the run itself wrote the record. Uninstall first rather than
  reaching for a flag.

### Diagnose without driving

```bash
maui devflow flow triage --manifest artifacts/execution-manifest.json \
  --report artifacts/flow-run.json --format markdown -o artifacts/triage.md
```

```bash
maui devflow flow qualify --corpus <corpus> --artifact-manifest <manifest> \
  -o artifacts/qualification.json --fail-on-non-pass
```

Both are diagnostic-only. Neither replays and neither applies a change.

### Reproduce imported evidence

```bash
maui devflow flow reproduce maui-tests/promo-reduces-total.md \
  --import artifacts/ci-flow-run.json --kind flow-run \
  --project src/Shop/Shop.csproj --platform android --output artifacts/repro-1
```

`reproduce` stops after trust evaluation. Imported CI evidence is
**untrusted** until a local reproduction agrees with it; see
maui-devflow-ci-triage.

## Validation

- The flow validated cleanly before any driving verb ran.
- The output directory was new or empty, and the first attempt is preserved.
- `--platform` was passed explicitly rather than relying on the `android`
  default of `flow run`.
- Cleanup policy was stated rather than defaulted silently when the run
  installed a package.
- The reported result names the exact target, and carries **not independently
  verified** whenever the plan declares no independent business oracle.

## Completion Check

Report the verb used, the exact target bound, the output directory, the
terminal status, and whether evidence was captured. Do not summarize a failing
run as a tooling problem without pointing at the failing step.
