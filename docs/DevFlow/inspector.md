# DevFlow Web Inspector

The DevFlow Web Inspector mirrors a running .NET MAUI app as a screenshot plus an interactive
visual-tree overlay. It is served directly at `http://localhost:19223/inspector/`.

The MAUI DevFlow Inspector host integrations embed that existing page in:

- MAUI DevFlow Inspector for VS Code;
- MAUI DevFlow Inspector for GitHub Copilot Canvas.

The broker-hosted page remains the **DevFlow Web Inspector**. **MAUI DevFlow Inspector** is the
public name for the host integrations added around it; all hosts embed the same page.

## Start the inspector

Add and start the DevFlow agent in a Debug build, launch the app, then:

```bash
# Select one connected app, start/focus the broker, and open its authenticated
# per-agent Inspector route in the system browser.
maui devflow inspect

# Print the URL instead of launching a browser (use --agent when multiple apps run).
maui devflow inspect --agent <agent-id> --no-launch
```

`maui devflow inspect --test <flow.md>` and `--trace <flow-run.json|mauitrace>` add only startup
hints to the shared shell. They never load, replay, import, or execute the supplied artifact.

You can still run **MAUI DevFlow: Open Inspector** in VS Code or open the MAUI DevFlow Inspector
in GitHub Copilot Canvas.

## Features

- screenshot and visual-tree inspection with hover, selection, and searchable hierarchy;
- a prominent disconnected-state overlay that preserves and clearly labels the last captured frame
  while DevFlow waits for the app to reconnect;
- tap, fill, scroll, gesture, navigation, theme, and live property mutation, with explicit
  **Apply to XAML** persistence for existing direct-literal attributes and runtime-owned property
  editor metadata;
- logs, live-updating network, preferences, device, sensor, native Alerts, read-only file browsing,
  and WebView/CDP data docks;
