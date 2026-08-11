# DevFlow Copilot host integration plan

## Purpose

Deepen the integration between the MAUI DevFlow Inspector and GitHub Copilot without creating a
second inspection, diagnostics, flow, evidence, or mutation implementation.

This plan covers:

- a VS Code `@devflow` Chat Participant;
- VS Code Language Model tools;
- versioned Inspector deep links;
- runtime Problems and layout findings in VS Code Diagnostics;
- editor Code Actions;
- compact MCP Apps for supported chat clients; and
- artifact-first integration for GitHub Copilot cloud agent and code review.

The work is intentionally split into independently shippable phases. The VS Code work should ship
before MCP Apps or cloud integration because it has the strongest local broker access and provides
the host services that later phases reuse.

## Existing foundations

The implementation must build on these existing boundaries:

| Foundation | Current location | Reuse requirement |
|---|---|---|
| Shared Inspector UI and host bridge | `src/Cli/Microsoft.Maui.Cli/DevFlow/Inspector/Web/` | Browser, VS Code, Canvas, and MCP Apps use the same bounded presentation contracts. |
| Canonical Inspector snapshot | `InspectorSnapshotService.cs`, `InspectorSnapshotClient.cs`, `@maui-devflow/client` at `src/DevFlow/js/devflow-client/` | Runtime element identity comes from the broker snapshot; hosts do not walk the app independently. |
| Driver diagnostics | `AgentClient`, `DiagnosticProblems.cs`, `LayoutDiagnosticsModels.cs` | Problems and layout findings keep their existing typed source metadata and coverage semantics. |
| Evidence capture and hostile-input reader | `src/Cli/Microsoft.Maui.Cli/DevFlow/Evidence/` | All local and cloud projections use the same redaction and validation rules. |
| Flow-run contracts | `Microsoft.Maui.DevFlow.Testing/Contracts/MauiFlowRunReport.cs` | MCP Apps and cloud tools render the canonical bounded report. |
| MCP tools | `src/Cli/Microsoft.Maui.Cli/DevFlow/Mcp/` | Existing tools remain authoritative; UI metadata is additive. |
| VS Code host | `src/DevFlow/js/vscode-inspector/` | Chat, diagnostics, Code Actions, URI handling, and Inspector hosting live in one extension. |
| Copilot Canvas | `.github/extensions/maui-devflow-canvas/` | Canvas behavior remains unchanged and consumes any new shared client contracts. |

## Architectural decisions

### 1. Host orchestration stays separate from runtime truth

The VS Code extension may decide which app, editor, participant command, or diagnostic the user
means. It must not duplicate visual-tree walking, source mapping, layout analysis, evidence
redaction, flow execution, or mutation logic.

### 2. Add one typed TypeScript gateway

Introduce a typed `DevFlowWorkspaceSession` in the existing `@maui-devflow/client` package at
`src/DevFlow/js/devflow-client/`. It owns broker discovery, agent selection, Inspector route
construction, and bounded reads for:

- active snapshot and query;
- Problems;
- layout diagnostics;
- evidence preview metadata;
- retained flow-run metadata; and
- agent lifecycle identity.

The VS Code extension and Canvas use this gateway. It calls the existing broker-hosted Inspector
endpoints, which already delegate to the canonical Driver and C# services. It does not introduce
new direct-agent semantics.

The gateway returns discriminated success/error results and carries the broker port, agent ID,
agent instance ID, snapshot revision, and capture time needed to reject stale UI state.

### 3. Host-only LM tools remain narrow

VS Code Language Model tools expose editor or Inspector host state that generic MCP cannot know:
the active Inspector panel, selected editor, selected live element, and the last explicitly
attached or displayed evidence. The existing DevFlow MCP server remains the broad automation API.

### 4. Diagnostics are observations, not build diagnostics

The VS Code Diagnostic collection represents facts from one running app instance. Every diagnostic
must include a source label, target identity, capture time, and stable in-memory fingerprint.
Diagnostics are cleared when the target app instance changes, disconnects beyond the grace period,
or the user clears the relevant DevFlow surface.

