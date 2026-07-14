# DevFlow Live Inspector

> **Public preview.** APIs and UX may change and this product is not covered by the Microsoft
> Support Policy.

The DevFlow Live Inspector mirrors a running .NET MAUI app as a screenshot plus an interactive
visual-tree overlay. The same inspector is used in:

- a browser at `http://localhost:19223/inspector/`;
- the MAUI DevFlow Inspector VS Code extension;
- the repo-scoped GitHub Copilot Canvas extension.

## Start the inspector

Add and start the DevFlow agent in a Debug build, launch the app, then:

```bash
maui devflow broker start
```

Open the broker URL above, run **MAUI DevFlow: Open Live Inspector** in VS Code, or open the MAUI
Live Canvas in GitHub Copilot.

## Features

- screenshot and visual-tree inspection with hover, selection, and searchable hierarchy;
- tap, fill, scroll, gesture, navigation, theme, and live property mutation;
- logs, network, preferences, device, sensor, file, and WebView/CDP data docks;
- click-to-XAML source navigation for Debug source maps;
- broker-owned workflow recording and replayable Markdown output;
- selected-element context attachment to GitHub Copilot;
- responsive light, dark, and high-contrast host theming.

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

## Atomic frames and coordinates

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

The lease is coordination, not authentication. See
[Privacy and Security](privacy-and-security.md).

## Workflow recording

Recording is owned by the broker and scoped to the current app. The current valid mutation lease
holder controls it. The agent observes successful supported mutations from every host, so a
workflow can begin in the browser, continue through Canvas or MCP after lease handoff, and stop in
VS Code without separate local recorders.

Currently normalized actions include tap, fill, scroll, navigate, back, theme changes, and property
changes. The result is a Markdown file with an authoritative `json maui-test` block for replay.
Replay is blocked while recording is active.

## Click-to-XAML

The Agent.Core package supplies a build-transitive source generator. In Debug builds it maps XAML
elements to source file, line, column, and a build-time content hash. VS Code compares the current
file hash before opening the recorded line and warns when the file changed after the app was built.

Source maps are disabled outside Debug by default because they embed XAML text and source paths.
They can be disabled explicitly:

```xml
<DevFlowXamlSourceMapsEnabled>false</DevFlowXamlSourceMapsEnabled>
```

## Platform boundaries

Android system dialogs and MAUI alerts are outside the in-app MAUI visual tree, so browser, VS Code,
and Canvas inspection cannot select their native buttons. Use the platform alert driver instead:

```bash
maui devflow ui alert detect --device emulator-5554
maui devflow ui alert dismiss "OK" --device emulator-5554
```

DevFlow Action request bodies are passed to the registered action. A generic
`include: ["screenshot", "tree"]` field does not add post-action captures; use the CLI/MCP
post-action options or request the screenshot and tree explicitly.

## Compatibility

Current clients remain read/write compatible with older agents that do not expose the lease
endpoint. Older clients cannot mutate current agents because they do not send a lease identity.
During public preview, upgrade the agent packages and all host tooling together.

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

See [Public Preview Release](public-preview-release.md) for CI and promotion gates.
