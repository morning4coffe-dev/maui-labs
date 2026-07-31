# Evidence bundles (`.mauitrace`)

`maui devflow evidence` captures a small, redacted, shareable snapshot of a running app so a bug
report carries facts instead of screenshots and prose. A bundle is an **on-demand static artifact** —
DevFlow never records continuously and never uploads anything.

```bash
# See exactly what would be shared — nothing is written
maui devflow evidence preview

# Write the bundle (defaults to ./maui-traces/<app>-<timestamp>.mauitrace)
maui devflow evidence capture

# Validate a bundle and open a generated offline report
maui devflow evidence view ./maui-traces/MyApp-20260729-112233.mauitrace

# Choose the report path; existing reports require explicit replacement
maui devflow evidence view trace.mauitrace --output-report report.html --overwrite
```

## What a bundle contains

Format version 1 is a ZIP with a fixed, allow-listed set of entries:

| Entry | Contents |
|---|---|
| `manifest.json` | Schema and redaction ruleset versions, tool/app/platform/capability metadata, per-entry sizes and SHA-256 hashes, counts, limits, exclusions, warnings |
| `environment.json` | App, platform, device, display, agent capabilities, current route |
| `tree.json` | Element **structure**: type, safe automation id, role, bounds, state, child counts, source hash, project-relative or file-name-only source location |
| `layout.json` | Layout diagnostics: rule outcomes, per-rule coverage, and element identity/geometry for each finding — included only when the connected agent supports `diagnostics.layout` |
| `problems.json` | Binding/property diagnostics (metadata only, messages re-redacted, paths normalized) |
| `logs.json` | Bounded, scrubbed log entries |
| `network.json` | HTTP **summaries**: method, host, path, query parameter *names*, status, duration, sizes, content types, scrubbed error |
| `screenshot.png` | Only when explicitly opted in |
| `workflow.md` | Only when reproduction steps are attached — scrubbed like every other payload (max 1 MB) |

No new ZIP entry is added for flow replay. When a replay captures failure evidence, the existing
`manifest.json` may include an additive `flowRun` object with the run ID, failed step ID, typed
failure code, report digest, local `flow-run.json` path/reference, and capture-completeness state.
This links the redacted `.mauitrace` v1 bundle back to its bounded structured report without
changing the v1 allow-list. A missing path means the host retained the report in memory only; the
run ID and digest still identify the report returned by CLI, MCP, Inspector, or broker status.

## Privacy contract

Redaction happens at ingestion, before anything is serialized, so every surface (CLI, MCP, Web
Inspector) shares one ruleset — `EvidenceRedaction`, versioned as `redactionVersion` in the
manifest and in every preview.

Never captured:

- element `Text`/`Value` content, native and framework property dictionaries;
- `BindingContext` or view-model object graphs;
- preferences and secure-storage values;
- geolocation;
- file contents from app storage;
- absolute user/machine paths (replaced by project-relative paths or file names);
- HTTP headers, bodies, and query-string values.

Bounded and scrubbed:

- logs are capped (default 200, max 500) and each message is secret-masked, path-stripped and
  truncated;
- redaction ruleset v2 recognizes keyed secrets plus common standalone credential shapes such as
  `sk-`/Slack/GitHub/GitLab/Google/AWS tokens, basic-auth URLs, and PEM private-key blocks;
- network summaries are capped (default 100, max 500);
- the visual tree is capped at 5,000 elements and 64 levels deep;
- the bundled layout scan examines at most 2,000 elements and retains at most 500 findings; each
  finding's message, explanation, and limitations are scrubbed and truncated, and source paths are
  made project-relative. Findings carry element identity and geometry only — never text or values.
  An agent without `diagnostics.layout` produces an explicit exclusion instead of an empty entry.

Screenshots are **opt-in everywhere** (`--include-screenshot`, the MCP `includeScreenshot`
parameter, or the checkbox in the Inspector dialog). The manifest always records whether a
screenshot was included and, when it was not, why.

Attached reproduction steps are opt-in too: the CLI and MCP tools only read a file you name
(`--workflow steps.md`), and the Inspector only attaches the loaded workflow when you tick the
second checkbox in the confirmation dialog — because recorded steps quote the text and values you
typed. Whatever is attached is scrubbed for secrets and absolute paths before it enters the bundle,
and the manifest warns that the bundle carries it.

## Flow replay report linkage