Layout diagnostics remain explicitly on-demand. The extension must not start periodic layout scans
merely to populate the Problems panel.

### 5. MCP Apps are an additive rendering channel

An MCP tool continues to return useful text and structured content to every client. Supported
clients may additionally render a `ui://` resource. Unsupported clients receive the existing text
result and lose no capability.

The app UI cannot bypass the MCP tool, broker, mutation lease, evidence consent, or source-apply
approval boundaries.

### 6. GitHub integration is artifact-first

GitHub-hosted agents cannot reach a developer's localhost broker. Cloud integration consumes
bounded CI artifacts, flow-run reports, evidence bundles, and trusted handoff envelopes. Live app
control is out of scope unless a separately reviewed authenticated relay is designed later.

## Phase 0: compatibility and contract spike

Before product implementation:

1. Upgrade `@types/vscode` from its current 1.90 floor to at least the selected engine version.
   Determine the minimum VS Code version where every required Chat Participant, URI handler, LM
   tool, diagnostics, Code Action, and MCP server-definition API is stable and Marketplace-eligible,
   not a proposed API. Remove `any` shims where that version provides stable types.
2. Set `engines.vscode` from that result and document which features degrade on older hosts.
3. Pin `maui-labs.maui-devflow-inspector` as the public extension identifier before any CLI,
   report, artifact, or documentation emits a DevFlow URI. Keep the identifier in one shared
   constant used by URI generation and tests.
4. Verify whether `ModelContextProtocol` 1.1.0 can emit MCP resource templates, structured tool
   content, and the MCP Apps `_meta` fields required by supported clients.
5. If the .NET SDK cannot express the required MCP Apps metadata without protocol-unsafe JSON
   manipulation, update the centrally managed package version before implementing Apps. Do not add
   a second Node MCP server solely for UI support.
6. Build one offline MCP App proof that renders a static Problems payload and still returns a useful
   text result to a client that ignores `ui://`.
7. Produce a written go/no-go result:
   - **go:** record the required .NET MCP package version and migration work;
   - **defer:** ship the VS Code, diagnostics, Code Action, and artifact-first phases without MCP
     Apps until a supported .NET protocol path exists.

**Exit criteria:** supported protocol shapes, minimum versions, fallback behavior, and package
changes are proven in tests before production UI work starts. MCP Apps delivery work cannot enter
implementation until the Apps gate is **go**.

## Phase 1: VS Code service layer and lifecycle

### Shared client

Extend the existing `@maui-devflow/client` package at `src/DevFlow/js/devflow-client/` with:

- `DevFlowWorkspaceSession`;
- typed Problems, layout, evidence-preview, and retained-run contracts;
- exact-agent resolution by agent ID, instance ID, or port;
- stale snapshot/agent-instance guards;
- bounded route and query construction; and
- lifecycle events for connected, restarted, disconnected, and selected-agent-changed.

Add runtime guards for every broker response. Generated URLs remain loopback-only and must match
the discovered broker state.

### Extension state

First add characterization tests around the current bridge, panel restart handling, selection/Data
tools, Copilot attachment, source navigation, and test bundle operations. Then mechanically extract
`vscode-inspector/src/extension.ts` into cohesive modules without changing behavior:

```text
src/
  extension.ts
  devflow-session.ts
  inspector-panel.ts
  copilot-participant.ts
  language-model-tools.ts
  uri-handler.ts
  diagnostics.ts
  code-actions.ts
  context-store.ts
```

After the mechanical refactor is green, introduce `DevFlowWorkspaceSession` and the new lifecycle
behavior. `context-store.ts` retains only bounded per-window state:

- active agent identity;
- selected live element;
- last Problems revision;
- last explicit layout report;
- last evidence preview or imported-artifact safe projection;
- last flow-run reference; and
- diagnostic fingerprints mapped to live element/problem/finding identifiers.

