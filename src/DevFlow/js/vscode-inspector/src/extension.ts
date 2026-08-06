import * as vscode from "vscode";
import * as path from "path";
import { createHash, randomBytes } from "crypto";
import type { AgentRegistration } from "@maui-devflow/client";

/**
 * MAUI DevFlow Inspector — VS Code host shell.
 *
 * A thin shell over the SHARED DevFlow inspector: it discovers the broker via the shared
 * `@maui-devflow/client`, then opens a webview that embeds the broker-hosted inspector for a
 * running app (`http://localhost:{brokerPort}/inspector/{agentId}/`) — the same inspector the
 * browser and Copilot Canvas use, including the rich property grid, visual tree, and record/replay.
 * No UI is re-implemented here; the host contributes the authenticated bridge for relaying
 * Send-to-Copilot into Chat, opening a XAML source file, and saving a recorded test.
 */
export function activate(context: vscode.ExtensionContext): void {
  context.subscriptions.push(
    vscode.commands.registerCommand(
      "mauiDevflow.openInspector",
      (startupHints?: InspectorStartupHints) => openInspector(startupHints),
    )
  );
  registerSelectionTool(context);
  registerDataSnapshotTool(context);
}

// The element the human currently has selected in the inspector (fed by devflow:selectionChanged over
// the host bridge). Powers both the one-shot Send-to-Copilot query and the language-model tool below.
type SelectedElement = { id?: string | null; type?: string; automationId?: string | null; text?: string | null; hasSource?: boolean };
interface DataSnapshot {
  kind: "dataSnapshot";
  scope: "logs" | "network" | "preferences" | "device" | "sensors" | "files" | "alerts";
  title: string;
  appName?: string | null;
  capturedAt: string;
  itemCount?: number | null;
  truncated?: boolean;
  redacted?: boolean;
  dataFormat?: string;
  data: unknown;
  agent?: {
    id?: string | null;
    appName?: string | null;
    platform?: string | null;
    port?: number | null;
  };
  metadata?: Record<string, unknown>;
  followUpTools?: string[];
}
interface BridgeResult {
  ok: boolean;
  message?: string;
  error?: string;
  name?: string;
  markdown?: string;
  planJson?: string;
  steps?: number;
  value?: {
    name?: string;
    kind?: "flow-run" | "mauitrace";
    bytesBase64?: string;
    state?: string;
    reason?: string;
    applied?: boolean;
    reverted?: boolean;
    preContentDigest?: string;
    appliedContentDigest?: string;
    contentDigest?: string;
    patchDigest?: string;
    applyRunId?: string;
    errorCode?: string;
    error?: string;
    sourceFile?: string;
    sourceLine?: number;
    sourceColumn?: number;
    sourceHash?: string;
    sourceConfidence?: string;
  };
}
interface TestBundle {
  name?: unknown;
  markdown?: unknown;
  planJson?: unknown;
  flowDigest?: unknown;
  planDigest?: unknown;
}
let currentSelection: SelectedElement | null = null;
let currentDataSnapshot: DataSnapshot | null = null;

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
            ? "The user has this .NET MAUI element selected in the MAUI DevFlow Inspector:\n" + JSON.stringify(currentSelection, null, 2)
            : "No element is currently selected in the MAUI DevFlow Inspector. Ask the user to click an element in the Inspector (or open it via 'MAUI DevFlow: Open Inspector').";
          return new ToolResult([new TextPart(text)]);
        },
      })
    );
  } catch {
    // Tool API unavailable — the one-shot Send-to-Copilot button still carries the element context.
  }
}

function registerDataSnapshotTool(context: vscode.ExtensionContext): void {
  const lm: any = (vscode as any).lm;
  if (!lm || typeof lm.registerTool !== "function") return;
  try {
    context.subscriptions.push(
      lm.registerTool("maui-devflow_getDataSnapshot", {
        invoke: async () => {
          const ToolResult = (vscode as any).LanguageModelToolResult;
          const TextPart = (vscode as any).LanguageModelTextPart;
          const text = currentDataSnapshot
            ? "The user attached this point-in-time, redacted .NET MAUI DevFlow Data snapshot. " +
              "Use followUpTools when deeper or fresher data is needed:\n" +
              JSON.stringify(currentDataSnapshot, null, 2)
            : "No DevFlow Data snapshot is currently attached. Ask the user to open Data in the MAUI DevFlow Inspector and use the paperclip button.";
          return new ToolResult([new TextPart(text)]);
        },
      })
    );
  } catch {
    // Tool API unavailable — the bridge falls back to partial text or the clipboard.
  }
}

interface InspectorStartupHints {
  test?: unknown;
  trace?: unknown;
  agentRequest?: unknown;
}

