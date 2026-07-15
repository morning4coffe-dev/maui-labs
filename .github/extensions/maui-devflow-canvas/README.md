# MAUI DevFlow Canvas (GitHub Copilot Canvas extension)

A **live, interactive view of a running .NET MAUI app** inside a GitHub Copilot canvas: the
app's real screenshot with a visual-tree overlay and an editable property grid. Both the
human and Copilot inspect, select, edit, and **drive the same running app** — backed by the
DevFlow broker + in-app agent, through the shared [`@maui-devflow/client`](../../../src/DevFlow/js/devflow-client).

This is the project-scoped (auto-discovered, committed) home for the extension, per GitHub's
`.github/extensions/<name>/` convention. It replaces the earlier user-scoped PoC at
`~/.copilot/extensions/maui-live-canvas`.

> **Status: public preview.** APIs and UX may change and this extension is not covered by the
> Microsoft Support Policy.

## Shared inspector architecture

The Canvas embeds the broker-hosted inspector used by the browser and VS Code. Its extension host
adds selected-element and redacted Data-snapshot context attachment, project-local workflow
persistence, and agent-callable actions.
Transport and discovery use `@maui-devflow/client`.

An upstream fidelity fix rode along: the client's `getProperty` now mirrors the historical
`value ?? result ?? d[name] ?? raw` fallback order.

## Architecture

```
Copilot Canvas ──► extension.mjs ──► broker-hosted shared inspector
       │                  │
       │                  ├──► LiveStore / DevflowDevice ──► @maui-devflow/client
       │                  └──► authenticated localhost host bridge
       └── agent actions ───────────────────────────────► broker + in-app agent
```

- **`extension.mjs`** — `createCanvas(...)` with ~29 agent-callable capabilities + a loopback
  server that serves the panel; `joinSession(...)` at the bottom.
- **`store.mjs`** (`LiveStore`) — fallback live model and agent-action state.
- **`devflow.mjs`** (`DevflowDevice`) — **rewritten**: the adapter over `@maui-devflow/client`.
- **`shell.mjs`** — embeds the shared broker-hosted inspector in an iframe (`renderShell`) and
  renders the lightweight disconnected/loading status shell (`renderDisconnected`) shown while no
  broker/agent has resolved yet; both share the hybrid `--df-*` theme-token language with the VS
  Code host shell.
- **`ui.mjs`** — legacy, hand-rendered panel HTML (screenshot + overlay + property grid). Kept on
  disk for reference/rollback only — `renderDisconnected` in `shell.mjs` is the runtime fallback now.
- **`recorder.mjs` / `replay.mjs`** — workflow persistence and replay. Active recording is owned
  by the broker and observes successful mutations from every DevFlow host.
- **`selftest*.mjs`, `clean-open-canvases.mjs`** — bridge smoke test + offline proof + maintenance.

### File map (relocated PoC vs. changed)

| File | Fate in M2 |
|---|---|
| `devflow.mjs` | **Rewritten** — thin adapter over `@maui-devflow/client` (was 1020 lines of transport) |
| `store.mjs`, `ui.mjs`, `extension.mjs`, `recorder.mjs`, `replay.mjs`, `selftest*.mjs`, `clean-open-canvases.mjs`, `MAUI-TEST-AGENT.md` | Relocated verbatim (import `./devflow.mjs` unchanged) |
| `package.json` | **New** — depends on `@github/copilot-sdk` + the workspace `@maui-devflow/client` (`file:` dep) |
| `test/device.test.mjs` | **New** — drop-in contract tests for the adapter against a fake agent |

## Migration from the old user extension

The repo-scoped extension replaces `~/.copilot/extensions/maui-live-canvas`. Remove or rename that
old directory before opening this repository; otherwise Copilot can discover two extensions with
overlapping capabilities.

```powershell
Remove-Item -Recurse -Force "$HOME\.copilot\extensions\maui-live-canvas"
```

```bash
rm -rf ~/.copilot/extensions/maui-live-canvas
```

Then reopen the repository so Copilot discovers
`.github/extensions/maui-devflow-canvas`.

## Install / test / run

```bash
# 1) Build the shared client first (the file: dependency packs its dist/).
cd ../../../src/DevFlow/js && npm ci && npm run build -w @maui-devflow/client

# 2) Install + test the extension.
cd ../../../.github/extensions/maui-devflow-canvas
npm ci
npm test                 # adapter contract tests (offline, fake agent)
npm run selftest:recorder  # offline recorder/replay proof

# Online bridge smoke test (needs a running MAUI app with the DevFlow agent):
npm run selftest
```

`npm start` (`node extension.mjs`) is launched by the Copilot canvas host — it calls
`joinSession()` and won't run standalone.

## Capabilities (stable names, unchanged from the PoC)

`get_canvas`, `refresh`, `list_agents`, `select_agent`, `get_tree`, `get_element`, `query`,
`hit_test`, `select_element`, `get_selection`, `attach_selection`, `get_property`,
`set_property`, `apply_and_verify`, `tap`, `fill`, `scroll`, `navigate`, `back`, `resize`,
`set_theme`, `screenshot`, `get_logs`, `start_recording`, `get_recording`,
`stop_and_save_test`, `save_test`, `list_tests`, `replay_test`.

Kept identical so existing prompts (e.g. "Open the MAUI Live Canvas") and `MAUI-TEST-AGENT.md`
keep working.

## Coordination and safety

- The Canvas uses the same global mutation lease as the browser, VS Code, MCP, and CLI.
- Closing the Canvas releases its lease and disposes the shared client.
- The localhost bridge requires JSON plus a per-instance nonce for all control and file writes.
- Replays are blocked while a workflow recording is active.

See [privacy and security](../../../docs/DevFlow/privacy-and-security.md) before using DevFlow with
sensitive app data.

## Requirements

- Node ≥ 18, `@github/copilot-sdk` ≥ 1, and a built `@maui-devflow/client`.
- A running .NET MAUI app with the DevFlow agent, discoverable via the DevFlow broker
  (`maui devflow` / `~/.mauidevflow/broker.json`). The adapter auto-starts the broker
  (`bootstrapBroker: "once"`).