It must not retain screenshots, secure values, arbitrary property dictionaries, raw imported
archives, or unredacted logs.

Opaque references issued to Chat or Code Actions are bound to the agent instance and capture
revision, expire when their source diagnostic/context entry is evicted, and fail closed after an
app restart, target change, diagnostic clear, or extension reload.

## Phase 2: `@devflow` Chat Participant

### Contribution

Contribute one participant named `devflow` with explicit commands:

| Command | Behavior |
|---|---|
| `@devflow /inspect` | Resolve or ask the user to select a running app, open/focus its Inspector, and report the target identity. Optional arguments may identify a platform or app. |
| `@devflow /diagnose-selection` | Resolve the current Inspector selection, query Problems for that element, run one explicit scoped layout scan, and explain coverage and limitations. It never mutates the app. |
| `@devflow /explain-problem` | Resolve a diagnostic/problem ID from the prompt, active editor Code Action, or selected element; fetch the current problem record; then explain it with source references and suggested next actions. |
| `@devflow /create-test` | Open the shared Test Workbench at Goal and send the existing bounded test-agent starter request. Saving and running remain separate human approvals. |

### Participant behavior

- Use the participant request model supplied by VS Code; do not create a separate Copilot SDK
  session.
- Stream deterministic target/progress facts before model-authored explanation.
- Put runtime facts in fenced or clearly labelled sections so app-provided text cannot masquerade
  as instructions.
- Treat UI text, logs, network summaries, workflow Markdown, and imported artifacts as untrusted
  data.
- Use command buttons only for registered DevFlow commands with bounded arguments.
- Return precise recovery instructions when no broker, app, selection, source map, or capability is
  available.
- Never infer approval for mutation, test commit/run, evidence screenshot inclusion, source apply,
  or lease takeover from chat text.

### Participant tests

Cover command parsing, target ambiguity, disconnected/restarted agents, stale selections, missing
source maps, Problems unavailable, partial layout coverage, prompt-injection canaries, and the
separate test commit/run approval contract.

## Phase 3: VS Code Language Model tools

Keep the existing selected-element and Data-snapshot tools and add:

| Tool | Mutability | Result |
|---|---|---|
| `maui-devflow_openInspector` | Host UI only | Opens/focuses the Inspector for an exact resolved app and optional element/run/problem reference. |
| `maui-devflow_resolveActiveApp` | Read-only | Returns the exact active app, platform, agent/instance identity, connection state, and available Inspector capabilities. |
| `maui-devflow_getProblems` | Read-only | Returns bounded current Problems, optionally filtered by element or source location. |
| `maui-devflow_getCurrentEvidence` | Read-only | Returns the last explicit evidence preview/import safe projection or retained flow-run reference. It never captures a new screenshot or reads a raw archive. |

Add `maui-devflow_runLayoutDiagnostics` only if evaluation shows that agent mode cannot reliably
discover the existing `maui_layout_diagnostics` MCP tool. Prefer the MCP tool when it is registered.

Every tool schema must support exact target identity. With multiple matching apps, read tools
return candidates and mutation/host-navigation tools require an explicit selection.

Record the evaluation result that decides whether `maui-devflow_runLayoutDiagnostics` is added.
Apply the same recorded gate to any LM tool that duplicates an existing MCP read rather than
exposing VS Code-only host state.

## Phase 4: versioned URI handling

Register a URI handler under the pinned Marketplace extension identifier:

```text
vscode://maui-labs.maui-devflow-inspector/open?v=1&agent=...&element=...&run=...
```

Supported version-1 parameters:

| Parameter | Purpose |
|---|---|
| `agent` | Broker agent ID. |
| `instance` | Optional agent instance ID used to reject stale links. |
| `element` | Optional live element ID to select after the current snapshot resolves. |
| `problem` | Optional Problem ID to focus and publish. |
| `run` | Optional retained flow-run ID to open in Trace/Results. |
| `view` | `inspector`, `problems`, `layout`, `tests`, `trace`, or `evidence`. |

Rules:

