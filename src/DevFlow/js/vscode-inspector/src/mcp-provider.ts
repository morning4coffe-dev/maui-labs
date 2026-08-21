import * as vscode from "vscode";

const PROVIDER_ID = "mauiDevflow.localMcp";

export function registerDevFlowMcpProvider(context: vscode.ExtensionContext): void {
  context.subscriptions.push(vscode.lm.registerMcpServerDefinitionProvider(PROVIDER_ID, {
    provideMcpServerDefinitions: async () => {
      const { resolveMauiCli } = await import("@maui-devflow/client");
      return [
        new vscode.McpStdioServerDefinition(
          "MAUI DevFlow (full local automation)",
          resolveMauiCli(),
          ["devflow", "mcp", "--profile", "full"],
          {},
          "1",
        ),
      ];
    },
  }));
}
