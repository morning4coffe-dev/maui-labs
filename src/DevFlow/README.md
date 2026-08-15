# Microsoft.Maui.DevFlow

A comprehensive testing, automation, and debugging toolkit for .NET MAUI applications.

> ⚠️ **Experimental** — APIs may change between releases. Not covered by the Microsoft Support Policy.

## Packages

| Package | Description |
|---------|-------------|
| **Microsoft.Maui.DevFlow.Agent** | In-app agent for .NET MAUI apps. Exposes visual tree, element interactions, screenshots, and profiling via HTTP/JSON API. |
| **Microsoft.Maui.DevFlow.Agent.Core** | Platform-agnostic core: HTTP server, visual tree walker, CSS selector engine, network capture, profiling. |
| **Microsoft.Maui.DevFlow.Agent.Gtk** | GTK/Linux agent for Maui.Gtk apps. |
| **Microsoft.Maui.DevFlow.Blazor** | Blazor WebView CDP bridge. Enables Chrome DevTools Protocol access for Blazor Hybrid content via Chobitsu. |
| **Microsoft.Maui.DevFlow.Blazor.Gtk** | Blazor CDP bridge for WebKitGTK on Linux. |
| **Microsoft.Maui.DevFlow.CLI** | DevFlow command implementation used by the unified `maui devflow` CLI surface for automation, debugging, and MCP server support. |
| **Microsoft.Maui.DevFlow.Driver** | Platform-aware app driver for iOS, Android, Mac Catalyst, Windows, and Linux. |
| **Microsoft.Maui.DevFlow.Testing** | Experimental public preview of framework-neutral flow contracts, Markdown parsing, validation, recording, and replay. It is independent of CLI, broker, Inspector, MCP, and test-framework adapters. |
| **Apple XCTest proof harness** | Nonshipping macOS host + native XCTest operation-agent prototype. It keeps the target foreground while the same public `MauiFlowRunner` executes on the host; see [Apple XCTest flow proof](../../docs/DevFlow/apple-xctest-spike.md). |
| **Microsoft.Maui.DevFlow.Logging** | Buffered rotating JSONL file logger. No MAUI dependency. |

## Quick Start

### 1. Install the NuGet packages

```xml
<PackageReference Include="Microsoft.Maui.DevFlow.Agent" />
<PackageReference Include="Microsoft.Maui.DevFlow.Blazor" />  <!-- If using Blazor Hybrid -->
```

### 2. Register in MauiProgram.cs

```csharp
using Microsoft.Maui.DevFlow.Agent;

public static MauiApp CreateMauiApp()
{
    var builder = MauiApp.CreateBuilder();
    builder.UseMauiApp<App>();

    #if MAUI_DEVFLOW
    builder.AddMauiDevFlowAgent();
    #endif

    return builder.Build();
}
```

`MAUI_DEVFLOW` is defined by the package only when `MauiDevFlowEnabled=true`. Debug builds enable
it by default. For an explicit optimized, read-only diagnostics build:

```bash
dotnet build -c Release -p:MauiDevFlowProfileMode=true
```

Profile mode also defines `MAUI_DEVFLOW`, so the same registration block is compiled without
requiring `DEBUG`; accidental optimized inclusion without profile mode remains a build error.
Profile mode disables network monitoring entirely so streaming/large HTTP bodies cannot perturb or
block the diagnostic run. The same build contract and runtime defaults ship in the standard,
GTK, and WPF agent packages.

### 3. Install the unified CLI tool

```bash
dotnet tool install -g Microsoft.Maui.Cli --prerelease
```

### 4. Interact with your running app

```bash
# Install DevFlow skills for AI agent integration (auto-detects target directory;
# defaults to .claude/skills/ — configurable via --target: claude, github, agent, agents, or auto)
# (configurable via --target: claude, github, agent, agents, or auto)
maui devflow init

# Visual tree
maui devflow ui tree

# Take a screenshot
maui devflow ui screenshot -o screenshot.png

# Tap an element
maui devflow ui tap --automationid "MyButton"

# Start MCP server for AI agent integration
maui devflow mcp

# Start the restricted, human-grant-gated test-authoring MCP profile
maui devflow mcp --profile test-agent

# Open the shared DevFlow Inspector and Test Workbench for the selected app
maui devflow inspect

# Print the selected per-agent URL without launching a browser
maui devflow inspect --agent <agent-id> --no-launch
```