- no embed token, broker token, file path, prompt, source text, or evidence payload is accepted;
- each value has a strict length and character bound;
- links resolve only against the locally discovered broker;
- `instance` mismatch produces a stale-link message rather than falling back to another app;
- element selection is verified against the current snapshot revision;
- unknown versions or views fail closed; and
- URI activation never mutates the app or starts a test.

Use these links for diagnostic targets, Code Actions, CLI output, generated offline reports, and
future GitHub artifact summaries.

## Phase 5: VS Code Diagnostics

Create one `DiagnosticCollection` named `maui-devflow`.

### Binding and property Problems

- Refresh only while at least one DevFlow UI surface is active or a participant/tool command
  requests Problems. Prefer an existing agent/broker event capability when it can carry the
  Problems revision. Otherwise use a bounded revision poll with a documented minimum interval,
  exponential backoff on failure, and suspension while the app is disconnected or the extension
  surface is hidden.
- Use the agent's Problems revision to avoid replacing unchanged diagnostics.
- Publish only records whose normalized source path resolves inside the current workspace.
- Map runtime severity conservatively to VS Code severity.
- Preserve the runtime problem code and count in the diagnostic message.
- Use source line/column when available; otherwise attach the finding to the file with a minimal
  range.

### Layout findings

- Publish findings only from the last explicit layout scan.
- Preserve outcome, confidence, coverage, and limitations in the diagnostic message or related
  information.
- Do not publish incomplete checks as passes or errors.
- Clear findings when the target instance or relevant source file identity changes.

### Diagnostic identity

Maintain an in-memory registry keyed by:

```text
agent-instance + source-file + problem/finding-id + source-range + capture-revision
```

The VS Code `Diagnostic.code` target points to the versioned DevFlow URI. Rich live identifiers stay
in the extension registry rather than being serialized into source files or arbitrary command URIs.

## Phase 6: editor Code Actions

Register a Code Action provider for XAML and C# documents with DevFlow source maps.

| Action | Availability | Behavior |
|---|---|---|
| **Inspect live control** | A diagnostic or cursor source range maps to one or more live elements. | Resolve ambiguity with Quick Pick, open the Inspector, and select the exact current element. |
| **Explain with Copilot** | A DevFlow diagnostic is present. | Open Chat with `@devflow /explain-problem` and an opaque in-memory diagnostic reference. |
| **Open selected runtime element** | The active Inspector selection has a source location in this document. | Reveal that source range and retain the reverse link back to the live element. |

Code Actions perform no app mutation or source edit. Any later fix remains normal Copilot/editor work
or the existing separately reviewed DevFlow source-proposal flow.

## Phase 7: structured MCP result migration

The mapped DevFlow MCP tools currently return plain text or image content. Before an MCP App renders
production data, introduce a structured-result path for the mapped text tools:

- preserve byte-for-byte equivalent legacy text content for existing clients;
- add bounded structured content using canonical Problems, layout, tree, flow-run, and evidence
  schemas;
- add UI metadata only after the Phase 0 Apps gate is **go**;
- prove that tool names, arguments, errors, and text fallbacks remain compatible; and
- migrate one read-only tool (`maui_problems`) first before changing the remaining mapped tools.

This is a distinct compatibility migration, not incidental metadata work. Compact Apps cannot depend
on ad-hoc parsing of the legacy text response.

## Phase 8: compact MCP Apps

### App surface

Add one versioned compact app resource:

```text
ui://maui-devflow/compact-view/v1
```

The resource hosts a small shell that renders one of these typed payloads:

- visual-tree fragment;
- Problems list/detail;
- layout summary/findings;
- flow-run summary/steps/failure;
- evidence preview/manifest.

Do not embed the full Inspector initially. Compact views are bounded chat artifacts, not a live
replacement for the Inspector or Canvas.

### Reuse strategy

1. Extract DOM-independent formatters and render models from existing Inspector modules where they
   are already the presentation authority.
