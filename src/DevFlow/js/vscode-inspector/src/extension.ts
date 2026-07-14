import * as vscode from "vscode";
import * as path from "path";
import { createHash, randomBytes } from "crypto";
import type { AgentRegistration } from "@maui-devflow/client";

/**
 * MAUI DevFlow Inspector — VS Code host shell (m4 + feature D).
 *
 * A thin shell over the SHARED DevFlow inspector: it discovers the broker via the shared
 * `@maui-devflow/client`, then opens a webview that embeds the broker-hosted inspector for a
 * running app (`http://localhost:{brokerPort}/inspector/{agentId}/`) — the same inspector the
 * browser and Copilot Canvas use, including the m6 rich property grid, the visual tree, and
 * record/replay. No UI is re-implemented here; the host contributes only the "juice" the shared
 * inspector can't do on its own: relaying Send-to-Copilot into Chat, opening a XAML source file,
 * and saving a recorded test. That contract is the authenticated host bridge (feature D).
 */
export function activate(context: vscode.ExtensionContext): void {
  context.subscriptions.push(
    vscode.commands.registerCommand("mauiDevflow.openInspector", () => openInspector())
  );
  registerSelectionTool(context);
}

// The element the human currently has selected in the inspector (fed by devflow:selectionChanged over
// the host bridge). Powers both the one-shot Send-to-Copilot query and the language-model tool below.
type SelectedElement = { id?: string | null; type?: string; automationId?: string | null; text?: string | null; hasSource?: boolean };
let currentSelection: SelectedElement | null = null;

function registerSelectionTool(context: vscode.ExtensionContext): void {
  // A Language Model Tool lets Copilot (agent mode) resolve "the selected element" / "fix the selected
  // item" to the control the human highlighted in the inspector — the VS Code equivalent of the canvas
  // get_selection action. Typed via `any` so we don't require a newer @types/vscode; the runtime API
  // is present in VS Code 1.95+ and simply no-ops on older builds.
  const lm: any = (vscode as any).lm;
  if (!lm || typeof lm.registerTool !== "function") return;
  try {
    context.subscriptions.push(
      lm.registerTool("maui-devflow_getSelectedElement", {
        invoke: async () => {
          const ToolResult = (vscode as any).LanguageModelToolResult;
          const TextPart = (vscode as any).LanguageModelTextPart;
          const text = currentSelection
            ? "The user has this .NET MAUI element selected in the DevFlow inspector:\n" + JSON.stringify(currentSelection, null, 2)
            : "No element is currently selected in the DevFlow inspector. Ask the user to click an element in the inspector (or open it via 'MAUI DevFlow: Open Live Inspector').";
          return new ToolResult([new TextPart(text)]);
        },
      })
    );
  } catch {
    // Tool API unavailable — the one-shot Send-to-Copilot button still carries the element context.
  }
}

async function openInspector(): Promise<void> {
  // The client is ESM; this extension is CommonJS — load it via a dynamic import.
  const { discoverBroker, readBrokerState } = await import("@maui-devflow/client");

  const configured = vscode.workspace.getConfiguration("mauiDevflow").get<number>("brokerPort");
  const brokerPort = typeof configured === "number" && configured > 0 ? configured : undefined;

  const discovery = await discoverBroker({ bootstrap: "never", brokerPort });
  if (!discovery || discovery.agents.length === 0) {
    vscode.window.showWarningMessage(
      "MAUI DevFlow: no running app found. Launch your app with the DevFlow agent, then run this command again."
    );
    return;
  }

  const agent = await pickAgent(discovery.agents);
  if (!agent) return;

  // The embed token (m7) proves this is a trusted local shell so the inspector relaxes its
  // anti-framing headers; it lives in the local broker.json. Only use it when that state file
  // actually describes the broker we discovered, so a stale/foreign broker.json can't pair a wrong
  // token with the configured port.
  const state = readBrokerState();
  const embedToken = state && state.port === discovery.port ? state.embedToken ?? undefined : undefined;
  // The broker's HttpListener is bound to `localhost`, so the iframe host MUST be localhost
  // (a 127.0.0.1 Host header is rejected as "Invalid Hostname").
  const base = `http://localhost:${discovery.port}/inspector/${encodeURIComponent(agent.id)}/`;
  const inspectorUrl = embedToken ? `${base}?embed=${encodeURIComponent(embedToken)}` : base;
  const title = agent.appName ?? agent.id;

  const panel = vscode.window.createWebviewPanel(
    "mauiDevflowInspector",
    `DevFlow · ${title}`,
    vscode.ViewColumn.Beside,
    {
      enableScripts: true,
      retainContextWhenHidden: true,
      // A VS Code webview will NOT load a localhost server in an iframe without a port mapping,
      // even on local desktop. Map the broker port through so the iframe's http://localhost:{port}
      // resolves to the extension-host's localhost:{port} (the broker). Also covers Remote/WSL.
      portMapping: [{ webviewPort: discovery.port, extensionHostPort: discovery.port }],
    }
  );

  // Per-embed secrets: `nonce` gates the one inline relay script (strict CSP, no unsafe-inline);
  // `bridgeId` authenticates every postMessage on the host bridge. The bridgeId travels in the URL
  // *fragment*, so it never reaches the broker over HTTP — only the iframe's own script reads it.
  const nonce = randomToken();
  const bridgeId = randomToken();

  // Register the message handler BEFORE the webview HTML loads so no early bridge message is lost.
  panel.webview.onDidReceiveMessage((msg) => handleBridgeMessage(msg));
  panel.webview.html = renderHost(inspectorUrl, title, nonce, bridgeId);
}

