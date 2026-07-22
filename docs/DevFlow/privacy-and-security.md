# DevFlow Privacy and Security

> **Public preview.** DevFlow is developer tooling, not a production remote-control service. Use it
> only with apps and data you are authorized to inspect.

## Local communication

The broker, web inspector, VS Code host, Canvas host, CLI, and in-app agent communicate over
loopback HTTP/WebSocket endpoints. DevFlow does not intentionally upload app data or telemetry to a
Microsoft service. A host can still pass selected context to its own AI service when the user
explicitly chooses **Attach to Copilot** or invokes an AI tool.

Loopback is a transport boundary, not user authentication. Other processes running as the same
desktop user may be able to connect. DevFlow therefore:

- validates loopback origins and host headers;
- uses per-instance tokens for inspector data and embedded host bridges;
- requires a global mutation lease before state-changing operations;
- blocks cross-origin simple POSTs on control/file-writing bridges;
- restricts source-file opening to local paths and asks before leaving the workspace;
- requires an explicit Inspector action before writing a direct-literal property to XAML, verifies
  the build-time source hash, and limits writes to files under the registered app project;
- asks for confirmation before executing arbitrary JavaScript in a live WebView;
- targets desktop native-alert actions by a freshly resolved process ID and Android actions by the
  unique device owning the selected agent port's ADB forward, refusing ambiguous targets.

Source writes use an atomic replacement with raw-byte conflict checks. If a concurrent save cannot
be verified or restored safely, DevFlow keeps a uniquely named `.bak` recovery copy beside the
XAML file and reports its path instead of deleting the uncertain version.

## Data DevFlow can expose

Depending on the enabled feature and caller permissions, DevFlow can expose:

- screenshots and the visual tree, including visible text and accessibility metadata;
- logs, network request metadata, preferences, device information, sensors, and app files;
- native alert titles, button labels, and button bounds reported by platform accessibility tooling;
- WebView page source and JavaScript evaluation results when explicitly requested;
- workflow recordings containing entered values, routes, selectors, and assertions;
- XAML source paths and source text in Debug source maps;
- secure-storage values through explicit CLI/MCP secure-storage tools.

Do not use real credentials, tokens, personal data, or production customer data in preview
recordings and screenshots. Treat saved Markdown workflows and downloaded files as sensitive
artifacts.

The Inspector Data paperclip is an explicit user action. It sends a bounded, point-in-time snapshot
of the current Logs, Network, Preferences, Device, Sensors, Files, or Alerts view to the active AI host.
Snapshots apply structural secret redaction, exclude secure storage, geolocation, WebView content,
network bodies, and file contents, and include the originating agent port so DevFlow MCP tools can
retrieve fresher or deeper data. Redaction is defense-in-depth, not a guarantee; inspect test data
before sharing it with an AI service.

## Build metadata and source maps

The agent embeds only the project filename by default. Its session identity is derived from a
sanitized one-way project-path value; the full path is not stored. Full-path project metadata is an
explicit local-debug opt-in:

```bash
dotnet build -p:MauiDevFlowIncludeProjectPath=true
```

Click-to-XAML source maps are enabled by default only for `Debug`. They embed XAML text and the
developer-machine source path in the app assembly. Set the following for any build that may be
shared or distributed:

```xml
<DevFlowXamlSourceMapsEnabled>false</DevFlowXamlSourceMapsEnabled>
```

## Production guidance

- Register the DevFlow agent only in Debug/developer builds.
- Do not expose agent or broker ports beyond loopback.
- Remove DevFlow packages and generated recordings from production release artifacts.
- Upgrade the agent, CLI, VS Code extension, and Canvas extension together during preview.
- Stop the broker and close host panels when inspection is complete.

Report security issues using the repository's
[security policy](https://github.com/dotnet/maui-labs/security/policy), not a public issue.