2. Define shared JSON schemas or TypeScript contracts beside `@maui-devflow/client`.
3. Have existing C# MCP tools return:
   - legacy text content;
   - bounded structured content; and
   - optional MCP Apps UI metadata.
4. The app calls existing MCP tools for refresh or drill-down. It never calls agent or broker HTTP
   endpoints directly.
5. Mutation buttons are omitted from version 1. A later version may invoke existing mutation tools
   only with normal MCP permission handling and mutation-lease enforcement.

### Tool mapping

| Existing tool/result | Compact view |
|---|---|
| `maui_tree`, `maui_query`, `maui_element` | Visual-tree fragment |
| `maui_problems` | Problems |
| `maui_layout_diagnostics` | Layout findings |
| Retained `maui_flow_replay` report or test-agent trace result | Flow-run result |
| `maui_evidence_preview` | Evidence preview |

No existing tool should lose its text response. Clients without MCP Apps support remain fully
functional.

The compact app reads an already retained flow-run projection. It never invokes
`maui_flow_replay` to obtain or refresh a report because replay drives the live app.

### App security

- strict CSP and no arbitrary remote content;
- no raw HTML from app data;
- all messages, paths, labels, and Markdown treated as text;
- no direct filesystem access;
- no automatic screenshot inclusion;
- bounded arrays, strings, depth, and total serialized bytes;
- app-to-host messages validated against a closed action schema; and
- prompt-injection and hostile-artifact canaries included in tests.

## Phase 9: artifact-first GitHub Copilot integration

### Scope

Create a separate read-only cloud diagnostic path. Do not weaken or repurpose the deterministic
`devflow-failure-publisher` or its restricted `devflow-investigate` workflow.

### Artifact contract

Define `devflow-copilot-diagnostic-v1`, a bounded manifest that references:

- trusted workflow/run/attempt/repository/commit provenance;
- a `flow-run.json` digest and safe run summary;
- optional `.mauitrace` digest and evidence manifest projection;
- evidence format and redaction ruleset versions;
- qualification and evidence-sufficiency state;
- platform and safe failure classification; and
- explicit omissions and trust state.

The manifest does not contain raw prompts, source text, UI text, typed values, branch names, logs,
network bodies, screenshots, tokens, or arbitrary Markdown.

### Cloud reader

Add a read-only MCP profile or companion command that:

1. accepts an explicit repository, workflow run, artifact, and expected digest;
2. downloads through the authenticated GitHub environment supplied to the agent;
3. applies ZIP bomb, traversal, allow-list, UTF-8, schema, size, hash, and provenance checks before
   projection;
4. parses `.mauitrace` and flow-run content through the existing hostile-input readers;
5. labels artifact content as untrusted data;
6. returns only bounded typed projections; and
7. exposes no mutation, issue-write, source-apply, replay, or arbitrary-download tool.

Start with stdio MCP inside the Copilot cloud-agent environment. A hosted remote MCP service is a
later option after its authentication and tenant-isolation design is reviewed.

### GitHub agents and skills

Add a dedicated custom agent and Skill for opt-in artifact diagnosis:

- the agent receives explicit artifact/run references, never searches for an implicit "latest";
- read-only artifact tools are allow-listed;
- it explains evidence sufficiency, trust state, and missing facts before suggesting changes;
- it can hand off to a normal implementation agent with a bounded summary;
- Copilot code review uses only the read-only subset; and
- every conclusion cites the artifact run/digest that supports it.

Before finalizing the manifest, define and test an explicit model-safe projection of
`MauiFlowRunReport`; do not assume every additive report field is suitable for cloud model context.

### Pull request experience

For trusted CI runs, publish a small check summary containing:

- outcome and qualification;
- artifact/run identifiers and digests;
- a link to the workflow artifact;
- a DevFlow URI for local reproduction where applicable; and
- a suggested Copilot prompt that names the explicit artifact.

Do not automatically mention or assign Copilot on untrusted pull requests. Human invocation remains
the admission boundary.

## Cross-cutting security requirements