// ── Host bridge handlers (the shared inspector calls these via postMessage → relay → extension) ──

interface BridgeMessage {
  type?: string;
  payload?: CopilotPayload;
  file?: string;
  line?: number;
  column?: number;
  sourceHash?: string;
  name?: string;
  markdown?: string;
  element?: SelectedElement | null;
}

interface CopilotPayload {
  element?: { type?: string; automationId?: string | null; text?: string | null; id?: string | null } | null;
  markdown?: string | null;
  markdownTruncated?: boolean;
  appName?: string | null;
}

async function handleBridgeMessage(msg: BridgeMessage | undefined): Promise<void> {
  if (!msg || typeof msg.type !== "string") return;
  switch (msg.type) {
    case "devflow:sendToCopilot":
      await sendToCopilot(msg.payload);
      break;
    case "devflow:openSource":
      await openSource(msg.file, msg.line, msg.column, msg.sourceHash);
      break;
    case "devflow:recordingComplete":
      await saveRecording(msg.name, msg.markdown);
      break;
    case "devflow:selectionChanged":
      currentSelection = msg.element ?? null;
      break;
  }
}

async function sendToCopilot(payload: CopilotPayload | undefined): Promise<void> {
  const el = payload?.element ?? currentSelection;
  if (el) currentSelection = el; // keep the language-model tool in sync with what we're attaching

  // ATTACH the selection as context — do NOT submit a message. VS Code 1.98+ supports `toolIds`,
  // which attaches a real tool context chip without relying on #mention parsing during a cold Chat
  // view startup. Older supported versions fall back to the textual #mauiSelection reference.
  const openChat = async (query: string, partial: boolean) =>
    vscode.commands.executeCommand("workbench.action.chat.open", { query, isPartialQuery: partial });

  if (vscodeAtLeast(1, 98)) {
    try {
      // Warm the Chat widget first. The partial-query branch in VS Code does not wait for a newly
      // created widget's view model, while the second call can attach `toolIds` deterministically.
      await vscode.commands.executeCommand("workbench.action.chat.open");
      await new Promise((resolve) => setTimeout(resolve, 50));
      await vscode.commands.executeCommand("workbench.action.chat.open", {
        query: "",
        isPartialQuery: true,
        toolIds: ["maui-devflow_getSelectedElement"],
      });
      return;
    } catch {
      // Fall through to the text-reference compatibility path.
    }
  }

  try {
    await openChat("#mauiSelection ", true);
    return;
  } catch {
    // Older VS Code without the tool/partial-query support — fall back to a descriptive, still-unsent
    // context string, then to the clipboard as a last resort.
  }

  function vscodeAtLeast(major: number, minor: number): boolean {
    const match = /^(\d+)\.(\d+)/.exec(vscode.version);
    if (!match) return false;
    const currentMajor = Number(match[1]);
    const currentMinor = Number(match[2]);
    return currentMajor > major || (currentMajor === major && currentMinor >= minor);
  }

  const bits: string[] = [];
  if (el) {
    const parts = [el.type ?? "Element"];
    if (el.automationId) parts.push(`AutomationId="${el.automationId}"`);
    if (el.text) parts.push(`text="${el.text}"`);
    bits.push(`Selected .NET MAUI element: ${parts.join(" ")}.`);
  }
  if (payload?.markdown) bits.push("\nRecorded steps:\n" + payload.markdown + (payload.markdownTruncated ? "\n…(truncated)" : ""));
  const context = bits.join("\n") || "No element is selected in the DevFlow inspector.";
  try {
    await openChat(context, true);
  } catch {
    await vscode.env.clipboard.writeText(context);
    vscode.window.showInformationMessage("DevFlow: Copilot Chat unavailable — selection context copied to the clipboard.");
  }
}