- **Layout** and **Performance** data docks for the on-demand diagnostics (see
  [On-demand diagnostics](#on-demand-diagnostics));
- click-to-XAML source navigation for Debug source maps;
- one integrated Tests workspace for authoring, loading project `maui-tests` files or local
  Markdown files, running them, and reviewing per-step results;
- an **Add to Copilot** context menu for the selected element, loaded workflow, both together, or
  the current bounded and redacted Data snapshot, including alert metadata;
- an **Evidence** action that previews exactly what a `.mauitrace` bundle would contain, then
  downloads it after explicit confirmation (screenshots stay opt-in) — see
  [evidence.md](evidence.md);
- responsive light, dark, and high-contrast host theming.

### Test Workbench human authoring (preview)

The prominent **Tests** toolbar action opens the shared Tests pane with one Data-style tab row:
**1 Goal**, **2 Steps**, **3 Review**, **4 Run**, **5 Results**, **Agent requests**, **Repair**,
**Improve**, and **Source**. The first five tabs retain progress state, while the remaining tabs are
stable peer destinations rather than stacked modes or an inline Advanced-tools disclosure. The
existing Workflow timeline remains passive active-test status; Interact/Inspect, the tree,
screenshot, properties/source, Data dock, and Evidence controls remain unchanged.

#### First test

1. Open **Tests** and enter the required **Goal** describing what the test must prove. This is the
   only field required before adding steps. Optional metadata is split into four collapsed groups:
   **Name and file**, **Scenarios and outcomes**, **Setup, safety, and platforms**, and
   **Review metadata**.
2. Select **Record steps** and perform the app interactions. Recording does not start a run.
3. Stop recording. DevFlow opens **Review** with a compact recorded-step list. Select a step to
   rename, reorder, remove, edit its selector, or add an expected result. Use **Record more steps**
   to append another demonstration, then select **Save test**.
4. Select **Run**. Review the concise target, safety, and side-effect summary, then choose
   **Review and start** and confirm. Saving never runs a test automatically.
5. DevFlow opens **Results** for every terminal outcome. Use the pass/fail banner and next actions
   to check and run again, improve the test, inspect a failed step, or review an eligible repair.

The Goal page also includes a collapsed **Create this test with your agent** guide. It copies a
ready-to-paste prompt that asks the restricted agent to prepare the complete draft and return only
the save decision to **Requests**. Saving and running remain separate human decisions. Failed
Results and Improve expose equivalent contextual prompts for diagnosis and read-only quality
review; agent repair suggestions are never applied directly.

Each workflow tab uses progressive disclosure. **Steps** shows only Goal recovery, active recording,
or the captured-step summary that is currently relevant. **Review** withholds save/run actions until
steps and an expected result exist. **Run** sends unsaved work back to Review and exposes one
current readiness action at a time. **Results** unlocks only when a result exists; technical
evidence, compatibility, and local study controls remain collapsed until explicitly opened.

Unavailable destinations are disabled in the tab row instead of opening premature empty screens:
Goal unlocks Steps; captured steps unlock Review; a saved test with an expected result unlocks
Run; a pending/recent request unlocks Agent requests; a failed local result unlocks Repair; a
loaded test unlocks Improve; and a selected source-mapped control unlocks Source. Hover/focus text
states the missing prerequisite, and arrow/Home/End navigation skips disabled tabs. Results unlocks
after a run produces a result. **Import result** is the separate toolbar entry for read-only
diagnostic-result import, so an empty Results tab cannot bypass a disabled workflow stage.

The managed Tests path requires a Goal, keeps the plan bound to the recorded flow, and saves the
Markdown flow plus its plan together. Raw quick-record and plan-sidecar maintenance are not exposed
as first-step choices. The old direct replay remains available only as **Legacy quick replay
(advanced)**; the primary **Check run** toolbar action always opens the broker run check.

The **Goal**, **Steps**, and **Review** tabs support explicit, human-authored test drafts. They do not start a
run, import a trace, invoke a provider, apply a repair, or write app source.

- Plan sidecars live beside their canonical Markdown flow as
  `maui-tests/<flow-base>.maui-plan.json`. The form records goal, scenarios, assumptions,
  preconditions, reset and side-effect policy, platform/capability requirements, independent
  oracles, acceptance criteria, provenance, and review metadata.
- Loading and saving use plan revision, plan digest, and flow digest compare-and-swap checks. A
  stale sidecar is never overwritten until the human selects the explicit confirmation action.
- Review displays compact recorded-step rows and one selected-step editor. Moving or removing a
  step changes only the draft; **Record more steps** appends a new recording to the existing draft.
  Selector edits must validate exactly one live match; raw runtime IDs cannot be promoted.
- **Add expected result** creates `exists`, `propEquals`, or `routeIs` verification, or an
  observation-only `pageChanged` note, for the selected step. It never pre-fills or persists
  secret-shaped values. Optional current-app checking uses the same strict runner semantics.
- **Check test**, **Review changes**, and **Save test** are all explicit. Save test writes canonical
  Markdown and its sidecar as a rollback-protected local bundle, or reports no success. It never
  replays the resulting flow.

Browser hosts use the registered project root only. When that local workspace is unavailable, the
browser downloads the flow/plan artifacts. VS Code and Canvas can handle a bounded
`saveTestBundle` bridge request that accepts only a top-level filename, the paired contents, and
their bound flow/plan digests. Host capability details are shown only when an attempted save needs
that fallback; Goal does not display a proactive unsupported-host warning.

### Prototype-study evidence (local only)

Results includes a collapsed **Prototype evidence (local only)** card for local usability studies
of the Workbench journey. It is a bounded `sessionStorage` journal for the current browser tab, not
telemetry: it has no HTTP endpoint, client, upload, or background egress. The explicit
**Download session evidence** control creates a JSON file locally; **Clear local session evidence**
requires a second confirmation action and starts an empty local journal.

The journal retains only event timestamps, safe human/agent provenance enums, booleans, bounded
counts and durations, and locally pseudonymized run/approval/proposal references. It never retains a Goal,
UI text, typed value, selector, flow, path, source, screenshot, prompt, reviewer identity, URL,
device serial, payload, or secret. Its summary includes authoring mode, time to Goal, recording and
review-to-save duration, first-result and run durations, saved-step/assertion/selector counts,
session replay stability, safe failure-class counts, a diagnosis proxy, agent request/decision/
expiry/staleness/consumption counts and decision durations, repair funnel counts, and Improve
scan/finding counts. Missing fields and retention/storage limitations remain explicit.

`localSessionOnly: true` is present in every export. These descriptive prototype-study measures
help identify where a human must review a failure or approve/verify a repair; they do **not**
qualify a platform, certify a device, establish replay correctness, or replace digest-bound
flow-run, qualification, or device evidence.

### Agent proposal approval bridge

The Tests tab row contains a first-class **Agent requests** inbox. Restricted MCP agents submit
bounded `approval-request` records to the broker; pending requests add the same count badge to the
**Tests** toolbar action and the **Agent requests** tab. Each card first shows only the requested
task, bounded summary, and expiry. **Review permissions and decide** expands the exact action,
selector, route, side-effect, action-count, value-byte, target, and decision controls. Recent
decisions remain collapsed.

A new request never steals the active tab. Its badge and status announcement indicate that review
is waiting. An approval deep link opens **Tests > Agent requests**, scrolls to the matching card,
and moves focus into it; returning to Goal, Steps, Review, Run, Results, or a tool is a normal tab
selection rather than closing an overlay.

The human can uncheck permissions or reduce numeric limits, confirms the reviewed scope, then
selects **Approve exact scope** or **Reject**. The Inspector revalidates the connected app instance
and broker draft before issuing any grant. The browser never receives the host-approval token or
grant; the restricted agent retrieves an approved grant only through its read-capability-protected
authoring status. Typing `approved` in chat has no security meaning.

Approved requests become `consumed` when their bounded action count is exhausted. Rejected,
expired, stale, and consumed requests cannot be reused. A changed app instance, build, plan/flow
revision or digest, or changed seed/backend state when those optional fingerprints were attested,
fails closed and requires a fresh request. The inbox cannot approve
repair application, source changes, lease takeover, arbitrary files/network/CDP, or any capability
outside the restricted test-agent action set.

The provider-neutral `requestTestProposal` host-bridge message remains reserved for future native
host presentation and is not advertised as an available capability. Canvas, VS Code, and the
browser all receive the shared broker-owned inbox when they embed the Inspector; they do not need
to mint ambient credentials.

Canvas reports source apply unsupported. VS Code can open a native reviewed diff and ask its local
human to confirm a bounded source apply, but it cannot approve itself, apply a flow repair, or
start an unapproved mutation. Browser hosts can download a patch and can apply only when they
explicitly advertise a bounded local-host action. See [Restricted test-agent protocol](test-agent.md).

### Improve (selector health)

The initial **Improve** view answers one question: is there a test to scan? With no loaded test it
shows only **Go to Steps**. With a test it shows **Scan test**; live-tree input stays under
**Scan options**. Findings appear only after the read-only scan, while filters and route/platform
coverage remain optional collapsed details.

**Improve** analyzes the loaded flow and plan, optional live tree facts, and retained run history
with the shared deterministic Testing package. Findings group by severity/category/step/source/
platform and expose rationale, evidence references, and links back to Steps, Trace, or the source
anchor. A scan becomes visibly stale after the flow or plan changes; **Rescan analysis** is a
read-only, explicit operation.

The tab displays fixed-rule score evidence and `uncalibrated` calibration state, never a
probability. It reports `DFSH001`–`DFSH011` for duplicate/missing durable IDs, fragile
runtime/type-index/text selectors, templates/virtualization, source staleness, managed/native
divergence, platform gaps, assertion gaps, plan coverage, and route/platform coverage. It has no
Apply, repair, source-write, model, or automatic selector-fallback control.

#### Resolving an ambiguous selector

An **ambiguous selector** is different from locator drift. Drift means a formerly valid selector
finds no control at a matching pre-dispatch checkpoint; only that narrow `locator-not-found` case
can enter the separate human-approved repair policy. Ambiguity means the selector currently finds
two or more controls. It may indicate duplicate `AutomationId` values, a repeated template, or an
app regression. DevFlow never chooses one automatically because that could hide the regression or
tap the wrong control.

For a terminal `locator-ambiguous` (or legacy `ambiguous`) result:

1. Select **Resolve N matches** in Results. It opens **Improve** and re-verifies the
   failed step's active selector against the current app.
2. Review the bounded safe cards (at most 20): ephemeral ID, type/role, AutomationId,
   visible/enabled state, bounds, and source-map presence/line. The cards never disclose text,
   values, property data, or source paths.
3. Use **Highlight in app** to inspect a human-selected candidate. **Use this AutomationId** is
   available only when the ID is distinct in the complete returned list; DevFlow then re-verifies
   it globally and requires exactly one live match before changing only the failed step's draft
   selector.
4. If no safe unique ID exists and the intended candidate is source-mapped, choose **Improve app
   testability**. This selects the element and opens a reviewed Source proposal; it does not
   write source or change a flow selector.
5. After a draft-only selector update, explicitly **Save test**, then explicitly rerun it. Resolve
   matches never commits, reruns, repairs, or applies source automatically.

When the list is truncated, duplicated, or has no usable ID, treat it as diagnostic evidence only:
highlight the intended control, improve its testability where safely mapped, or author a selector
manually through the normal unique-validation path. The deterministic Improve scan may focus the
failed step and still reports duplicate-AutomationId findings, but it never creates a repair
proposal.

### Repair (human-approved selector repair)

The initial **Repair** view shows only the current safe action: **Open Results** when no failed local
test exists, **Check latest failure** when one can be classified, or **Create suggested update**
when eligibility passes. A suggestion then advances one reviewed action at a time: **Review
suggested update**, **Try this update**, **Approve update**, and **Apply update**. Classification
evidence, selector proof, and policy rules remain collapsed. **Diagnose with your agent** copies a
bounded prompt for failure explanation or an inert suggestion; agent-originated suggestions are
never applied directly.

The **Repair** tab explains eligibility rather than guessing. It accepts only a primary
pre-dispatch `locator-not-found` with a complete matching current checkpoint and trusted local
evidence. It shows every blocking reason for route/login/modal/locale/theme/orientation/seed/
display/collection drift, ambiguity, assertions, unknown completion, infrastructure, unsafe data,
capability gaps, virtualized rows, and non-replayable side effects.

`locator-ambiguous` is explicitly not self-repair eligible. The Repair tab redirects people back
to **Resolve matches** in Results and **Improve** rather than generating a candidate or guessing a
control.

Candidate cards retain deterministic selector-health rank components, rationale, risk flags,
fingerprint/uniqueness evidence, and `uncalibrated` calibration. The preview is a minimal
selector-only JSON/Markdown diff that proves actions, assertions, expected values, and step order
are unchanged. Imported `untrusted` and `attested` artifacts stay diagnostic-only; only a current
local run or `locally-reproduced` import can create an approvable proposal.

Validation needs a human-issued, bounded grant and a lifecycle-capable host. It hard-resets app and
backend/test data, verifies fingerprints, then replays to the failed step using an in-memory
override only. If no lifecycle host is connected, the tab reports that honest fallback and does not
consume or persist a candidate. Apply is absent for agent-originated proposals until a human
validation and approval grant complete. An applied repair requires three clean verification
replays; a failed verification becomes `rollback-required`, and rollback creates a new flow
revision. The tab never offers app-source changes or automatic repair.

### Source (reviewed XAML and C# AutomationId proposals)

The initial **Source** view shows only **Select a control first**. Once a mapped control is selected,
the user chooses XAML or C#, enters the new AutomationId, and selects **Check source**. Eligibility
then exposes **Create source proposal**; a proposal advances through preview, approval, and a
host-supported apply or patch download. Build, flow, host, and policy facts remain collapsed.

The **Source** tab is a separate proposal path beside property persistence and selector repair. It
accepts only a current, unambiguous Debug source map whose hash matches a registered,
project-contained, non-generated, non-linked `.xaml` file without a reparse-point escape. The
mapped declaration must be a direct static element start tag. The only operation is adding or
replacing a literal `AutomationId` that follows the ASCII test-ID grammar, is nonempty and
nonlocalized/non-user-derived, and is unique in both project and current live scope.
The grammar is `^[A-Za-z](?:[A-Za-z0-9]|[._-](?=[A-Za-z0-9])){0,127}$`; lexical
screens also reject binding/user/secret-shaped terms rather than deriving identifiers from visible
text or app data.

The analyzer fails closed for bindings, resources and markup extensions, conditional/generated
elements, DataTemplate/ControlTemplate/style/setter/resource scopes, BindableLayout/repeaters and
virtualized items, native and WebView synthetic nodes. It displays every code, exact XAML diff,
file/hash/line/source anchor, old/new literal, uniqueness evidence, affected flows and official
platforms, risks, host capability, and pending build/runtime/remap/replay/oracle work.

Source approval is source-specific and single-use; it is bound to proposal, patch digest, file
hash, project identity, affected-flow references, host, and expiry. A local host does an immediate
compare-and-swap atomic write—there is no force apply. **Canvas never applies source**. VS Code can
open a native diff and, after a human confirmation, coordinate the bounded local write. Browsers
can download the patch or use an explicitly capable local host.

Applying source never changes a flow selector. Every affected official target TFM buildable on the
current host must build, then the app must rebuild/relaunch, remap the changed declaration, prove
runtime uniqueness, replay affected flows, and pass an independent oracle. iOS and Mac Catalyst
targets unavailable on Windows remain explicit `pending-external-qa`, not verified. A failed
verification becomes `rollback-required`; rollback atomically restores the prior bytes and appends
a redacted hash-linked history event. Any selector follow-up is a new separately reviewed flow
repair proposal.

#### Roslyn-proven C#

The Source tab can also select **C#** mode. It accepts only a mapped registered `.cs` document
whose Roslyn semantic model proves one supported MAUI actionable control at one direct object
initializer or direct literal `.AutomationId` assignment. The proposal records a document/span/hash,
semantic symbol/type, exact minimal forward patch, and exact inverse patch; it does not broadly
format or rewrite C#.
If the running element does not expose a C# map, VS Code can contribute only the active saved C#
selection's file/line/column/hash; the broker independently confines and re-proves that selection.

DataTemplate/ControlTemplate/repeater/item-factory/BindableLayout code, collections/lambdas/
factories, virtualized items, generated/linked/outside-project files, conditional/preprocessor
branches, reflection/dynamic construction, Shell/native/WebView elements, and computed/bound/
localized/user-derived or duplicate IDs are rejected. The broker has no C# source-write route.
After human approval it records `awaiting-host-apply`; a native IDE host opens a diff, applies the
exact patch, and acknowledges its pre/post hashes and digest. Browser download is read-only and
Canvas reports C# apply unsupported. A failed verification requires an IDE-mediated inverse patch
and a new redacted history event; selectors remain a separate repair proposal.

### Run and Trace (preview)

The **Run** tab is a broker-owned preflight, not a browser replay implementation. It shows the
selected flow/plan revision and digests, exact agent instance, app/build/platform facts reported by
the live agent, declared capabilities, reset/seed/backend/oracle/precondition evidence, and a
value-free summary of planned tap/fill/navigation/theme/property effects. Secret references are
named, but their values are never displayed.

The broker evaluates the canonical safety admission before a run can start. The human must check
the explicit acknowledgement; a `non-replayable` plan also needs a distinct one-shot
authorization. Missing reset, precondition, or oracle evidence remains a broker warning or
rejection—it is never inferred from a checkbox. Legacy/unspecified policy, stale plan binding,
missing oracle, and unknown/orphaned completion are visibly called out.

Run distinguishes blockers from verification notes. A genuine flow/plan digest mismatch disables
Run and sends the user to Review to **Save updated test**. The Inspector preserves the broker's
canonical flow digest rather than recomputing an incompatible browser hash. A missing independent
business oracle does **not** block ordinary replay; it appears under **Verification notes** and
means the result can run/pass but cannot be labeled independently verified.

Start sends an idempotency key, exact target instance, flow Markdown, plan, and constrained safety
context to the broker workflow-run service. The Inspector stores the returned run capability only
for its own short-lived session/journal restoration, polls with bounded backoff, and shows queued,
lease acquisition, preparation, running, cancellation-pending, and all terminal states. Cancelling
prevents future steps but does not claim an in-flight command was undone.

The **Trace** tab renders the bounded structured run report: ordered steps and first divergence,
timing/outcome/failure class, selector resolution, actionability ladder, command receipt certainty,
redacted assertion disclosures, reset/precondition/oracle facts, artifacts, report digest, and
explicit truncation/omission notices. `[` and `]` move through steps; every visual detail has text
equivalents. Failure evidence is a linked redacted `.mauitrace` v1 download when retained by the
Inspector. Screenshots and flow text are both off by default and require separate preflight
consent.

**Pick trace** imports only `flow-run.json` or `.mauitrace` v1 through the broker artifact-trust
boundary. Browser hosts use a bounded local picker; VS Code supplies its native bounded picker;
Canvas honestly reports when no native picker is available. Imports retain only an isolated,
redacted projection with an `untrusted`, `attested`, or `locally-reproduced` trust state. While an
imported trace is selected, the Inspector is captured/read-only: Interact, Record, Run, mutations,
property/source application, lease takeover, CDP evaluation, and effectful Data controls are
disabled. No imported file is replayed automatically or made repair-authoritative. **Reproduce
locally** leaves the import untouched and opens a separate, explicitly confirmed live preflight;
matching source facts unavailable to the host remain unavailable rather than guessed.

## Architecture

```text
Browser / VS Code / Canvas
          |
          v
DevFlow broker-hosted inspector
          |
          v
In-app DevFlow agent
```

The broker discovers agents and serves the HTML/CSS/JavaScript bundle. Inspector mutations are
proxied to the selected in-app agent. VS Code and Canvas embed the same page and add an
authenticated host bridge for source navigation, recording persistence, and Copilot context.

## Host-adaptive layout

The shared inspector keeps one interaction model while adapting its chrome to the host viewport:

- **Wide browser/editor:** tree, screenshot, and properties are docked as three panes.
- **Compact editor:** the tree remains available while properties open as a drawer, preserving
  screenshot width.
- **Narrow Canvas/editor:** the screenshot is primary; tree and properties become coordinated
  full-height drawers with a scrim.
- **Short host:** drawers and overlay Data/timeline sheets protect the screenshot's vertical budget.

The toolbar keeps interaction mode, tree, fit, and recording visible. Secondary actions remain
inline for as long as they fit; only the non-fitting actions move into the **More** menu. More can
open over an active Data or properties surface, and Copilot choices open as a nested submenu.
Host bridges also supply their color palette, font metadata, contrast mode, and reduced-motion
preference. VS Code placement is configurable with
`mauiDevflow.openLocation` (`auto`, `beside`, or `active`).

## Coordinated frames and coordinates

Each inspector refresh creates an immutable frame containing:

- the visual tree used for the overlay;
- the exact screenshot bytes;
- screenshot dimensions;
- the screenshotted root page and its window offset;
- rendered element HTML.

The screenshot URL includes the frame ID. Expired frame screenshots return `404`, causing the
client to refresh state instead of pairing a new screenshot with stale bounds. When a modal or
sheet is screenshotted, only that page subtree is rendered, preventing underlying window chrome
from producing negative offsets.

Browser coordinates are converted from the fit-scaled viewport back to screenshot coordinates,
then translated to window coordinates with the frame's root offset before hit testing.

## Global mutation lease

All state-changing calls use one lease per running app. Browser tabs, VS Code, Canvas, MCP, and CLI
therefore cannot drive the app concurrently.

- The first mutating host claims the lease.
- Read-only inspection remains available to other hosts.
- A host can release or explicitly take over the lease.
- Lease release or takeover hands an active app-scoped recording to the next valid lease holder.
- Closing Canvas releases its lease; abandoned leases also expire automatically.

The lease coordinates writers; it is not an authentication boundary.

## Workflow recording

Recording is owned by the broker and scoped to the current app. The current valid mutation lease
holder controls it. The agent observes successful supported mutations from every host, so a
workflow can begin in the browser, continue through Canvas or MCP after lease handoff, and stop in
VS Code without separate local recorders.

The Workflow panel can also load an existing test from the registered app project's top-level
`maui-tests` directory or from an OS-selected `.md` file. Project files are confined to that
directory, capped at 1 MB, and parsed and validated before Inspector makes them replayable.
Replay results stay in the same Workflow panel instead of opening a separate report surface.

Currently normalized actions include tap, fill, scroll, navigate, back, theme changes, and property
changes. The result is a Markdown file with an authoritative `json maui-test` block for replay.
Replay is blocked while recording is active.

Active recordings are atomically spooled under `~/.mauidevflow/recordings/` with a schema version,
bounded size/steps, expiry, and per-user permissions. A temporary agent disconnect or broker
restart therefore preserves the active recording; explicit stop, cancel, or expiry deletes it.
Corrupt spools are quarantined and reported in the broker log.

Live recordings are process-instance scoped, so two devices running the same package never append
to one another. Rebuild recovery requires the random `recordingId` capability returned at start;
package/TFM identity alone cannot adopt another process's recording. Inspector retains a bounded
set of active capabilities in that host panel's browser session storage and removes each one when
its recording is stopped or discarded; separate tabs/panels do not share resume authority. A
different panel can explicitly join a still-live shared recording by pressing **Record**, which
returns that active recording's capability under the normal workflow controls.

Schema 2 adds selector diagnostics (`matchCount`, `quality`, and `fragilityReasons`) while preserving
schema 1 parsing and replay. AutomationId and exact-text selectors must resolve exactly one element;
duplicate matches fail loudly. Replay waits for visible/enabled targets and for positive stable tap
bounds, stops at the first divergence by default, and can keep a redacted in-memory evidence bundle
for download after a failed Inspector replay. It does not claim to detect platform-specific
occlusion.

## Click-to-XAML

The Agent.Core package supplies a build-transitive source generator. In Debug builds it maps XAML
elements to source file, line, column, and a build-time content hash. VS Code compares the current
file hash before opening the recorded line and warns when the file changed after the app was built.

Source locations are emitted only when the runtime element can be matched conservatively to its
XAML declaration. Repeated same-type siblings need sibling-unique `AutomationId` values; otherwise
their source actions are withheld rather than risk opening or editing the wrong declaration.

Source maps are disabled outside Debug by default because they embed XAML text and source paths.
They can be disabled explicitly:

```xml
<DevFlowXamlSourceMapsEnabled>false</DevFlowXamlSourceMapsEnabled>
```

### Apply property values to XAML

For source-mapped elements, each supported property row includes an **Apply to XAML** button. Live
editing remains runtime-only until this button is selected. The broker then updates only an
existing direct-literal attribute in the registered app project while preserving the rest of the
file, its encoding, and line endings.

The write is rejected when the property comes from a binding, resource, markup extension, style,
property element, template, or code-created element. It is also rejected when the source changed
outside Inspector after the app was built or after the previous Inspector write. Rebuild the app
to refresh stale source maps.

The broker validates the value against the running element before writing and restricts edits to
the agent-advertised property grid. Current agents describe editor kind, current value, writability,
enum choices, and numeric constraints from the runtime control. It binds relative project names to the build's default path-derived
DevFlow session identity; builds using a custom `MauiDevFlowSessionId` should also set
`MauiDevFlowIncludeProjectPath=true` to provide an unambiguous project root.

`AutomationId` intentionally remains outside this generic property allowlist. Its reviewed
source-proposal lifecycle is described in the **Source** tab above and cannot be reached by
**Apply to XAML**.

## Platform boundaries

The WebView data tab lists attached Blazor WebViews, displays page source, and evaluates JavaScript
through the existing CDP bridge. Every expression requires confirmation because arbitrary
JavaScript can read or change live application data. A bundled Chrome DevTools frontend is
intentionally outside the Inspector scope; use external browser platform tools when the
full DOM, console, network, and debugger experience is required.

System dialogs and MAUI alerts are outside the in-app MAUI visual tree. The **Alerts** data tab uses
the existing platform drivers to detect and dismiss them without pretending they are selectable
MAUI elements. Detection remains read-only. Dismissal requires the mutation lease and is blocked
while workflow replay is driving the app.

Availability of an Inspector control or an alert driver is not platform qualification. The
Workbench all-platform gate still requires independent Android, iOS, Mac Catalyst, and Windows
runtime evidence; Android is currently an engineering pilot and is **not-qualified** without
real-device evidence. AppKit, WPF, and GTK remain separately reported experimental lanes. See
[human-authored testing](testing.md) for the gate matrix and
[the preview compatibility policy](compatibility.md) for contract expectations.

- Android actions target only the online device whose existing ADB forward owns the selected
  agent port. Missing or ambiguous ownership is rejected.
- Windows and Mac Catalyst actions refresh and use the exact app process ID reported by the agent.
- Linux actions connect the platform driver to the exact selected agent.
- iOS alert control remains CLI-only because Inspector registration does not carry a simulator
  UDID. Use the platform alert driver explicitly:

```bash
maui devflow ui alert detect --device <simulator-udid>
maui devflow ui alert dismiss "OK" --device <simulator-udid>
```

DevFlow Action request bodies are passed to the registered action. A generic
`include: ["screenshot", "tree"]` field does not add post-action captures; use the CLI/MCP
post-action options or request the screenshot and tree explicitly.

## On-demand diagnostics

Two data docks expose the read-only diagnostics described in
[src/DevFlow/README.md](../../src/DevFlow/README.md). Both are token-gated `POST` reads that proxy
the shared driver analysis, so the Inspector, the CLI, and the MCP tools cannot diverge.

**Layout** runs one explicit scan when the tab is opened or refreshed. It renders the summary, the
per-rule coverage table, the findings, and the report's limitations; clicking a finding selects the
affected element and opens its property grid. There is no automatic re-scan, no watch mode, and no
screenshot or frame refresh triggered by a diagnostic — a diagnostic must never change what you are
looking at. A scan with no findings is presented next to its coverage, never as a clean pass:
unevaluated geometry is `incomplete`.

**Performance** requires an explicit **Start recording**. While a session is active the tab polls
its own panel every three seconds and nothing else; stopping ends the polling. Buffer loss is
promoted above the metrics, and display-cadence estimates are never rendered as a frame rate. In a
normal Debug build the panel shows a warning that Hot Reload, the debugger, and DevFlow diagnostics
perturb the measurement; in an explicit read-only profile build it reports the low-perturbation
state instead. Starting or stopping a session is blocked while workflow replay is driving the app,
because a profiler session would perturb the run being replayed.

Profiler sessions are single-owner. If another CLI/MCP/Inspector client already started one, this
Inspector shows it as a read-only attachment: **Stop** remains disabled and starting a replacement
returns a conflict. The creator holds a separate opaque stop token (kept inside the host, not
exposed through status); a creator stop captures one final boundary sample before returning the
final summary. Memory rows distinguish managed heap, total process resident/physical footprint,
and native-heap-specific counters instead of treating process footprint as unmanaged memory.

Neither tab participates in **Add to Copilot**. The Copilot data-context scopes are limited to the
bounded, redacted snapshot shapes that module already sanitizes; layout and performance payloads are
not in that set, so the attach button stays disabled on these tabs. Use `maui_layout_diagnostics`
and `maui_performance_snapshot` when an agent needs the data.

## Compatibility

Current clients remain read/write compatible with older agents that do not expose the lease
endpoint. Older clients cannot mutate current agents because they do not send a lease identity.
Inspector falls back to its legacy static property table when an older agent does not expose
runtime descriptors. The Node client negotiates `ui.events`; unsupported agents enter a stable
`polling-only` state and recheck the capability every 60 seconds so an in-place upgrade recovers
without reconnect churn. Upgrade the agent packages and host tooling together.

## Implementation

| Area | Location |
|---|---|
| Inspector server and proxy | `src/Cli/Microsoft.Maui.Cli/DevFlow/Inspector/` |
| Shared web UI | `src/Cli/Microsoft.Maui.Cli/DevFlow/Inspector/Web/` |
| Broker routing and coordination | `src/Cli/Microsoft.Maui.Cli/DevFlow/Broker/` |
| XAML source maps | `src/DevFlow/Microsoft.Maui.DevFlow.Agent.Core/SourceMapping/` |
| Shared Node client | `src/DevFlow/js/devflow-client/` |
| VS Code host | `src/DevFlow/js/vscode-inspector/` |
| Copilot Canvas host | `.github/extensions/maui-devflow-canvas/` |
| Playwright tests | `src/DevFlow/Microsoft.Maui.DevFlow.Inspector.Tests/` |
