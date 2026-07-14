# MAUI DevFlow Inspector for VS Code

Live-inspect and drive a running .NET MAUI app from VS Code. The extension embeds the same
broker-hosted inspector used by the browser and GitHub Copilot Canvas, including the visual tree,
screenshot overlay, property editing, workflow recording, and click-to-XAML navigation.

> **Public preview.** APIs and UX may change and this extension is not covered by the Microsoft
> Support Policy.

## Requirements

1. Install the preview `Microsoft.Maui.Cli` global tool and DevFlow agent packages.
2. Add and start the DevFlow agent in a Debug build of your MAUI app.
3. Launch the app so it registers with the local DevFlow broker.
4. Run **MAUI DevFlow: Open Live Inspector** from the Command Palette.

The extension requires VS Code 1.98 or later. It runs in the workspace extension host so local,
Remote, and WSL workspaces connect to the broker beside the app tooling.

## Copilot and source integration

- **Attach to Copilot** adds the selected MAUI element through the
  `maui-devflow_getSelectedElement` language-model tool.
- **Open source** navigates to generated XAML source locations when Debug source maps are enabled.
- **Record** creates a portable Markdown workflow that can be replayed by DevFlow.

See the [DevFlow inspector documentation](https://github.com/dotnet/maui-labs/blob/main/docs/DevFlow/inspector.md)
and [privacy and security guidance](https://github.com/dotnet/maui-labs/blob/main/docs/DevFlow/privacy-and-security.md).