async function openSource(
  file: string | undefined,
  line: number | undefined,
  column: number | undefined,
  sourceHash: string | undefined,
): Promise<void> {
  if (typeof file !== "string" || !file.trim()) return;
  const raw = file.trim();

  // Resolve file: URIs first so `file:///C:/…` works; reject any OTHER URL scheme (http:, vscode:, …)
  // but never mistake a Windows drive letter ("C:\") for a scheme. This is a hard security gate —
  // the path arrives over a postMessage bridge.
  let fsPath: string;
  const isDriveLetter = /^[a-zA-Z]:[\\/]/.test(raw);
  if (/^file:/i.test(raw)) {
    try {
      fsPath = vscode.Uri.parse(raw).fsPath;
    } catch {
      return;
    }
  } else if (/^[a-zA-Z][a-zA-Z0-9+.-]*:/.test(raw) && !isDriveLetter) {
    vscode.window.showWarningMessage("DevFlow: refusing to open a non-local source path.");
    return;
  } else {
    fsPath = raw;
  }

  // Hard-block UNC and Windows device namespaces in BOTH slash forms (\\server, //server, \\?\, //./)
  // on the RESOLVED path — these can reach network shares (NTLM credential exposure) or raw devices.
  if (/^[\\/]{2}/.test(fsPath)) {
    vscode.window.showWarningMessage("DevFlow: refusing to open a UNC/network source path.");
    return;
  }
  if (!path.isAbsolute(fsPath)) {
    vscode.window.showWarningMessage("DevFlow: source path is not absolute.");
    return;
  }

  const folders = vscode.workspace.workspaceFolders ?? [];
  const inside = folders.some((f) => isInside(f.uri.fsPath, fsPath));
  if (!inside) {
    const pick = await vscode.window.showWarningMessage(
      `Open a source file outside your workspace?\n${fsPath}`,
      { modal: true },
      "Open"
    );
    if (pick !== "Open") return;
  }

  const ln = Math.max(0, (Number(line) || 1) - 1);
  const col = Math.max(0, (Number(column) || 1) - 1);
  try {
    const doc = await vscode.workspace.openTextDocument(vscode.Uri.file(fsPath));
    if (sourceHash) {
      const currentHash = createHash("sha256").update(doc.getText(), "utf8").digest("hex").slice(0, 16);
      if (currentHash !== sourceHash.toLowerCase()) {
        const choice = await vscode.window.showWarningMessage(
          "DevFlow: this XAML file changed after the running app was built, so the recorded source line may be stale.",
          { modal: true },
          "Open Anyway",
        );
        if (choice !== "Open Anyway") return;
      }
    }
    const editor = await vscode.window.showTextDocument(doc, { preview: true });
    const pos = new vscode.Position(ln, col);
    editor.selection = new vscode.Selection(pos, pos);
    editor.revealRange(new vscode.Range(pos, pos), vscode.TextEditorRevealType.InCenter);
  } catch {
    vscode.window.showErrorMessage(`DevFlow: could not open ${fsPath}`);
  }
}