async function openInspector(startupHints?: InspectorStartupHints): Promise<void> {
  // The client is ESM; this extension is CommonJS — load it via a dynamic import.
  const { discoverBroker, readBrokerState } = await import("@maui-devflow/client");

  const config = vscode.workspace.getConfiguration("mauiDevflow");
  const configured = config.get<number>("brokerPort");
  const brokerPort = typeof configured === "number" && configured > 0 ? configured : undefined;

  const discovery = await discoverBroker({ bootstrap: "never", brokerPort });
  if (!discovery || discovery.agents.length === 0) {
    vscode.window.showWarningMessage(
      "MAUI DevFlow: no running app found. Launch your app with the DevFlow agent, then run this command again."
    );
    return;
  }

  const selectedAgent = await pickAgent(discovery.agents);
  if (!selectedAgent) return;
  let agent: AgentRegistration = selectedAgent;

  // The embed token proves this is a trusted local shell so the Inspector relaxes its
  // anti-framing headers; it lives in the local broker.json. Only use it when that state file
  // actually describes the broker we discovered, so a stale/foreign broker.json can't pair a wrong
  // token with the configured port.
  const state = readBrokerState();
  const embedToken = state && state.port === discovery.port ? state.embedToken ?? undefined : undefined;
  // The broker's HttpListener is bound to `localhost`, so the iframe host MUST be localhost
  // (a 127.0.0.1 Host header is rejected as "Invalid Hostname").
  const base = `http://localhost:${discovery.port}/inspector/${encodeURIComponent(agent.id)}/`;
  let inspectorUrl = withInspectorStartupHints(
    embedToken ? `${base}?embed=${encodeURIComponent(embedToken)}` : base,
    startupHints,
  );
  let title = agent.appName ?? agent.id;
  let activeBrokerPort = discovery.port;

  const panel = vscode.window.createWebviewPanel(
    "mauiDevflowInspector",
    `MAUI DevFlow Inspector · ${title}`,
    resolveViewColumn(config.get<string>("openLocation")),
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
  let nonce = randomToken();
  let bridgeId = randomToken();

  // Register the message handler BEFORE the webview HTML loads so no early bridge message is lost.
  panel.webview.onDidReceiveMessage(async (msg: BridgeMessage | undefined) => {
    const requestBridgeId = bridgeId;
    let result: BridgeResult;
    try {
      result = await handleBridgeMessage(msg);
    } catch (error) {
      result = { ok: false, error: `The VS Code host could not handle the request: ${String(error)}` };
    }
    if (typeof msg?.requestId === "string" &&
        (msg.type === "devflow:attachData" ||
         msg.type === "devflow:attachCopilot" ||
         msg.type === "devflow:pickWorkflow" ||
         msg.type === "devflow:saveTestBundle" ||
         msg.type === "devflow:loadTestBundle" ||
         msg.type === "devflow:pickTrace" ||
         msg.type === "devflow:openSourceDiff" ||
         msg.type === "devflow:applySourceProposal" ||
         msg.type === "devflow:applyCSharpSourceProposal" ||
         msg.type === "devflow:getCSharpSourceSelection")) {
      if (requestBridgeId !== bridgeId) return;
      await panel.webview.postMessage({
        type: "devflow:hostResult",
        v: 1,
        bridgeId: requestBridgeId,
        requestId: msg.requestId,
        ...result,
      });
    }
  });
  panel.webview.html = renderHost(inspectorUrl, title, nonce, bridgeId);

  let disposed = false;
  let refreshing = false;
  let runtimeIdentity = agentRuntimeIdentity(agent);
  let connectionSignature = inspectorConnectionSignature(
    activeBrokerPort,
    embedToken,
    agent,
  );
  const restartWatcher = setInterval(async () => {
    if (disposed || refreshing) return;
    refreshing = true;
    try {
      const refreshed = await discoverBroker({ bootstrap: "never", brokerPort });
      if (!refreshed || refreshed.agents.length === 0) return;
      const nextAgent = refreshed.agents.find((candidate) => candidate.id === agent.id) ??
        refreshed.agents.find((candidate) =>
          candidate.project === agent.project &&
          candidate.tfm === agent.tfm &&
          candidate.platform === agent.platform &&
          candidate.appName === agent.appName);
      if (!nextAgent) return;

      const refreshedState = readBrokerState();
      const refreshedEmbedToken = refreshedState && refreshedState.port === refreshed.port
        ? refreshedState.embedToken ?? undefined
        : undefined;
      const nextSignature = inspectorConnectionSignature(
        refreshed.port,
        refreshedEmbedToken,
        nextAgent,
      );
      if (nextSignature === connectionSignature) return;

      const nextRuntimeIdentity = agentRuntimeIdentity(nextAgent);
      if (nextRuntimeIdentity !== runtimeIdentity) {
        currentSelection = null;
        currentDataSnapshot = null;
      }

      agent = nextAgent;
      activeBrokerPort = refreshed.port;
      runtimeIdentity = nextRuntimeIdentity;
      connectionSignature = nextSignature;
      title = nextAgent.appName ?? nextAgent.id;
      const nextBase = `http://localhost:${refreshed.port}/inspector/${encodeURIComponent(nextAgent.id)}/`;
      inspectorUrl = withInspectorStartupHints(
        refreshedEmbedToken
          ? `${nextBase}?embed=${encodeURIComponent(refreshedEmbedToken)}`
          : nextBase,
        startupHints,
      );
      nonce = randomToken();
      bridgeId = randomToken();
      panel.title = `MAUI DevFlow Inspector · ${title}`;
      panel.webview.options = {
        enableScripts: true,
        portMapping: [{ webviewPort: refreshed.port, extensionHostPort: refreshed.port }],
      };
      panel.webview.html = renderHost(inspectorUrl, title, nonce, bridgeId);
    } catch {
      // Keep the last frame visible while the broker is restarting; the next interval retries discovery.
    } finally {
      refreshing = false;
    }
  }, 2500);
  panel.onDidDispose(() => {
    disposed = true;
    clearInterval(restartWatcher);
  });
}

function agentRuntimeIdentity(agent: AgentRegistration): string {
  return [
    agent.id,
    agent.sessionId ?? "",
    agent.processId ?? "",
    agent.connectedAt ?? "",
  ].join("|");
}

function withInspectorStartupHints(
  inspectorUrl: string,
  hints: InspectorStartupHints | undefined,
): string {
  const url = new URL(inspectorUrl);
  for (const [key, value] of [
    ["test", hints?.test],
    ["trace", hints?.trace],
    ["agentRequest", hints?.agentRequest],
  ] as const) {
    if (typeof value !== "string") continue;
    const bounded = value.trim();
    if (!bounded || bounded.length > 2048) continue;
    url.searchParams.set(key, bounded);
  }
  return url.toString();
}

function inspectorConnectionSignature(
  brokerPort: number,
  embedToken: string | undefined,
  agent: AgentRegistration,
): string {
  return [
    brokerPort,
    embedToken ?? "",
    agentRuntimeIdentity(agent),
  ].join("|");
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
  bundle?: TestBundle;
  element?: SelectedElement | null;
  snapshot?: DataSnapshot;
  context?: "selection" | "workflow" | "combined";
  proposalId?: string;
  fileRelativePath?: string;
  patchDigest?: string;
  diff?: string;
  baseContentDigest?: string;
  patch?: SourcePatch;
  rollback?: boolean;
  requestId?: string;
}

interface SourcePatch {
  start?: unknown;
  length?: unknown;
  replacement?: unknown;
  beforeDigest?: unknown;
  afterDigest?: unknown;
}

interface CopilotPayload {
  element?: { type?: string; automationId?: string | null; text?: string | null; id?: string | null } | null;
  markdown?: string | null;
  markdownTruncated?: boolean;
  appName?: string | null;
}

async function handleBridgeMessage(msg: BridgeMessage | undefined): Promise<BridgeResult> {
  if (!msg || typeof msg.type !== "string") return { ok: false, error: "Invalid DevFlow bridge message." };
  switch (msg.type) {
    case "devflow:sendToCopilot":
      await sendToCopilot(msg.payload);
      return { ok: true };
    case "devflow:attachCopilot":
      await sendToCopilot(msg.payload);
      return { ok: true, message: "Added Inspector context to Copilot." };
    case "devflow:pickWorkflow":
      return await pickWorkflowFile();
    case "devflow:openSource":
      await openSource(msg.file, msg.line, msg.column, msg.sourceHash);
      return { ok: true };
    case "devflow:recordingComplete":
      await saveRecording(msg.name, msg.markdown);
      return { ok: true };
    case "devflow:saveTestBundle":
      return await saveTestBundle(msg.bundle);
    case "devflow:loadTestBundle":
      return await loadTestBundle();
    case "devflow:pickTrace":
      return await pickTraceArtifact();
    case "devflow:openSourceDiff":
      return await openSourceDiff(msg.diff, msg.fileRelativePath);
    case "devflow:applySourceProposal":
      return await confirmXamlSourceApply(msg.proposalId, msg.fileRelativePath, msg.patchDigest);
    case "devflow:applyCSharpSourceProposal":
      return await applyCSharpSourceProposal(msg);
    case "devflow:getCSharpSourceSelection":
      return await getCSharpSourceSelection();
    case "devflow:selectionChanged":
      currentSelection = msg.element ?? null;
      return { ok: true };
    case "devflow:attachData":
      return await attachDataToCopilot(msg.snapshot);
    default:
      return { ok: false, error: "Unsupported DevFlow bridge message." };
  }

  async function openSourceDiff(diff: string | undefined, fileRelativePath: string | undefined): Promise<BridgeResult> {
    if (typeof diff !== "string" || !diff || Buffer.byteLength(diff, "utf8") > 1024 * 1024)
      return { ok: false, error: "The reviewed source diff is missing or exceeds 1 MB." };
    try {
      const document = await vscode.workspace.openTextDocument({
        content: diff,
        language: "diff",
      });
      await vscode.window.showTextDocument(document, { preview: true });
      return { ok: true, message: `Opened reviewed source diff${fileRelativePath ? ` for ${fileRelativePath}` : ""}.` };
    } catch {
      return { ok: false, error: "VS Code could not open the reviewed XAML diff." };
    }
  }

  async function confirmXamlSourceApply(
    proposalId: string | undefined,
    fileRelativePath: string | undefined,
    patchDigest: string | undefined,
  ): Promise<BridgeResult> {
    if (typeof proposalId !== "string" || proposalId.length > 128 ||
        typeof fileRelativePath !== "string" || fileRelativePath.length > 512 ||
        typeof patchDigest !== "string" || !/^sha256:[0-9a-f]{64}$/i.test(patchDigest)) {
      return { ok: false, error: "The reviewed XAML source proposal binding is invalid." };
    }
    const choice = await vscode.window.showWarningMessage(
      `Apply the reviewed AutomationId-only XAML proposal to ${fileRelativePath}? VS Code will ask the local DevFlow host to perform its compare-and-swap write. Flow selectors are not changed.`,
      { modal: true },
      "Apply reviewed XAML change",
    );
    return choice === "Apply reviewed XAML change"
      ? { ok: true, message: "VS Code confirmed the bounded local XAML apply." }
      : { ok: false, error: "The local VS Code user did not confirm the XAML source apply." };
  }

  async function applyCSharpSourceProposal(message: BridgeMessage): Promise<BridgeResult> {
    const proposalId = typeof message.proposalId === "string" ? message.proposalId : "";
    const relativePath = typeof message.fileRelativePath === "string" ? message.fileRelativePath : "";
    const patchDigest = typeof message.patchDigest === "string" ? message.patchDigest : "";
    const baseContentDigest = typeof message.baseContentDigest === "string" ? message.baseContentDigest : "";
    const patch = message.patch;
    const rollback = message.rollback === true;
    if (!/^[A-Za-z0-9_-]{1,128}$/.test(proposalId) ||
        !isSafeCSharpRelativePath(relativePath) ||
        !isSha256Digest(patchDigest) ||
        !isSha256Digest(baseContentDigest) ||
        !isValidSourcePatch(patch)) {
      return { ok: false, error: "The reviewed C# source proposal binding is invalid." };
    }

    const target = resolveWorkspaceCSharpSource(relativePath);
    if (!target) {
      return {
        ok: false,
        error: "VS Code can apply reviewed C# source only to one project file inside the open workspace.",
      };
    }

    try {
      const document = await vscode.workspace.openTextDocument(target);
      if (document.isDirty) {
        return { ok: false, error: "Save or discard local C# edits before applying the reviewed proposal." };
      }
      const beforeBytes = await vscode.workspace.fs.readFile(target);
      const preContentDigest = sha256Digest(beforeBytes);
      if (preContentDigest !== baseContentDigest.toLowerCase()) {
        return {
          ok: false,
          error: "The C# file changed after proposal preview. The IDE did not apply the stale patch.",
          value: { applied: false, reverted: false, preContentDigest, patchDigest },
        };
      }

      const start = patch!.start as number;
      const length = patch!.length as number;
      const replacement = patch!.replacement as string;
      const content = document.getText();
      if (start > content.length || length > content.length - start) {
        return { ok: false, error: "The reviewed C# patch span is outside the current document." };
      }
      const updated = content.slice(0, start) + replacement + content.slice(start + length);
      await openCSharpProposalDiff(document, updated, rollback);
      const action = rollback ? "Apply reviewed C# rollback" : "Apply reviewed C# change";
      const choice = await vscode.window.showWarningMessage(
        rollback
          ? `Apply the exact reviewed C# rollback to ${relativePath}?`
          : `Apply the exact reviewed C# AutomationId patch to ${relativePath}?`,
        { modal: true },
        action,
      );
      if (choice !== action) {
        return { ok: false, error: "The local VS Code user did not apply the reviewed C# patch." };
      }

      const range = new vscode.Range(document.positionAt(start), document.positionAt(start + length));
      const edit = new vscode.WorkspaceEdit();
      edit.replace(target, range, replacement);
      if (!await vscode.workspace.applyEdit(edit) || !await document.save()) {
        return { ok: false, error: "VS Code could not apply and save the exact reviewed C# patch." };
      }

      const afterBytes = await vscode.workspace.fs.readFile(target);
      const afterContentDigest = sha256Digest(afterBytes);
      const expectedAfterDigest = String(patch!.afterDigest).toLowerCase();
      if (afterContentDigest !== expectedAfterDigest) {
        return {
          ok: false,
          error: "The saved C# file bytes do not match the reviewed patch result; broker acknowledgment was withheld.",
          value: {
            applied: false,
            reverted: false,
            preContentDigest,
            appliedContentDigest: afterContentDigest,
            contentDigest: afterContentDigest,
            patchDigest,
            errorCode: "post-apply-hash-mismatch",
          },
        };
      }

      return {
        ok: true,
        message: rollback ? "VS Code applied the reviewed C# rollback." : "VS Code applied the reviewed C# patch.",
        value: rollback
          ? {
            reverted: true,
            preContentDigest,
            contentDigest: afterContentDigest,
            patchDigest,
          }
          : {
            applied: true,
            preContentDigest,
            appliedContentDigest: afterContentDigest,
            patchDigest,
            applyRunId: `vscode-${Date.now().toString(36)}`,
          },
      };
    } catch {
      return { ok: false, error: "VS Code could not open or apply the reviewed C# source patch." };
    }
  }

  async function getCSharpSourceSelection(): Promise<BridgeResult> {
    const editor = vscode.window.activeTextEditor;
    const document = editor?.document;
    if (!editor || !document || document.languageId !== "csharp" ||
        document.uri.scheme !== "file" || document.isUntitled) {
      return {
        ok: false,
        error: "Open and save the exact C# declaration in VS Code before creating a source proposal.",
      };
    }
    if (document.isDirty) {
      return { ok: false, error: "Save the active C# document before creating a source proposal." };
    }
    const folders = vscode.workspace.workspaceFolders ?? [];
    if (!folders.some((folder) => isInside(folder.uri.fsPath, document.uri.fsPath))) {
      return { ok: false, error: "The active C# document is outside the open VS Code workspace." };
    }
    const selection = editor.selection.active;
    return {
      ok: true,
      message: "Using the active VS Code C# declaration for Roslyn proposal analysis.",
      value: {
        sourceFile: document.uri.fsPath,
        sourceLine: selection.line + 1,
        sourceColumn: selection.character + 1,
        sourceHash: createHash("sha256").update(document.getText(), "utf8").digest("hex").slice(0, 16),
        sourceConfidence: "roslyn-proven",
      },
    };
  }
}

function isSha256Digest(value: string): boolean {
  return /^sha256:[0-9a-f]{64}$/i.test(value);
}

function sha256Digest(value: Uint8Array): string {
  return "sha256:" + createHash("sha256").update(value).digest("hex");
}

function isValidSourcePatch(patch: SourcePatch | undefined): patch is Required<SourcePatch> {
  return !!patch &&
    Number.isInteger(patch.start) && (patch.start as number) >= 0 &&
    Number.isInteger(patch.length) && (patch.length as number) >= 0 &&
    typeof patch.replacement === "string" && Buffer.byteLength(patch.replacement, "utf8") <= 1024 &&
    isSha256Digest(String(patch.beforeDigest)) &&
    isSha256Digest(String(patch.afterDigest));
}

function isSafeCSharpRelativePath(value: string): boolean {
  if (!value || value.length > 512 || !/\.cs$/i.test(value) || path.isAbsolute(value))
    return false;
  const normalized = value.replace(/\\/g, "/");
  return !normalized.split("/").some((segment) => !segment || segment === "." || segment === "..") &&
    !normalized.includes("\0");
}

function resolveWorkspaceCSharpSource(relativePath: string): vscode.Uri | null {
  const normalized = relativePath.replace(/\\/g, "/");
  const matches = (vscode.workspace.workspaceFolders ?? [])
    .map((folder) => ({ folder, uri: vscode.Uri.joinPath(folder.uri, ...normalized.split("/")) }))
    .filter(({ folder, uri }) => isInside(folder.uri.fsPath, uri.fsPath));
  return matches.length === 1 ? matches[0].uri : null;
}

async function openCSharpProposalDiff(
  document: vscode.TextDocument,
  updated: string,
  rollback: boolean,
): Promise<void> {
  const preview = await vscode.workspace.openTextDocument({ content: updated, language: "csharp" });
  await vscode.commands.executeCommand(
    "vscode.diff",
    document.uri,
    preview.uri,
    rollback ? "Reviewed C# rollback" : "Reviewed C# AutomationId proposal",
    { preview: true },
  );
}

const FLOW_RUN_MAX_BYTES = 1024 * 1024;
const EVIDENCE_MAX_BYTES = 64 * 1024 * 1024;

async function pickTraceArtifact(): Promise<BridgeResult> {
  const picked = await vscode.window.showOpenDialog({
    canSelectMany: false,
    canSelectFiles: true,
    canSelectFolders: false,
    filters: { "DevFlow trace": ["json", "mauitrace"] },
    title: "Choose a bounded DevFlow flow-run report or evidence bundle",
  });
  const file = picked?.[0];
  if (!file) return { ok: false, error: "Trace selection was cancelled." };

  const name = path.basename(file.fsPath);
  const lower = name.toLowerCase();
  const kind = lower.endsWith(".mauitrace")
    ? "mauitrace"
    : lower.endsWith(".json")
      ? "flow-run"
      : null;
  if (!kind) return { ok: false, error: "Choose a .json flow-run report or .mauitrace v1 bundle." };

  try {
    const stat = await vscode.workspace.fs.stat(file);
    const maximum = kind === "flow-run" ? FLOW_RUN_MAX_BYTES : EVIDENCE_MAX_BYTES;
    if (stat.size <= 0 || stat.size > maximum) {
      return {
        ok: false,
        error: `${kind === "flow-run" ? "flow-run.json" : ".mauitrace"} must be 1 byte to ${Math.floor(maximum / (1024 * 1024))} MB.`,
      };
    }
    const bytes = await vscode.workspace.fs.readFile(file);
    if (bytes.byteLength !== stat.size || bytes.byteLength > maximum) {
      return { ok: false, error: "The selected trace changed or exceeded its bounded import size." };
    }
    return {
      ok: true,
      value: {
        name,
        kind,
        bytesBase64: Buffer.from(bytes).toString("base64"),
      },
    };
  } catch {
    return { ok: false, error: "Could not read the selected trace artifact." };
  }
}

async function sendToCopilot(payload: CopilotPayload | undefined): Promise<void> {
  const carriesElement = !!payload && Object.prototype.hasOwnProperty.call(payload, "element");
  const el = carriesElement ? payload?.element : currentSelection;
  if (el) currentSelection = el; // keep the language-model tool in sync with what we're attaching

  const bits: string[] = [];
  if (el) {
    const parts = [el.type ?? "Element"];
    if (el.automationId) parts.push(`AutomationId="${el.automationId}"`);
    if (el.text) parts.push(`text="${el.text}"`);
    bits.push(`Selected .NET MAUI element: ${parts.join(" ")}.`);
  }
  if (payload?.markdown) bits.push("\nRecorded steps:\n" + payload.markdown + (payload.markdownTruncated ? "\n…(truncated)" : ""));
  const context = bits.join("\n") || "No element is selected in the MAUI DevFlow Inspector.";
  if (!el && payload?.markdown) {
    try {
      await vscode.commands.executeCommand("workbench.action.chat.open", {
        query: context,
        isPartialQuery: true,
      });
      return;
    } catch {
      await vscode.env.clipboard.writeText(context);
      vscode.window.showInformationMessage("DevFlow: workflow context copied for Copilot.");
      return;
    }
  }
  await attachToolContext(
    "maui-devflow_getSelectedElement",
    "#mauiSelection ",
    context,
    payload?.markdown ? context : "",
    "Added the MAUI selection to Copilot.",
    "Copied the MAUI selection context for Copilot.",
    "DevFlow: Copilot Chat unavailable — selection context copied to the clipboard.");
}

async function pickWorkflowFile(): Promise<BridgeResult> {
  const picked = await vscode.window.showOpenDialog({
    canSelectMany: false,
    canSelectFiles: true,
    canSelectFolders: false,
    filters: { "DevFlow workflow": ["md"] },
    title: "Choose a DevFlow workflow test",
  });
  const file = picked?.[0];
  if (!file) return { ok: false, error: "Workflow selection was cancelled." };
  try {
    const stat = await vscode.workspace.fs.stat(file);
    if (stat.size > 1024 * 1024)
      return { ok: false, error: "Workflow test files larger than 1 MB cannot be loaded." };
    const bytes = await vscode.workspace.fs.readFile(file);
    return {
      ok: true,
      name: path.basename(file.fsPath),
      markdown: Buffer.from(bytes).toString("utf8"),
    };
  } catch {
    return { ok: false, error: "Could not read the selected workflow test." };
  }
}

const dataSnapshotScopes = new Set(["logs", "network", "preferences", "device", "sensors", "files", "alerts"]);
const DATA_SNAPSHOT_MAX_BYTES = 20_000;

async function attachDataToCopilot(snapshot: DataSnapshot | undefined): Promise<BridgeResult> {
  if (!snapshot || snapshot.kind !== "dataSnapshot" || snapshot.redacted !== true
      || !dataSnapshotScopes.has(snapshot.scope) || typeof snapshot.title !== "string") {
    const error = "The Data snapshot was invalid and was not added to Copilot.";
    vscode.window.showWarningMessage(`DevFlow: ${error}`);
    return { ok: false, error };
  }
  let serialized: string;
  try {
    serialized = JSON.stringify(snapshot);
  } catch {
    return { ok: false, error: "The Data snapshot could not be serialized." };
  }
  if (Buffer.byteLength(serialized, "utf8") > DATA_SNAPSHOT_MAX_BYTES) {
    const error = "The Data snapshot exceeded the safe context size and was not added.";
    vscode.window.showWarningMessage(`DevFlow: ${error}`);
    return { ok: false, error };
  }
  currentDataSnapshot = JSON.parse(serialized) as DataSnapshot;
  const fallback = "MAUI DevFlow Data snapshot:\n" + JSON.stringify(currentDataSnapshot, null, 2);
  return await attachToolContext(
    "maui-devflow_getDataSnapshot",
    "#mauiData ",
    fallback,
    "",
    `Added ${currentDataSnapshot.title} to Copilot.`,
    "Copied the Data context for Copilot.",
    "DevFlow: Copilot Chat unavailable — Data context copied to the clipboard.");
}

async function attachToolContext(
  toolId: string,
  reference: string,
  fallbackText: string,
  partialQuery: string,
  attachedMessage: string,
  copiedMessage: string,
  clipboardMessage: string,
): Promise<BridgeResult> {
  const openChat = async (query: string, partial: boolean) =>
    vscode.commands.executeCommand("workbench.action.chat.open", { query, isPartialQuery: partial });

  if (vscodeAtLeast(1, 98)) {
    try {
      await vscode.commands.executeCommand("workbench.action.chat.open");
      await new Promise((resolve) => setTimeout(resolve, 50));
      await vscode.commands.executeCommand("workbench.action.chat.open", {
        query: partialQuery,
        isPartialQuery: true,
        toolIds: [toolId],
      });
      return { ok: true, message: attachedMessage };
    } catch {
      // Fall through to the text-reference compatibility path.
    }
  }

  try {
    await openChat(reference + partialQuery, true);
    return { ok: true, message: attachedMessage };
  } catch {
    // Older VS Code without tool/partial-query support uses descriptive text, then the clipboard.
  }

  try {
    await openChat(fallbackText, true);
    return { ok: true, message: attachedMessage };
  } catch {
    try {
      await vscode.env.clipboard.writeText(fallbackText);
      vscode.window.showInformationMessage(clipboardMessage);
      return { ok: true, message: copiedMessage };
    } catch {
      return { ok: false, error: "Copilot Chat was unavailable and the context could not be copied." };
    }
  }
}

function vscodeAtLeast(major: number, minor: number): boolean {
  const match = /^(\d+)\.(\d+)/.exec(vscode.version);
  if (!match) return false;
  const currentMajor = Number(match[1]);
  const currentMinor = Number(match[2]);
  return currentMajor > major || (currentMajor === major && currentMinor >= minor);
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
  if (folders.length)
    await vscode.workspace.fs.createDirectory(vscode.Uri.joinPath(folders[0].uri, "maui-tests"));
  const defaultUri = folders.length
    ? vscode.Uri.joinPath(folders[0].uri, "maui-tests", `${safe}.md`)
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

const TEST_BUNDLE_MAX_BYTES = 1024 * 1024;

function safeTestBundleName(value: unknown): string | null {
  if (typeof value !== "string") return null;
  const name = value.trim();
  if (!name || name.length > 255 || !/\.md$/i.test(name) ||
      name.includes("/") || name.includes("\\") || path.basename(name) !== name)
    return null;
  return name;
}

function safeDigest(value: unknown): string | null {
  return typeof value === "string" && /^[a-f0-9]{64}$/i.test(value) ? value.toLowerCase() : null;
}

function bundlePlanName(flowName: string): string {
  return flowName.replace(/\.md$/i, "") + ".maui-plan.json";
}

function canonicalBundleJson(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(canonicalBundleJson);
  if (value && typeof value === "object") {
    return Object.fromEntries(Object.keys(value as Record<string, unknown>)
      .sort()
      .map((key) => [key, canonicalBundleJson((value as Record<string, unknown>)[key])]));
  }
  return value;
}

function canonicalBundleDigest(value: unknown): string {
  return createHash("sha256").update(JSON.stringify(canonicalBundleJson(value)), "utf8").digest("hex");
}

function flowPayload(markdown: string): unknown | null {
  const match = /```json maui-test\s*\r?\n([\s\S]*?)\r?\n```/.exec(markdown);
  if (!match) return null;
  try { return JSON.parse(match[1]); } catch { return null; }
}

async function testRoot(): Promise<vscode.Uri | null> {
  const folder = vscode.workspace.workspaceFolders?.[0];
  if (!folder) return null;
  const root = vscode.Uri.joinPath(folder.uri, "maui-tests");
  try {
    await vscode.workspace.fs.createDirectory(root);
    const stat = await vscode.workspace.fs.stat(root);
    if ((stat.type & vscode.FileType.SymbolicLink) !== 0) return null;
    return root;
  } catch {
    return null;
  }
}

async function isSafeRegularFile(uri: vscode.Uri): Promise<boolean> {
  try {
    const stat = await vscode.workspace.fs.stat(uri);
    return (stat.type & vscode.FileType.SymbolicLink) === 0;
  } catch {
    return true; // a non-existing destination is safe to create beneath the checked root
  }
}

async function saveTestBundle(bundle: TestBundle | undefined): Promise<BridgeResult> {
  const name = safeTestBundleName(bundle?.name);
  const markdown = typeof bundle?.markdown === "string" ? bundle.markdown : null;
  const planJson = typeof bundle?.planJson === "string" ? bundle.planJson : null;
  const flowDigest = safeDigest(bundle?.flowDigest);
  const planDigest = bundle?.planDigest == null ? null : safeDigest(bundle.planDigest);
  if (!name || !markdown || !planJson || !flowDigest || (bundle?.planDigest != null && !planDigest))
    return { ok: false, error: "The test bundle must contain a bounded flow, plan, and SHA-256 digests." };
  if (Buffer.byteLength(markdown, "utf8") > TEST_BUNDLE_MAX_BYTES ||
      Buffer.byteLength(planJson, "utf8") > TEST_BUNDLE_MAX_BYTES)
    return { ok: false, error: "Flow and plan files must each be 1 MB or smaller." };

  let plan: any;
  try { plan = JSON.parse(planJson); } catch { return { ok: false, error: "The plan sidecar is not valid JSON." }; }
  if (!plan || typeof plan !== "object" || plan.schema !== 1 ||
      plan.flow?.path !== name || plan.flow?.digest !== flowDigest)
    return { ok: false, error: "The plan sidecar is not bound to the requested flow filename and digest." };
  const payload = flowPayload(markdown);
  if (!payload || canonicalBundleDigest(payload) !== flowDigest)
    return { ok: false, error: "The flow digest does not match the authoritative maui-test payload." };
  if (planDigest && canonicalBundleDigest(plan) !== planDigest)
    return { ok: false, error: "The plan digest does not match the submitted plan sidecar." };

  const root = await testRoot();
  if (!root) return { ok: false, error: "No safe workspace maui-tests directory is available." };
  const flow = vscode.Uri.joinPath(root, name);
  const sidecar = vscode.Uri.joinPath(root, bundlePlanName(name));
  if (!await isSafeRegularFile(flow) || !await isSafeRegularFile(sidecar))
    return { ok: false, error: "Refusing to overwrite a symbolic-link test artifact." };

  const nonce = randomBytes(12).toString("hex");
  const staged = [
    {
      target: flow,
      temporary: flow.with({ path: flow.path + `.${nonce}.tmp` }),
      backup: flow.with({ path: flow.path + `.${nonce}.bak` }),
      content: Buffer.from(markdown, "utf8"),
    },
    {
      target: sidecar,
      temporary: sidecar.with({ path: sidecar.path + `.${nonce}.tmp` }),
      backup: sidecar.with({ path: sidecar.path + `.${nonce}.bak` }),
      content: Buffer.from(planJson, "utf8"),
    },
  ];
  const previous = new Map<string, Uint8Array | null>();
  let preserveBackups = false;
  try {
    for (const item of staged) {
      let bytes: Uint8Array | null = null;
      try { bytes = await vscode.workspace.fs.readFile(item.target); } catch {}
      previous.set(item.target.fsPath, bytes);
      if (bytes) await vscode.workspace.fs.writeFile(item.backup, bytes);
      await vscode.workspace.fs.writeFile(item.temporary, item.content);
    }
    for (const item of staged)
      await vscode.workspace.fs.rename(item.temporary, item.target, { overwrite: true });
    const document = await vscode.workspace.openTextDocument(flow);
    await vscode.window.showTextDocument(document, { preview: false });
    return { ok: true, message: `Saved ${name} and ${bundlePlanName(name)} in maui-tests.` };
  } catch {
    let restored = true;
    for (const item of [...staged].reverse()) {
      try {
        const prior = previous.get(item.target.fsPath);
        if (prior) {
          try { await vscode.workspace.fs.rename(item.backup, item.target, { overwrite: true }); }
          catch { await vscode.workspace.fs.writeFile(item.target, prior); }
        }
        else {
          try { await vscode.workspace.fs.delete(item.target, { recursive: false, useTrash: false }); } catch {}
        }
      } catch { restored = false; }
    }
    preserveBackups = !restored;
    return {
      ok: false,
      error: restored
        ? "The bundle was not saved; the previous flow and plan were restored."
        : "The bundle write failed and the prior files could not be fully restored.",
    };
  } finally {
    for (const item of staged) {
      try { await vscode.workspace.fs.delete(item.temporary, { recursive: false, useTrash: false }); } catch {}
      try {
        if (!preserveBackups)
          await vscode.workspace.fs.delete(item.backup, { recursive: false, useTrash: false });
      } catch {}
    }
  }
}

async function loadTestBundle(): Promise<BridgeResult> {
  const root = await testRoot();
  if (!root) return { ok: false, error: "No safe workspace maui-tests directory is available." };
  const picked = await vscode.window.showOpenDialog({
    canSelectMany: false,
    canSelectFiles: true,
    canSelectFolders: false,
    defaultUri: root,
    filters: { "DevFlow workflow": ["md"] },
    title: "Load a DevFlow test bundle",
  });
  const flow = picked?.[0];
  if (!flow) return { ok: false, error: "Test bundle selection was cancelled." };
  const name = safeTestBundleName(path.basename(flow.fsPath));
  if (!name || path.dirname(flow.fsPath) !== root.fsPath || !isInside(root.fsPath, flow.fsPath))
    return { ok: false, error: "Only top-level maui-tests Markdown files can be loaded." };
  const sidecar = vscode.Uri.joinPath(root, bundlePlanName(name));
  try {
    if (!await isSafeRegularFile(flow) || !await isSafeRegularFile(sidecar))
      return { ok: false, error: "Refusing to load a symbolic-link test artifact." };
    const flowBytes = await vscode.workspace.fs.readFile(flow);
    if (flowBytes.byteLength > TEST_BUNDLE_MAX_BYTES)
      return { ok: false, error: "Workflow files larger than 1 MB cannot be loaded." };
    let planJson: string | undefined;
    try {
      const planBytes = await vscode.workspace.fs.readFile(sidecar);
      if (planBytes.byteLength > TEST_BUNDLE_MAX_BYTES)
        return { ok: false, error: "Plan sidecars larger than 1 MB cannot be loaded." };
      planJson = Buffer.from(planBytes).toString("utf8");
    } catch {
      // A Markdown flow remains useful without a plan; the shared UI makes that state explicit.
    }
    return { ok: true, name, markdown: Buffer.from(flowBytes).toString("utf8"), planJson };
  } catch {
    return { ok: false, error: "Could not load the selected test bundle." };
  }
}

function isInside(root: string, target: string): boolean {
  const rel = path.relative(root, target);
  return rel.length > 0 && !rel.startsWith("..") && !path.isAbsolute(rel);
}

// `mauiDevflow.openLocation` placement: "beside"/"active" are explicit; "auto" (default) picks the
// column that keeps the user's code visible — beside the active editor when one is open, or the
// active (otherwise-empty) group when it isn't, so the inspector doesn't strand itself in a second
// empty column.
function resolveViewColumn(openLocation: string | undefined): vscode.ViewColumn {
  if (openLocation === "beside") return vscode.ViewColumn.Beside;
  if (openLocation === "active") return vscode.ViewColumn.Active;
  return vscode.window.activeTextEditor ? vscode.ViewColumn.Beside : vscode.ViewColumn.Active;
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
      const capabilities = ['copilot', 'copilotContext', 'workflowFilePicker', 'attachData', 'openSource', 'saveRecording', 'selection', 'saveTestBundle', 'loadTestBundle', 'pickTrace', 'openSourceDiff', 'applySourceProposal', 'applyCSharpSourceProposal', 'getCSharpSourceSelection'];
      // Map the shared inspector's semantic theme tokens onto VS Code's theme colors so the panel
      // adopts the user's active color theme (light / dark / high-contrast). getComputedStyle resolves
      // each --vscode-* var to a concrete color; the inspector re-validates every value before use.
      // Values may list several VS Code color IDs in priority order — e.g. the high-contrast-only
      // '--vscode-contrastBorder'/'--vscode-contrastActiveBorder' tokens are empty outside an HC theme,
      // so listing them first gives HC users crisper borders/outlines without affecting other themes.
      const THEME_MAP = {
        '--df-bg': '--vscode-editor-background', '--df-surface': '--vscode-sideBar-background',
        '--df-surface-2': '--vscode-editorWidget-background', '--df-fg': '--vscode-editor-foreground',
        '--df-muted': '--vscode-descriptionForeground',
        '--df-border': ['--vscode-contrastBorder', '--vscode-panel-border'],
        '--df-border-subtle': ['--vscode-contrastBorder', '--vscode-editorWidget-border'],
        '--df-hover': '--vscode-toolbar-hoverBackground',
        '--df-hover-row': '--vscode-list-hoverBackground', '--df-accent': '--vscode-button-background',
        '--df-accent-fg': '--vscode-button-foreground', '--df-selected': '--vscode-list-activeSelectionBackground',
        '--df-selected-fg': '--vscode-list-activeSelectionForeground', '--df-danger': '--vscode-errorForeground',
        '--df-focus': ['--vscode-focusBorder'], '--df-warn': '--vscode-editorWarning-foreground',
        '--df-error': '--vscode-errorForeground',
        // Semantic tokens the shared inspector uses for tree/property-grid syntax coloring.
        '--df-type': '--vscode-symbolIcon-classForeground', '--df-name': '--vscode-symbolIcon-variableForeground',
        '--df-source': '--vscode-symbolIcon-fileForeground', '--df-success': '--vscode-testing-iconPassed',
        '--df-outline-hover': ['--vscode-focusBorder'],
        '--df-outline-select': ['--vscode-contrastActiveBorder', '--vscode-list-focusOutline']
      };
      function currentModeKind() {
        // VS Code sets the theme class (vscode-light / vscode-dark / vscode-high-contrast[-light]) and
        // data-vscode-theme-kind on <body>; check <html> too for resilience across VS Code versions.
        const b = document.body, r = document.documentElement;
        return (b.getAttribute('data-vscode-theme-kind') || r.getAttribute('data-vscode-theme-kind') || b.className || r.className || '');
      }
      function currentMode() {
        const k = currentModeKind();
        if (/light/i.test(k)) return 'light';           // covers vscode-light and vscode-high-contrast-light
        return 'dark';                                   // vscode-dark + vscode-high-contrast (HC colors ride in via palette)
      }
      function readVars(el) {
        const cs = getComputedStyle(el);
        const out = {};
        for (const key in THEME_MAP) {
          const candidates = Array.isArray(THEME_MAP[key]) ? THEME_MAP[key] : [THEME_MAP[key]];
          for (const cand of candidates) {
            const v = cs.getPropertyValue(cand).trim();
            if (v) { out[key] = v; break; }
          }
        }
        return out;
      }
      function buildTheme() {
        // Theme vars are defined on :root; body inherits them. Read both and prefer the :root values so a
        // missing var on one element still resolves. Even if the palette ends up empty, mode alone fixes
        // the cross-origin iframe's wrong prefers-color-scheme (the white-in-dark-VS-Code symptom).
        const palette = Object.assign({}, readVars(document.body), readVars(document.documentElement));
        return { mode: currentMode(), palette: palette };
      }
      function prefersReducedMotion() {
        // VS Code adds a reduce-motion class to <body> when "workbench.reduceMotion" is on; also
        // honor the OS-level media query as a fallback (both are booleans, never sent as CSS values).
        try {
          if (/reduce[-_]?motion/i.test(document.body.className)) return true;
          return !!(window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
        } catch (e) { return false; }
      }
      function safeFontFamily(v) {
        const s = typeof v === 'string' ? v.trim() : '';
        if (!s || s.length > 120 || !/^[A-Za-z0-9 ,'"_.\-]+$/.test(s)) return undefined;
        return s;
      }
      function safeFontSize(v) {
        const s = typeof v === 'string' ? v.trim() : '';
        return /^[0-9]{1,3}(\.[0-9]+)?px$/.test(s) ? s : undefined;
      }
      function safeFontWeight(v) {
        const s = typeof v === 'string' ? v.trim() : '';
        return /^(normal|bold|[1-9]00)$/.test(s) ? s : undefined;
      }
      function buildFont() {
        // Host font metadata only — never routed through theme.palette (that channel is colors-only).
        try {
          const cs = getComputedStyle(document.documentElement);
          const family = safeFontFamily(cs.getPropertyValue('--vscode-font-family'));
          const size = safeFontSize(cs.getPropertyValue('--vscode-font-size'));
          const weight = safeFontWeight(cs.getPropertyValue('--vscode-font-weight'));
          const out = {};
          if (family) out.family = family;
          if (size) out.size = size;
          if (weight) out.weight = weight;
          return Object.keys(out).length ? out : undefined;
        } catch (e) { return undefined; }
      }
      function buildProfile() {
        const profile = { surface: 'editor' };
        if (/high-contrast/i.test(currentModeKind())) profile.contrast = 'high';
        if (prefersReducedMotion()) profile.reducedMotion = true;
        const font = buildFont();
        if (font) profile.font = font;
        return profile;
      }
      function announce() {
        try {
          if (frame.contentWindow) {
            frame.contentWindow.postMessage({ type: 'devflow:host', v: 1, bridgeId: bridgeId, capabilities: capabilities, hostKind: 'vscode', hostLabel: 'VS Code Inspector', theme: buildTheme(), profile: buildProfile() }, '*');
          }
        } catch (e) { /* cross-origin during teardown */ }
      }
      function sendTheme() {
        try {
          if (frame.contentWindow) {
            frame.contentWindow.postMessage(Object.assign({ type: 'devflow:theme', v: 1, bridgeId: bridgeId, profile: buildProfile() }, buildTheme()), '*');
          }
        } catch (e) { /* cross-origin during teardown */ }
      }
      // Announce capabilities ONLY in response to the inspector's nonce-authenticated
      // 'devflow:ready' — never unconditionally on iframe 'load', so the bridge secret can't leak to
      // a page the iframe later navigates to (it wouldn't know the fragment nonce to send 'ready').
      window.addEventListener('message', function (e) {
        const d = e.data;
        if (e.source !== frame.contentWindow) {
          if (d && d.type === 'devflow:hostResult' && d.bridgeId === bridgeId && frame.contentWindow) {
            frame.contentWindow.postMessage(d, '*');
          }
          return;
        }
        if (!d || d.bridgeId !== bridgeId) return;                // nonce-authenticated
        if (d.type === 'devflow:ready') { announce(); return; }
        if (d.type === 'devflow:sendToCopilot' || d.type === 'devflow:attachCopilot' || d.type === 'devflow:pickWorkflow' || d.type === 'devflow:attachData' || d.type === 'devflow:openSource' || d.type === 'devflow:recordingComplete' || d.type === 'devflow:selectionChanged' || d.type === 'devflow:saveTestBundle' || d.type === 'devflow:loadTestBundle' || d.type === 'devflow:pickTrace' || d.type === 'devflow:openSourceDiff' || d.type === 'devflow:applySourceProposal' || d.type === 'devflow:applyCSharpSourceProposal' || d.type === 'devflow:getCSharpSourceSelection') {
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
