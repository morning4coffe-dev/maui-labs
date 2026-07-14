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
- restricts source-file opening to local paths and asks before leaving the workspace.

## Data DevFlow can expose

Depending on the enabled feature and caller permissions, DevFlow can expose:

- screenshots and the visual tree, including visible text and accessibility metadata;
- logs, network request metadata, preferences, device information, sensors, and app files;
- workflow recordings containing entered values, routes, selectors, and assertions;
- XAML source paths and source text in Debug source maps;
- secure-storage values through explicit CLI/MCP secure-storage tools.

Do not use real credentials, tokens, personal data, or production customer data in preview
recordings and screenshots. Treat saved Markdown workflows and downloaded files as sensitive
artifacts.

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