async function saveRecording(name: string | undefined, markdown: string | undefined): Promise<void> {
  if (typeof markdown !== "string" || markdown.length === 0) return;
  const safe = (typeof name === "string" && name ? name : "recording").replace(/[^A-Za-z0-9_.-]/g, "_");
  const folders = vscode.workspace.workspaceFolders ?? [];
  const defaultUri = folders.length
    ? vscode.Uri.joinPath(folders[0].uri, `${safe}.md`)
    : vscode.Uri.file(`${safe}.md`);
  const target = await vscode.window.showSaveDialog({
    defaultUri,
    filters: { Markdown: ["md"] },
    title: "Save DevFlow recording",
  });
  if (!target) return;
  try {
    await vscode.workspace.fs.writeFile(target, Buffer.from(markdown, "utf8"));
    const doc = await vscode.workspace.openTextDocument(target);
    await vscode.window.showTextDocument(doc, { preview: false });
    vscode.window.showInformationMessage(`DevFlow recording saved: ${target.fsPath}`);
  } catch {
    vscode.window.showErrorMessage("DevFlow: could not save the recording.");
  }
}

function isInside(root: string, target: string): boolean {
  const rel = path.relative(root, target);
  return rel.length > 0 && !rel.startsWith("..") && !path.isAbsolute(rel);
}

async function pickAgent(agents: AgentRegistration[]): Promise<AgentRegistration | undefined> {
  if (agents.length === 1) return agents[0];
  const pick = await vscode.window.showQuickPick(
    agents.map((a) => ({
      label: a.appName ?? a.id,
      description: `${a.platform ?? "?"} · port ${a.port}`,
      agent: a,
    })),
    { placeHolder: "Select a running MAUI app to inspect" }
  );
  return pick?.agent;
}