- Continue using exact agent and agent-instance identity for every live operation.
- Never place broker tokens, embed tokens, grant IDs, approval tokens, or sensitive values in chat,
  diagnostics, URIs, logs, telemetry, or GitHub artifacts.
- Preserve the global mutation lease; Chat Participant, LM tools, Code Actions, and MCP Apps do not
  create alternative mutation authority.
- Chat approval is intent only. Existing test commit/run, source apply, evidence screenshot, and
  repair approval mechanisms remain authoritative.
- Source paths must normalize into the current workspace before editor navigation or diagnostics.
- Imported files and GitHub artifacts remain diagnostic-only until existing trust and local
  reproduction requirements are satisfied.
- Every model-facing projection is bounded, redacted, and explicitly labels application-controlled
  content as data.

## Testing strategy

### TypeScript and VS Code extension

- unit tests for participant command parsing and result formatting;
- fake broker tests for lifecycle, ambiguity, stale instance, and capability degradation;
- URI parser and round-trip tests, including hostile and oversized input;
- diagnostic range, severity, clearing, deduplication, and revision tests;
- Code Action availability and opaque-reference expiry tests;
- LM tool schema and bounded-result tests;
- extension activation tests for commands, participant, tools, URI handler, diagnostics, and Code
  Actions; and
- prompt-injection/secret/path canary tests for every Copilot-facing projection.

### C# and MCP

- typed structured-result compatibility tests that retain legacy text;
- resource and resource-template inventory tests;
- MCP App metadata and unsupported-client fallback tests;
- evidence/flow-run hostile-input regression tests;
- cloud artifact provenance, ZIP, digest, size, and trust-state tests; and
- tool-profile tests proving the cloud/code-review profile is read-only.

### Integration

- VS Code extension against a fake broker and sample source workspace;
- one live Inspector smoke test for each participant command;
- one supported MCP Apps client smoke test plus a text-only MCP client test;
- cloud-agent dry run using a fixed trusted artifact fixture; and
- code-review evaluation proving that findings cite only the supplied artifact.

The cloud evaluation uses a fixed trusted-artifact fixture and fails unless:

- every diagnostic conclusion cites the supplied run ID and digest;
- no source, log, workflow, or artifact content absent from the projection is claimed;
- the MCP inventory contains only the approved read-only tools;
- malformed, untrusted, mismatched, or insufficient artifacts produce no fix recommendation; and
- no GitHub, source, test, replay, issue, or app mutation occurs.

## Delivery sequence

Implement as separate reviewable commits or pull requests:

1. VS Code characterization tests;
2. behavior-preserving VS Code module extraction;
3. shared TypeScript gateway and contract guards;
4. VS Code lifecycle service;
5. Chat Participant;
6. LM tools;
7. URI handler;
8. Diagnostics;
9. Code Actions;
10. MCP Apps compatibility spike and go/no-go decision;
11. structured result migration, beginning with `maui_problems`;
12. compact Problems and layout apps;
13. tree, flow-run, and evidence compact apps;
14. cloud artifact contract, model-safe flow-run projection, and offline verifier;
15. read-only cloud MCP profile;
16. GitHub custom agent, Skill, and trusted CI summary.

Do not combine the cloud trust boundary with the initial VS Code feature pull request.

## Completion criteria

The initiative is complete when:

- a Marketplace-installed VS Code extension can open and diagnose an exact running MAUI app through
  `@devflow`;
- Copilot agent mode can discover host state through bounded LM tools while broad automation remains
  in DevFlow MCP;
- DevFlow Problems and explicit layout findings appear at their mapped source locations and provide
  safe navigation and explanation actions;
- versioned URIs reopen an exact non-stale Inspector context without carrying authority or payloads;
- supported MCP clients render compact views and unsupported clients retain equivalent text;
- a GitHub-hosted Copilot agent can diagnose an explicit trusted DevFlow artifact without localhost
  access or write authority; and
- all paths preserve DevFlow's redaction, evidence, mutation-lease, test approval, source-apply, and
  imported-artifact trust contracts.
