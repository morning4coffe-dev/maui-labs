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
```

## What a bundle contains

Format version 1 is a ZIP with a fixed, allow-listed set of entries:

| Entry | Contents |
|---|---|
| `manifest.json` | Schema and redaction ruleset versions, tool/app/platform/capability metadata, per-entry sizes and SHA-256 hashes, counts, limits, exclusions, warnings |
| `environment.json` | App, platform, device, display, agent capabilities, current route |
| `tree.json` | Element **structure**: type, safe automation id, role, bounds, state, child counts, source hash, project-relative or file-name-only source location |
| `problems.json` | Binding/property diagnostics (metadata only, messages re-redacted, paths normalized) |
| `logs.json` | Bounded, scrubbed log entries |
| `network.json` | HTTP **summaries**: method, host, path, query parameter *names*, status, duration, sizes, content types, scrubbed error |
| `screenshot.png` | Only when explicitly opted in |
| `workflow.md` | Only when reproduction steps are attached — scrubbed like every other payload (max 1 MB) |

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
- network summaries are capped (default 100, max 500);
- the visual tree is capped at 5,000 elements and 64 levels deep.

Screenshots are **opt-in everywhere** (`--include-screenshot`, the MCP `includeScreenshot`
parameter, or the checkbox in the Inspector dialog). The manifest always records whether a
screenshot was included and, when it was not, why.

Attached reproduction steps are opt-in too: the CLI and MCP tools only read a file you name
(`--workflow steps.md`), and the Inspector only attaches the loaded workflow when you tick the
second checkbox in the confirmation dialog — because recorded steps quote the text and values you
typed. Whatever is attached is scrubbed for secrets and absolute paths before it enters the bundle,
and the manifest warns that the bundle carries it.

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

`evidence view` never renders or executes anything from the bundle. It parses the trusted entries
and **regenerates** a self-contained static HTML report: every value is HTML-encoded, the document
declares `default-src 'none'; script-src 'none'` (images limited to embedded `data:` URIs), and any
bundled `workflow.md` is rendered as inert, encoded text. Generated reports live in a dedicated
temporary folder and are aged out by TTL and count — only files this tool created are deleted.

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