function renderHost(inspectorUrl: string, title: string, nonce: string, bridgeId: string): string {
  // The shared inspector runs on localhost; embed it in an iframe. On desktop the iframe keeps its
  // http://localhost origin, but in Remote/WSL/web VS Code serves it through a
  // `https://<port>-<uuid>.vscode-webview.net` proxy origin, so the webview CSP frame-src allows
  // both. The single relay <script> is the only script and is pinned to `nonce`.
  const frameSrc = jsString(`${inspectorUrl}#devflowBridge=${bridgeId}`);
  const bridgeLiteral = jsString(bridgeId);
  return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta http-equiv="Content-Security-Policy"
        content="default-src 'none'; frame-src http://127.0.0.1:* http://localhost:* https://*.vscode-webview.net; style-src 'unsafe-inline'; script-src 'nonce-${nonce}';" />
  <title>${escapeHtml(title)}</title>
  <style>
    /* Fill the whole webview: no host chrome (the editor tab already names the panel) and no VS Code
       default body padding, so the shared inspector sits flush to the panel edges. */
    html, body { margin: 0 !important; padding: 0 !important; height: 100%; overflow: hidden; background: var(--vscode-editor-background, #1e1e1e); }
    #frame { position: fixed; inset: 0; width: 100%; height: 100%; border: 0; display: block; }
  </style>
</head>
<body>
  <iframe id="frame" sandbox="allow-scripts allow-forms allow-same-origin"></iframe>
  <script nonce="${nonce}">
    (function () {
      const vscode = acquireVsCodeApi();
      const frame = document.getElementById('frame');
      const bridgeId = ${bridgeLiteral};
      // Capabilities this host contributes to the shared inspector.
      const capabilities = ['copilot', 'openSource', 'saveRecording', 'selection'];
      // Map the shared inspector's semantic theme tokens onto VS Code's theme colors so the panel
      // adopts the user's active color theme (light / dark / high-contrast). getComputedStyle resolves
      // each --vscode-* var to a concrete color; the inspector re-validates every value before use.
      const THEME_MAP = {
        '--df-bg': '--vscode-editor-background', '--df-surface': '--vscode-sideBar-background',
        '--df-surface-2': '--vscode-editorWidget-background', '--df-fg': '--vscode-editor-foreground',
        '--df-muted': '--vscode-descriptionForeground', '--df-border': '--vscode-panel-border',
        '--df-border-subtle': '--vscode-editorWidget-border', '--df-hover': '--vscode-toolbar-hoverBackground',
        '--df-hover-row': '--vscode-list-hoverBackground', '--df-accent': '--vscode-button-background',
        '--df-accent-fg': '--vscode-button-foreground', '--df-selected': '--vscode-list-activeSelectionBackground',
        '--df-selected-fg': '--vscode-list-activeSelectionForeground', '--df-danger': '--vscode-errorForeground',
        '--df-focus': '--vscode-focusBorder', '--df-warn': '--vscode-editorWarning-foreground',
        '--df-error': '--vscode-errorForeground'
      };
      function currentMode() {
        // VS Code sets the theme class (vscode-light / vscode-dark / vscode-high-contrast[-light]) and
        // data-vscode-theme-kind on <body>; check <html> too for resilience across VS Code versions.
        const b = document.body, r = document.documentElement;
        const k = (b.getAttribute('data-vscode-theme-kind') || r.getAttribute('data-vscode-theme-kind') || b.className || r.className || '');
        if (/light/i.test(k)) return 'light';           // covers vscode-light and vscode-high-contrast-light
        return 'dark';                                   // vscode-dark + vscode-high-contrast (HC colors ride in via palette)
      }
      function readVars(el) {
        const cs = getComputedStyle(el);
        const out = {};
        for (const key in THEME_MAP) { const v = cs.getPropertyValue(THEME_MAP[key]).trim(); if (v) out[key] = v; }
        return out;
      }
      function buildTheme() {
        // Theme vars are defined on :root; body inherits them. Read both and prefer the :root values so a
        // missing var on one element still resolves. Even if the palette ends up empty, mode alone fixes
        // the cross-origin iframe's wrong prefers-color-scheme (the white-in-dark-VS-Code symptom).
        const palette = Object.assign({}, readVars(document.body), readVars(document.documentElement));
        return { mode: currentMode(), palette: palette };
      }
      function announce() {
        try {
          if (frame.contentWindow) {
            frame.contentWindow.postMessage({ type: 'devflow:host', v: 1, bridgeId: bridgeId, capabilities: capabilities, hostKind: 'vscode', hostLabel: 'VS Code Inspector', theme: buildTheme() }, '*');
          }
        } catch (e) { /* cross-origin during teardown */ }
      }
      function sendTheme() {
        try {
          if (frame.contentWindow) {
            frame.contentWindow.postMessage(Object.assign({ type: 'devflow:theme', v: 1, bridgeId: bridgeId }, buildTheme()), '*');
          }
        } catch (e) { /* cross-origin during teardown */ }
      }
      // Announce capabilities ONLY in response to the inspector's nonce-authenticated
      // 'devflow:ready' — never unconditionally on iframe 'load', so the bridge secret can't leak to
      // a page the iframe later navigates to (it wouldn't know the fragment nonce to send 'ready').
      window.addEventListener('message', function (e) {
        if (e.source !== frame.contentWindow) return;             // only our embedded inspector
        const d = e.data;
        if (!d || d.bridgeId !== bridgeId) return;                // nonce-authenticated
        if (d.type === 'devflow:ready') { announce(); return; }
        if (d.type === 'devflow:sendToCopilot' || d.type === 'devflow:openSource' || d.type === 'devflow:recordingComplete' || d.type === 'devflow:selectionChanged') {
          vscode.postMessage(d);                                  // relay to the extension host
        }
      });
      // Attach the listener BEFORE navigating the iframe so no early 'ready' is lost.
      // Re-push the theme whenever the user switches VS Code color theme (body class / theme-kind flip).
      try {
        new MutationObserver(function () { sendTheme(); }).observe(document.body, { attributes: true, attributeFilter: ['class', 'data-vscode-theme-kind', 'data-vscode-theme-name', 'style'] });
      } catch (e) { /* MutationObserver is always available in a webview */ }
      frame.src = ${frameSrc};
    })();
  </script>
</body>
</html>`;
}

function randomToken(): string {
  // 128-bit URL-safe token (base64url) — safe in a URL fragment and in the [A-Za-z0-9_-] the shared
  // inspector accepts for the bridge nonce.
  return randomBytes(16).toString("base64").replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function escapeHtml(s: string): string {
  return s.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c] as string);
}

function jsString(s: string): string {
  // Safe string literal for inlining into the <script>: JSON-encode, then neutralize `<` so a value
  // can never terminate the script element.
  return JSON.stringify(s).replace(/</g, "\\u003c");
}

export function deactivate(): void {
  // Webview panels are disposed with the extension context; nothing extra to clean up.
}