### Resume after a rebuild

DevFlow can save an explicit, broker-owned **Shell route** checkpoint. It stores only route and
connection metadata under `~/.mauidevflow/`; it does not serialize ViewModels, app values, or
navigation parameters beyond the route. Reconnects never navigate automatically.

```bash
maui devflow resume save
# rebuild or reconnect the app
maui devflow resume status
maui devflow resume restore
maui devflow resume clear
```

The Web Inspector exposes the same Save route, Resume, and Clear route controls in its existing
toolbar. The raw route remains local so restore can navigate correctly, while CLI/Inspector status
and evidence output redact every query value and fragment.

### Test Workbench shell (preview)

The Inspector's visible **Tests** action opens the shared **DevFlow Test Workbench**:
**Plan**, **Steps**, **Run**, **Trace**, **Repair**, **Improve**, and **Source**. It preserves the existing
Workflow timeline as the compact active-test strip and leaves Inspector interaction, recording,
replay, Data, and Evidence behavior unchanged.

`maui devflow inspect --test <flow.md>` and `--trace <flow-run.json|mauitrace>` are startup hints
only: they do not replay or execute anything. Plan and Steps now support bounded human authoring:
plan sidecars, broker-owned recording state, semantic metadata review, typed assertions, validation,
deterministic diff, and an explicit flow+plan commit. The shell does not auto-run after saving,
repair selectors automatically, apply an unapproved source patch, or invoke an agent/provider.

Each plan is stored next to its flow as `maui-tests/<flow-base>.maui-plan.json` and binds the
canonical flow filename and digest. Save/commit performs revision and digest checks, shows stale
state instead of silently overwriting, rejects symlink/reparse-point traversal, and writes a
canonical Markdown/sidecar pair with rollback on write failure. Browser hosts download artifacts
when no confined workspace is available; VS Code and Canvas use a bounded host bundle-save bridge.

Imported run reports and `.mauitrace` v1 bundles are never replayed by the Workbench. They begin
`untrusted`; independently verified producer facts may make them `attested`, but both states are
diagnostic-only. Only a new local run that matches current flow/app/target/failure checkpoints can
be `locally-reproduced`, the minimum trust state for a future repair or source proposal. Imported
artifacts remain diagnostic-only; the local Source tab requires a current mapped declaration and
separate human approval before an explicit local host can apply a bounded XAML patch. Its C# mode
adds only Roslyn-proven direct initializer/literal-assignment proposals: the broker never writes
C# source, and a native IDE host must acknowledge the exact pre/post hashes and patch digest.

### Diagnose binding Problems

The Inspector's **Problems** tab and `GET /api/v1/diagnostics/problems` expose bounded,
metadata-only MAUI binding failures. Entries identify the binding path, target type/property, and
XAML source when available; rejected runtime values are never retained. Selecting a Problem jumps
to the affected live element so you can inspect its value source and choose a safe edit. Binding
and dynamic-resource properties fail closed unless an explicit unsafe override is requested, and
unsafe overrides are never recorded into replay workflows.

### Reliable workflow replay

Workflow selectors using `AutomationId` or exact text must resolve to exactly one element. A
duplicate is reported as an ambiguity rather than replaying against an arbitrary first match.
Before tap and fill, replay waits for a visible and enabled element; taps also wait for stable,
positive bounds. Safe property changes only require an unambiguous target, so a flow can disable
and later re-enable the same element. Type/index and runtime-id selectors remain explicitly fragile.
Replay stops at the first divergence by default.

The canonical flow contracts and replay semantics live in
`Microsoft.Maui.DevFlow.Testing`. The CLI, broker, Inspector, evidence capture, and MCP tools are
adapters over that package rather than independent flow engines.

```bash
maui devflow flow replay maui-tests/login.md --evidence-on-failure
```

Failure evidence uses the normal redaction policy and leaves screenshots off unless separately
requested. Active workflow recordings are spooled under `~/.mauidevflow/recordings/` so an agent
disconnect or broker restart does not discard them; stop or cancel deletes the spool. Password
entries and controls with secret-shaped identifiers never persist their typed value. The recorded
step stores an environment-variable reference such as
`MAUI_DEVFLOW_SECRET_PASSWORDENTRY_STEP_2`; set that variable in the CLI/Inspector process before
replay. A spool write or size-limit failure rejects/rolls back the step and marks the recording
non-durable so it cannot be silently completed with a missing mutation.

