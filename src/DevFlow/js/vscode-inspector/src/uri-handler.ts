import * as vscode from "vscode";
import type { DevFlowHostServices } from "./host-services";
import { DEVFLOW_EXTENSION_ID, parseDevFlowUri } from "./uri-contract";

export function registerDevFlowUriHandler(
  context: vscode.ExtensionContext,
  services: DevFlowHostServices,
): void {
  context.subscriptions.push(vscode.window.registerUriHandler({
    handleUri: async (uri) => {
      if (uri.authority && uri.authority !== DEVFLOW_EXTENSION_ID) {
        vscode.window.showWarningMessage("DevFlow rejected a link for another extension.");
        return;
      }
      const target = parseDevFlowUri(uri.path, uri.query);
      if (!target) {
        vscode.window.showWarningMessage("DevFlow rejected an invalid or unsupported Inspector link.");
        return;
      }
      await services.openUriTarget(target);
    },
  }));
}
