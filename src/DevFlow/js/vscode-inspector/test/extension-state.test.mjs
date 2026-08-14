import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

import {
  agentRuntimeIdentity,
  selectRefreshedAgent,
} from "../dist-test/agent-identity.js";
import { requiresBridgeRequestId } from "../dist-test/bridge-contract.js";
import {
  isNativeApprovalRequest,
  performNativeApproval,
} from "../dist-test/native-approval.js";
import { BoundedReferenceStore } from "../dist-test/context-store.js";
import {
  createDevFlowUriQuery,
  parseDevFlowUri,
} from "../dist-test/uri-contract.js";

const packageJson = JSON.parse(await readFile(
  new URL("../package.json", import.meta.url),
  "utf8",
));

const current = {
  id: "a",
  project: "/work/apps/demo/Demo.csproj",
  tfm: "net10.0-windows10.0.19041.0",
  platform: "windows",
  appName: "Demo",
  packageId: "com.example.demo",
  deviceId: "desktop",
  sessionId: "session-a",
  processId: 42,
  port: 9223,
};

test("reconnect selection fails closed on duplicate matches", () => {
  assert.equal(selectRefreshedAgent([{ ...current }], current)?.id, "a");
  assert.equal(selectRefreshedAgent([
    { ...current, id: "b" },
    { ...current, id: "c" },
  ], current), null);
  assert.equal(selectRefreshedAgent([
    { ...current, id: "a" },
    { ...current, id: "b" },
  ], current)?.id, "a");
});

test("reconnect selection accepts the unique compatible replacement", () => {
  const replacement = { ...current, id: "b", sessionId: "session-a" };
  assert.equal(selectRefreshedAgent([replacement], current)?.id, "b");
  assert.equal(selectRefreshedAgent([
    replacement,
    { ...replacement, id: "c", project: "/work/apps/other/Other.csproj" },
  ], current)?.id, "b");
});

test("reconnect selection rejects an optional identity mismatch", () => {
  assert.equal(selectRefreshedAgent([{ ...current, id: "b", deviceId: "phone" }], current), null);
  assert.notEqual(
    agentRuntimeIdentity(current),
    agentRuntimeIdentity({ ...current, deviceId: "phone" }),
  );
});

test("request-gated bridge actions are enumerated", () => {
  assert.equal(requiresBridgeRequestId("devflow:attachData"), true);
  assert.equal(requiresBridgeRequestId("devflow:requestTestProposal"), true);
  assert.equal(requiresBridgeRequestId("devflow:selectionChanged"), false);
  assert.equal(requiresBridgeRequestId("devflow:openSource"), true);
  assert.equal(requiresBridgeRequestId("devflow:nativeApproval"), true);
});

test("native approval validates bounds and keeps native credentials out of results", async () => {
  const request = {
    approvalRequestId: "approval-1",
    kind: "commit",
    intent: "Save the reviewed login test",
    approvedScope: {
      allowedActions: ["author-commit"],
      allowedSelectors: ["automationId:Save"],
      allowedRoutes: ["/login"],
      allowedSideEffectClasses: ["none"],
      maxActionCount: 1,
      maxValueBytes: 0,
    },
    grantDurationSeconds: 600,
    appName: "Demo",
    platform: "windows",
    scopeSummary: "save the test, 1 exact selector, up to 1 action",
  };
  assert.equal(isNativeApprovalRequest(request), true);
  assert.equal(isNativeApprovalRequest({ ...request, approvalRequestId: "../bad" }), false);

  const calls = [];
  const token = "a".repeat(43);
  const result = await performNativeApproval(
    request,
    { brokerPort: 19223, agentId: "agent-1" },
    () => ({ pid: 1, port: 19223, startedAt: "2026-01-01T00:00:00Z", nativeApprovalToken: token }),
    async (url, options) => {
      calls.push({ url, options });
      return calls.length === 1
        ? { ok: true, status: 201, json: async () => ({ confirmationCapability: "b".repeat(43) }) }
        : { ok: true, status: 200, json: async () => ({ ok: true, message: "Approved." }) };
    },
  );
  assert.deepEqual(result, { ok: true, message: "Approved." });
  assert.equal(calls.length, 2);
  assert.equal(calls[0].options.headers["X-DevFlow-Host-Approval-Token"], token);
  assert.equal(calls[1].options.headers["X-DevFlow-Host-Approval-Token"], undefined);
  assert.deepEqual(JSON.parse(calls[0].options.body).approvedScope, JSON.parse(calls[1].options.body).approvedScope);
  assert.equal(JSON.parse(calls[0].options.body).grantDurationSeconds, JSON.parse(calls[1].options.body).grantDurationSeconds);
  assert.equal(JSON.stringify(result).includes(token), false);
});

test("DevFlow URIs round-trip bounded identifiers", () => {
  const query = createDevFlowUriQuery({
    view: "problems",
    agent: "agent-1",
    instance: "instance:2",
    problem: "problem-3",
  });
  assert.deepEqual(parseDevFlowUri("/open", query), {
    version: "1",
    view: "problems",
    agent: "agent-1",
    instance: "instance:2",
    problem: "problem-3",
  });
  assert.equal(parseDevFlowUri("/open", "v=2&view=problems"), null);
  assert.equal(parseDevFlowUri("/open", "v=1&view=unknown"), null);
  assert.equal(parseDevFlowUri("/open", "v=1&agent=../../secret"), null);
});

test("bounded references expire and evict without encoding context", () => {
  const store = new BoundedReferenceStore(1, 1024, 60_000);
  const first = store.put({ problem: "one" });
  const second = store.put({ problem: "two" });
  assert.match(first, /^[A-Za-z0-9_-]+$/);
  assert.equal(store.get(first), null);
  assert.deepEqual(store.get(second), { problem: "two" });
  assert.ok(!second.includes("two"));
});

test("extension manifest contributes the participant, tools, URI activation, and MCP provider", () => {
  const participant = packageJson.contributes.chatParticipants.find(
    (candidate) => candidate.name === "devflow",
  );
  assert.ok(participant);
  assert.deepEqual(
    participant.commands.map((command) => command.name),
    ["inspect", "diagnose-selection", "explain-problem", "create-test"],
  );
  const tools = new Set(
    packageJson.contributes.languageModelTools.map((tool) => tool.name),
  );
  for (const tool of [
    "maui-devflow_getSelectedElement",
    "maui-devflow_getDataSnapshot",
    "maui-devflow_openInspector",
    "maui-devflow_resolveActiveApp",
    "maui-devflow_getProblems",
    "maui-devflow_getCurrentEvidence",
  ]) {
    assert.equal(tools.has(tool), true, `missing ${tool}`);
  }
  assert.deepEqual(packageJson.activationEvents, ["onUri"]);
  assert.equal(packageJson.private, undefined);
  assert.equal(
    packageJson.contributes.mcpServerDefinitionProviders[0].id,
    "mauiDevflow.localMcp",
  );
});