Concurrent instances of the same package/TFM keep separate live recordings. After a rebuild, a new
process may adopt a disconnected recording only when the host presents that recording's random
`recordingId` capability; package/TFM identity alone never merges processes. Inspector keeps a
bounded per-panel session list of active capabilities so its browser, VS Code, and Canvas hosts can
resume after reconnecting without granting a separate tab authority, while MCP callers pass the id
returned by `maui_flow_record_start`. Another panel may explicitly join a still-live recording by
starting recording; Inspector's passive status polling never transfers resume authority.

### Android flow pilot

The Android integration fixture owns build, install, reset, seed, ADB forwarding, launch, and
preflight verification; `Microsoft.Maui.DevFlow.Testing.MauiFlowRunner` remains the only flow
executor. It builds `DevFlow.Sample` with `DevFlowIntegrationTest=true`, which enables a
Debug-only test extension for deterministic sample state and a non-sensitive fingerprint. That
extension is not compiled into Release or profile builds.

Prerequisites: the MAUI Android workload, an Android SDK with `adb`/emulator tools, and either a
ready emulator or `ANDROID_HOME`/`ANDROID_SDK_ROOT`. Run the guarded one-flow smoke test only when
a device is intentionally available:

```powershell
$env:DEVFLOW_RUN_ANDROID_FLOW_TESTS = '1'
$env:DEVFLOW_TEST_PLATFORM = 'android'
dotnet test src/DevFlow/Microsoft.Maui.DevFlow.Agent.IntegrationTests/ `
  --filter FullyQualifiedName~AndroidFlowEnvironmentTests
```

Set `DEVFLOW_TEST_ANDROID_SERIAL` to use a specific emulator or physical test device. The initial
Tier-1 corpus is under `samples/DevFlow.Sample/maui-tests/`; reports and host diagnostics are
written below `artifacts/TestResults/devflow-flow/android/`. App-only reset is deliberately not
reported as external-backend reset proof, so flows with external effects must not claim the
`test-tenant-resettable` policy.

The separate flow-pilot selector runs every committed Tier-1 flow three times from a fixture-owned
clean state. It is intentionally separate from ordinary integration tests:

```powershell
$env:DEVFLOW_RUN_ANDROID_FLOW_PILOT = '1'
$env:DEVFLOW_FLOW_PILOT_ARTIFACT_ROOT = 'artifacts/devflow/local-pilot/android'
$env:DEVFLOW_FLOW_PILOT_WORKFLOW_RUN_ID = 'local-pilot'
dotnet test src/DevFlow/Microsoft.Maui.DevFlow.Agent.IntegrationTests/ `
  --filter 'Category=FlowPilot' `
  --logger 'trx;LogFileName=devflow-flow-android.trx' `
  --results-directory artifacts/TestResults/devflow-flow/android
```

In GitHub Actions, open **DevFlow Integration Tests**, choose `flow-pilot` for **Platforms**, and
run the workflow. The same advisory Android lane runs nightly, when a relevant pull request path
changes, or when the PR receives `integration-tests` or `flow-pilot`. It is not required PR CI.

The workflow uploads these safe-by-default artifacts on success and failure:

```text
artifacts/TestResults/devflow-flow/android/*.trx
artifacts/devflow/<workflow-run>/android/manifest.json
artifacts/devflow/<workflow-run>/android/<flow>-attempt-<n>/flow-run.json
artifacts/devflow/<workflow-run>/android/<flow>-attempt-<n>/failure.mauitrace  # failure only, when available
artifacts/devflow/<workflow-run>/android/<flow>-attempt-<n>/android-host-diagnostics.json
```

`manifest.json` binds the source commit, workflow run, Testing package version, app/package and
flow digests, Android SDK/AVD/device facts, reset/seed fingerprints, and SHA-256/size metadata for
published files. `firstAttempt` is immutable: later clean repetitions and any manually collected
`diagnosticReruns` are supporting evidence and never turn a first failure into a pass. An
`infrastructure-error` outcome or `failure.class: infrastructure` means provisioning, reset,
install, ADB, emulator, launch, or agent-readiness failed; it is not a flow-repair result.
Screenshots, source, and raw model context remain excluded by default. Failure evidence is
redacted `.mauitrace` metadata/tree/log output with screenshots disabled.