`MauiFlowRunner` emits `flow-run-report-v1` for every pass, divergence, cancellation, timeout,
lease loss, infrastructure failure, and unknown command completion. The broker retains that report
with terminal run status and writes `<artifact-root>/<runId>/flow-run.json` atomically when an
artifact root is configured. `maui devflow flow replay`, MCP replay, and Inspector replay preserve
their legacy `FlowReplayReport` fields while adding optional `report`, `reportPath`, and
`reportDigest` fields.

The report records redacted selector/value descriptors, actionability polls, command receipts,
assertion disclosure state, typed failure classification, artifact references, and additive
side-effect admission facts: policy, reset/seed evidence, expected and observed preconditions,
compensator outcome, independent business-oracle results, and explicit replay/repair eligibility
reasons. A run is not marked verified unless its required independent oracle succeeds. These are
identity-only facts; reset strategies, evidence references, seed identities, and messages are
scrubbed with the same report redactor and never execute a reset or compensator.

Legacy manual schema-2 flow replay remains usable. Its report makes the absence of safety evidence
explicit with `sideEffectPolicy: "unspecified"` and `repairEligibility: false`; it does not claim a
repair-verified clean state. The report intentionally does not embed a `.mauitrace`, raw secrets,
or typed sensitive values. Evidence capture is failure-only and metadata/tree/log oriented by
default; screenshots remain opt-in.

## Preview before you share

Every surface shows the same plan object before a bundle is produced:

- the entries that will be included, with item counts;
- the entries that will be excluded, with reasons;
- the data classes that are never captured;
- the applied limits, screenshot status, redaction ruleset version, and warnings;
- the destination path (CLI/MCP) or suggested download name (Inspector).

In the Web Inspector, the **Evidence** toolbar action fetches the plan and shows it in an accessible
confirmation dialog. The bundle is only fetched and downloaded after that confirmation, and the
screenshot checkbox starts unchecked every time.

## Writing is atomic

A capture writes to a uniquely named temporary file beside the destination, validates the finished
archive with the same hostile-input reader used for imports, and only then moves it into place.
Overwriting requires `--overwrite`; without it an existing file is left untouched.

## Reading is defensive

Bundles are treated as hostile input. Before any content is decompressed the reader checks:

- entry names against the allow-list (flat names only — no traversal, nesting, or rooted paths);
- duplicate entries;
- entry count, per-entry size, total uncompressed size, and per-entry compression ratio;
- the manifest schema id, format version, and JSON shape.

Each allow-listed entry retains its capture-side limit on import (for example, workflow Markdown is
at most 1 MB and screenshots at most 16 MB). Parsed JSON sections are also semantically bounded
before rendering: null required collections, oversized arrays/strings, non-finite geometry, or
trees beyond the capture depth/count are ignored with a warning rather than reaching the report
renderer.

`evidence view` never renders or executes anything from the bundle. It parses the trusted entries
and **regenerates** a self-contained static HTML report: every value is HTML-encoded, the document
declares `default-src 'none'; script-src 'none'` (images limited to embedded `data:` URIs), and any
bundled `workflow.md` is rendered as inert, encoded text. Terminal control and Unicode format
characters are removed before rendering. Explicit report paths must use `.html`/`.htm`, reject
Windows device/alternate-stream names, and never overwrite unless `--overwrite` is supplied.
Generated reports live in a dedicated temporary folder and are aged out by TTL and count — only
files this tool created are deleted.

## MCP tools

| Tool | Purpose |
|---|---|
| `maui_evidence_preview` | Return the plan for a capture without writing anything |
| `maui_evidence_capture` | Write a bundle atomically and return its path plus the manifest summary |

## Implementation

| Area | Location |
|---|---|
| Format, models, redaction, paths | `src/Cli/Microsoft.Maui.Cli/DevFlow/Evidence/` |
| CLI commands | `src/Cli/Microsoft.Maui.Cli/DevFlow/Evidence/EvidenceCommands.cs` |
| MCP tools | `src/Cli/Microsoft.Maui.Cli/DevFlow/Mcp/Tools/EvidenceTools.cs` |
| Inspector routes | `src/Cli/Microsoft.Maui.Cli/DevFlow/Inspector/InspectorServer.cs` (`/api/evidence/*`) |
| Inspector UI | `src/Cli/Microsoft.Maui.Cli/DevFlow/Inspector/Web/inspector-evidence.js` |
| Tests | `src/DevFlow/Microsoft.Maui.DevFlow.Tests/Evidence*Tests.cs`, `src/Cli/Microsoft.Maui.Cli.UnitTests/EvidenceCliTests.cs`, `src/DevFlow/js/test/inspector-evidence.test.mjs` |
