import assert from "node:assert/strict";
import test from "node:test";
import { isNativeApprovalRequest, performCanvasNativeApproval } from "../native-approval.mjs";

const request = Object.freeze({
  approvalRequestId: "approval-1",
  kind: "run",
  intent: "Run the reviewed login test once",
  approvedScope: {
    allowedActions: ["tap"],
    allowedSelectors: ["automationId:SignIn"],
    allowedRoutes: ["/login"],
    allowedSideEffectClasses: ["test-tenant-resettable"],
    maxActionCount: 1,
    maxValueBytes: 64,
  },
  grantDurationSeconds: 300,
  appName: "Demo",
  platform: "android",
  scopeSummary: "tap, 1 exact selector, up to 1 action",
});

test("Canvas native approval requires bounded input and uses the token only for confirmation issue", async () => {
  assert.equal(isNativeApprovalRequest(request), true);
  assert.equal(isNativeApprovalRequest({ ...request, approvedScope: { ...request.approvedScope, allowedActions: [] } }), false);

  const token = "a".repeat(43);
  const calls = [];
  const result = await performCanvasNativeApproval(
    request,
    { brokerPort: 19223, agentId: "agent-1" },
    () => ({ port: 19223, nativeApprovalToken: token }),
    async (url, options) => {
      calls.push({ url, options });
      return calls.length === 1
        ? { ok: true, status: 201, json: async () => ({ confirmationCapability: "b".repeat(43) }) }
        : { ok: true, status: 200, json: async () => ({ ok: true, message: "Approved once." }) };
    },
  );

  assert.deepEqual(result, { ok: true, status: "Approved once." });
  assert.equal(calls.length, 2);
  assert.match(calls[0].url, /approval-confirmations\/issue$/);
  assert.match(calls[1].url, /agent-requests\/approval-1\/approve$/);
  assert.equal(calls[0].options.headers["X-DevFlow-Host-Approval-Token"], token);
  assert.equal(calls[1].options.headers["X-DevFlow-Host-Approval-Token"], undefined);
  assert.equal(JSON.stringify(result).includes(token), false);
});