Every integration-workflow invocation also publishes an advisory static
`maui-preview-qualification-v1` report. The emulator flow-pilot appends a second read-only
qualification report when its artifact manifest exists. Both reports remain `not-qualified` until
the required real Android-device evidence, complete report/schema/recording facts, approved plan
and independent reviews, and performance evidence are supplied. The AVD is explicitly marked
`deviceEvidence.kind: "emulator"` and `realDevice: false`; it is useful pilot evidence but never
silently counted toward the 100 clean first-attempt real-device executions required for each
Tier-1 flow. The workflow is advisory, not a required PR gate, and neither report replays a flow
or applies a repair/source proposal.

### Windows second-platform contract

The official MAUI Windows lane is implemented separately from the repository's experimental WPF
backend. `WindowsFixture` builds the instrumented Debug
`net10.0-windows10.0.19041.0` sample, terminates only its owned process, performs a
test-only process-memory reset/seed, launches on the configured DevFlow port, rejects stale agent
instances, and verifies app/build/package, checkpoint, display, locale/theme, and state
fingerprints before invoking the same public `MauiFlowRunner`.

Run the checked-in host entry point on Windows:

```powershell
pwsh .\eng\devflow\Run-DevFlowFlowQa.ps1 `
  --platform windows `
  --repeat 3 `
  --results-root .\artifacts\TestResults\devflow-flow\windows `
  --qualification
```

The dedicated `Category=WindowsFlowQa` corpus records three clean attempts per shared Tier-1
flow in `windows-tier1-manifest.json`; it does not treat broad Windows integration tests or WPF
tests as DevFlow Windows evidence. It also checks managed/native UIA identity, duplicate
rejection, Shell/modal checkpoints, native dialogs, WebView reset where available, report parity,
source-proposal safety, and restricted artifact trust. A missing supported capability is reported
as `capability-missing`; Windows remains **not qualified** until a successful runtime artifact set
and the broader release metrics are reviewed.

The host must provide an active interactive Windows desktop. If an unpackaged WinUI test process
exits before agent readiness (for example, from a disconnected RDP session), the fixture writes a
redacted process-exit host diagnostic and returns `infrastructure-failure`; it does not claim a
flow failure or Windows qualification.

### Measurement status

Goal #4 names five metrics. Their recorded state is tracked deliberately rather than implied:

| Metric | Recorded value today | Where |
|---|---|---|
| False repair rate | `0/316`, split `curated 0/16` + `generated 0/300` — static corpus only, no device trials | `tests/DevFlow/InspectorCorpus/README.md` |
| Classification accuracy | Measured on the curated corpus with a published confusion matrix; gate stays `not-qualified` below 100 labelled cases | `tests/DevFlow/InspectorCorpus/baselines/README.md` |
| Repair acceptance / precision | Denominator is the curated repair-positive count; no human-acceptance signal exists yet | `tests/DevFlow/InspectorCorpus/baselines/qualification.json` |
| Replay stability | **No recorded number.** Needs real-device runs; `maui devflow flow qualify --accumulate` makes the ≥100 clean first attempts reachable across independent jobs | [flow-qa.md](../../docs/DevFlow/flow-qa.md) |
| Authoring time | **No recorded number.** The protocol and export path exist; no sessions collected, and no unassisted control arm has been run | [authoring-time-protocol.md](../../docs/DevFlow/authoring-time-protocol.md) |

### On-demand diagnostics

Two explicit, read-only diagnostics are available from the CLI, the MCP tools, and the Inspector's
**Data** dock. Both are one-shot: nothing runs in the background, nothing watches for changes, and
neither refreshes the Inspector frame or takes a screenshot.

```bash
# One layout scan of the running app
maui devflow diagnostics layout --json
maui devflow diagnostics layout --element MyList --max-elements 500

# A bounded performance triage window
maui devflow diagnostics performance --duration 5 --sample-interval 250
maui devflow diagnostics performance --attach   # summarize the session already running
```

**Layout diagnostics** (`GET|POST /api/v1/ui/diagnostics/layout`, rule catalog
`GET /api/v1/ui/diagnostics/layout/rules`, capabilities `diagnostics.layout` and
`ui.layoutDiagnostics`) uses a versioned schema v2 contract shared by the CLI, Driver, MCP tools,
evidence, and Inspector. The compatibility GET performs the managed baseline scan in one UI-thread
tree walk:

| Rule | Outcome | Confidence |
|------|---------|------------|
| `layout.visible-zero-area` | violation | high |
| `layout.constraint-violation` | violation | high |
| `layout.element-outside-window` | observation | medium |
| `layout.desired-size-constrained` | observation | medium |
| `layout.child-outside-parent` | observation | low |

The v2 POST request additionally accepts profiles, a selected rule set, minimum severity,
pass accounting, evidence controls, stability and occlusion options, privacy controls, and
suppressions. Its rule catalog also includes clipping, content overflow, text rendering,
geometric overlap, visual/interaction occlusion, and accessibility visibility. Those rules
currently advertise `unavailable` support from the managed baseline instead of producing a false
clean result; native and Blazor collectors will progressively make them available.

Geometry the active collector cannot read is reported as `incomplete`, never as a pass, and every
report carries per-rule `coverage`, explicit `limitations`, stable suppression fingerprints,
source-aware element references, and immutable snapshot/revision metadata. Element text, values,
and property dictionaries remain excluded by default. Inspector suppressions are exact project
entries under `layoutDiagnostics.suppressions` in `.mauidevflow`; user-wide suppressions can be
placed in `~/.mauidevflow/layout-diagnostics.json`.

**Performance triage** starts a new profiler session only when none is active; otherwise callers
must attach read-only to the existing session, and only the creator may stop it. Stopping captures
a final boundary sample and returns the final batch atomically. A successful start returns an
opaque stop token; read-only status/attachment exposes only the session id, so another client
cannot terminate the creator's session after a lease expires. The triage view aggregates the
profiler streams into a task-focused summary: managed heap, process resident/physical footprint,
and native-heap-specific memory (where available); GC deltas; CPU average and peak; thread peak;
top hotspots; marker counts; and prominent buffer-loss metadata. Process footprint is never
presented as unmanaged/native heap. Frame rate is
reported **only** when the platform provides exact native rendered-frame timings — display-cadence
estimates are never surfaced as FPS. Aggregation lives in `Microsoft.Maui.DevFlow.Driver`
(`PerformanceAggregator`) so the CLI, MCP tools, and Inspector present identical analysis. In a
normal Debug build the summary states that Hot Reload, the debugger, and DevFlow's own diagnostics
perturb the numbers; hand off to a native profiler for call-stack attribution.

Layout findings are added to `.mauitrace` evidence bundles as `layout.json` when the connected
agent supports them, projected into the same evidence-safe shape as the rest of the bundle
(project-relative source paths, no text, values, or property dictionaries).

### Session identity

When `Microsoft.Maui.DevFlow.Agent` is referenced, builds are tagged with a **session identity**
derived from a one-way, sanitized project-path value. This metadata-only identifier helps DevFlow
distinguish builds from different environments without modifying the app's `ApplicationId` or
bundle identifier. The full project path is not embedded by default.

The session identity is included in:
- Assembly metadata (`Microsoft.Maui.DevFlowSessionId`) — compile-time injected by the `Microsoft.Maui.DevFlow.Agent` MSBuild targets
- Project identity metadata (`Microsoft.Maui.DevFlowProject`) — the project filename by default
- Broker registration (visible via `maui devflow list`)
- Agent status endpoint (`/api/v1/agent/status`)

You can override the automatically derived identity:

```bash
# Set a specific session identity
dotnet build -p:MauiDevFlowSessionId=mysession
```

> **Note:** Session IDs are sanitized to lowercase alphanumeric characters only.
> For example, `My-Session` would become `mysession`. Auto-derived IDs (from the
> project path) are prefixed with `dw` and truncated to 26 characters. Explicit
> overrides keep the full sanitized value without prefix or truncation.

The same value can also be supplied via the `MAUI_DEVFLOW_SESSION_ID` environment variable.

For local debugging that needs full-path project disambiguation, opt in explicitly with
`-p:MauiDevFlowIncludeProjectPath=true`. This embeds the project path in the app assembly.

## Features

- **Visual Tree Inspection** — query the full MAUI visual tree via HTTP API or CLI
- **Element Interaction** — tap, fill, scroll, navigate, focus, resize, and mutate properties
- **Screenshots** — capture PNG screenshots from any platform (full window or per-element)
- **Screen Recording** — start/stop video recording of app sessions
- **Network Monitoring** — intercept and inspect HTTP requests/responses
- **Performance Profiling** — CPU, memory, GC, and jank detection with markers and spans
- **Performance Triage** — a bounded, capability-honest summary over the profiler streams (`maui devflow diagnostics performance`) that never invents a frame rate and always states what perturbed the run
- **Layout Diagnostics** — an on-demand, read-only scan of managed MAUI layout state (`maui devflow diagnostics layout`) with typed findings, per-rule coverage, and explicit limitations
- **Blazor CDP Bridge** — Chrome DevTools Protocol for Blazor WebViews (DOM, JS eval, navigation, input)
- **DevFlow Web Inspector** — the shared browser UI, embedded by MAUI DevFlow Inspector hosts for VS Code and GitHub Copilot Canvas
- **Global Mutation Lease** — prevents browser, VS Code, Canvas, MCP, and CLI callers from driving the app concurrently
- **Workflow Recording** — broker-owned recording observes successful mutations from every host and emits replayable Markdown
- **Click-to-XAML** — Debug source maps connect visual-tree elements to their XAML declarations
- **MCP Server** — 79 structured tools for AI agent integration
- **Logging** — buffered JSONL file logging with WebView JS console capture
- **Real-time Streaming** — WebSocket channels for logs, network, sensors, profiler, and UI events
- **Storage Access** — read/write app preferences, secure storage, discover file storage roots, and manage sandboxed app files remotely
- **Device Introspection** — battery, connectivity, geolocation, display, permissions, and sensor data
- **Dialog Handling** — detect and dismiss alerts/action sheets programmatically
- **Batch Operations** — execute command sequences from stdin for scripting
- **Agent Extensions** — expose app-specific diagnostic tools under `/api/v1/ext/{namespace}/...` with self-describing metadata for CLI and MCP discovery
- **Multi-Platform** — iOS, Android, Mac Catalyst, Windows, Linux/GTK, and separately reported experimental AppKit

## CLI Commands

All DevFlow commands are available under `maui devflow`. Run `maui devflow <command> --help` for details.

| Command Group | Description |
|---------------|-------------|
| `ui` | Visual tree, element interaction, screenshots, alerts, assertions |
| `recording` | Start, stop, and manage screen recordings of app sessions |
| `webview` | Blazor WebView automation — DOM, JS eval, navigation, input, screenshots |
| `logs` | Fetch and stream application logs |
| `network` | Monitor and inspect HTTP requests |
| `diagnostics` | On-demand layout scan and bounded performance triage |
| `storage` | Read/write app preferences, secure storage, discover file storage roots, and manage sandboxed app files |
| `agent` | Discover and inspect connected agents (status, list, wait, diagnose) |
| `extensions` | List, describe, and call app-specific DevFlow extension tools |
| `broker` | Manage the agent broker (start, stop, status, log) |
| `batch` | Execute command sequences from stdin |
| `commands` | List all available commands (schema discovery) |
| `mcp` | Start the MCP server for AI agent integration |

### DevFlow Global Options

These options apply to all `maui devflow` subcommands:

| Option | Description |
|--------|-------------|
| `--agent-port`, `-ap` | Agent HTTP port (auto-discovered via broker/.mauidevflow; falls back to 9223) |
| `--agent-host`, `-ah` | Agent HTTP host (default: localhost) |
| `--platform`, `-p` | Target platform (maccatalyst, android, ios, windows) |
| `--no-json` | Force human-readable output |

## Test Workbench platform qualification

The Testing package is a `net9.0` library and has compile-only consumers for .NET 9/.NET 10,
Android, iOS, Mac Catalyst, Windows, and experimental AppKit. Compilation and general agent
availability do **not** qualify a platform runtime. The required all-platform completion gate is
Android, iOS, Mac Catalyst, and Windows:

| Required platform | Workbench qualification status |
|---|---|
| Android | Engineering pilot only; currently **not-qualified** because the required real-device first-attempt evidence is absent. Emulator artifacts remain pilot evidence only. |
| iOS | Required gate not yet qualified. |
| Mac Catalyst | Required gate not yet qualified. |
| Windows | Required gate not yet qualified. |

| Separately reported experimental platform | Status |
|---|---|
| macOS AppKit | Experimental AppKit fixture and advisory macOS handoff; it never waives a Mac Catalyst gate. |
| WPF | Experimental; it does not waive the Windows gate. |
| GTK/Linux | Experimental; it does not waive any required MAUI gate. |

The macOS package-consumer job is an Apple **compile** smoke before a publish; it does not launch
an app, simulator, or device. A macOS QA handoff is still required for iOS and Mac Catalyst
runtime evidence. Detailed QA scripts are intentionally owned by the `platform-qa-scripts` todo.
The independent Appium lane is black-box smoke coverage, not the DevFlow flow-execution kernel or
a qualification pass.

## Documentation

- [DevFlow Web Inspector and MAUI DevFlow Inspector hosts](../../docs/DevFlow/inspector.md)
- [Broker Architecture](../../docs/DevFlow/broker.md)
- [Protocol Spec](../../docs/DevFlow/spec/README.md)
- [Human-authored Testing and platform qualification](../../docs/DevFlow/testing.md)
- [Restricted test-agent protocol](../../docs/DevFlow/test-agent.md)
- [Evidence privacy and artifact trust](../../docs/DevFlow/evidence.md)
- [Preview API and contract compatibility policy](../../docs/DevFlow/compatibility.md)
- [Android Setup](../../docs/DevFlow/setup-guides/android-setup.md)
- [Apple Platforms Setup](../../docs/DevFlow/setup-guides/apple-platforms-setup.md)
- [Windows Setup](../../docs/DevFlow/setup-guides/windows-setup.md)
- [Independent Appium black-box smoke tests](../../docs/DevFlow/appium-smoke-testing.md)

## Development

```bash
# Open just DevFlow in your IDE
open src/DevFlow/DevFlow.slnf

# Build
dotnet build src/DevFlow/DevFlow.slnf

# Run tests
dotnet test src/DevFlow/Microsoft.Maui.DevFlow.Tests/
```

### Real app integration tests

The simulator/emulator-driven suite is kept separate from the fast PR test pass and is intended to be run explicitly. Set `DEVFLOW_TEST_PLATFORM` to one of: `maccatalyst` (or `mac`/`catalyst`), `macos` (experimental AppKit only), `ios`, `android`, `windows`. Defaults to `maccatalyst` on macOS, `windows` on Windows.

```bash
# Mac Catalyst
DEVFLOW_TEST_PLATFORM=maccatalyst dotnet test src/DevFlow/Microsoft.Maui.DevFlow.Agent.IntegrationTests/

# iOS Simulator
DEVFLOW_TEST_PLATFORM=ios DEVFLOW_TEST_IOS_VERSION=18.x dotnet test src/DevFlow/Microsoft.Maui.DevFlow.Agent.IntegrationTests/

# Android Emulator
DEVFLOW_TEST_PLATFORM=android DEVFLOW_TEST_ANDROID_API=35 DEVFLOW_TEST_ANDROID_AVD=devflow-tests-api35 DEVFLOW_TEST_ANDROID_SERIAL=emulator-5580 dotnet test src/DevFlow/Microsoft.Maui.DevFlow.Agent.IntegrationTests/

# Windows (run on a Windows machine)
DEVFLOW_TEST_PLATFORM=windows dotnet test src/DevFlow/Microsoft.Maui.DevFlow.Agent.IntegrationTests/

# Experimental native AppKit (run only on macOS; never Mac Catalyst coverage)
DEVFLOW_TEST_PLATFORM=macos DEVFLOW_RUN_APPKIT_FLOW_QA=1 dotnet test src/DevFlow/Microsoft.Maui.DevFlow.Agent.IntegrationTests/ --filter Category=AppKitFlowQa
```

For local reliability, prefer running one platform suite at a time from a given repo worktree. Android fixture selection can be pinned with `DEVFLOW_TEST_ANDROID_AVD` and `DEVFLOW_TEST_ANDROID_SERIAL` when you want the harness to use a known emulator instance.

There is also a manual GitHub Actions workflow at `.github/workflows/devflow-integration.yml` for running the same suite in CI.

### Independent Appium black-box smoke tests

The opt-in `Microsoft.Maui.DevFlow.Appium.SmokeTests` project is a separate external lane for
release-like/uninstrumented apps, native compatibility, and optional Android/iOS system permission
dialogs. It does not use the in-app DevFlow agent, broker, semantic flow runner, repair services,
or qualification gates. See [Appium black-box smoke tests](../../docs/DevFlow/appium-smoke-testing.md)
for driver setup, environment configuration, platform limitations, and artifact retention.

## Version

Current version is managed in [`eng/Versions.props`](../../eng/Versions.props).
