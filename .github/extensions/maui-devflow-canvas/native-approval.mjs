const REQUEST_ID = /^[A-Za-z0-9_-]{1,256}$/;
const KIND = /^[a-z][a-z0-9-]{0,63}$/;
const TOKEN = /^[A-Za-z0-9_-]{32,256}$/;

function boundedString(value, maximum) {
  return typeof value === "string" && value.length > 0 && value.length <= maximum;
}

function boundedList(value, maximumItems, maximumItemLength) {
  return Array.isArray(value) &&
    value.length <= maximumItems &&
    value.every((item) => boundedString(item, maximumItemLength)) &&
    new Set(value).size === value.length;
}

export function isNativeApprovalRequest(value) {
  const scope = value?.approvedScope;
  return !!value &&
    boundedString(value.approvalRequestId, 256) &&
    REQUEST_ID.test(value.approvalRequestId) &&
    boundedString(value.kind, 64) &&
    KIND.test(value.kind) &&
    boundedString(value.intent, 1024) &&
    boundedString(value.appName, 256) &&
    boundedString(value.platform, 128) &&
    boundedString(value.scopeSummary, 1024) &&
    Number.isInteger(value.grantDurationSeconds) &&
    value.grantDurationSeconds >= 1 &&
    value.grantDurationSeconds <= 900 &&
    scope &&
    boundedList(scope.allowedActions, 64, 512) &&
    scope.allowedActions.length > 0 &&
    boundedList(scope.allowedSelectors, 128, 512) &&
    boundedList(scope.allowedRoutes, 64, 512) &&
    boundedList(scope.allowedSideEffectClasses, 64, 256) &&
    Number.isInteger(scope.maxActionCount) &&
    scope.maxActionCount >= 1 &&
    scope.maxActionCount <= 10_000 &&
    Number.isInteger(scope.maxValueBytes) &&
    scope.maxValueBytes >= 0 &&
    scope.maxValueBytes <= 1_048_576;
}

function wireScope(scope) {
  return {
    allowedActions: [...scope.allowedActions],
    allowedSelectors: [...scope.allowedSelectors],
    allowedRoutes: [...scope.allowedRoutes],
    allowedSideEffectClasses: [...scope.allowedSideEffectClasses],
    maxActionCount: scope.maxActionCount,
    maxValueBytes: scope.maxValueBytes,
  };
}

async function postJson(fetchImpl, url, body, headers = {}) {
  const response = await fetchImpl(url, {
    method: "POST",
    headers: { "Content-Type": "application/json", ...headers },
    body: JSON.stringify(body),
  });
  return {
    ok: response.ok,
    status: response.status,
    body: await response.json().catch(() => null),
  };
}

function responseError(body, fallback) {
  return typeof body?.error === "string"
    ? body.error
    : typeof body?.error?.message === "string"
      ? body.error.message
      : fallback;
}

/**
 * Reads the local state only after the outer Canvas shell has shown its confirm dialog. The
 * single-use confirmation capability stays in this host-only function and is never returned.
 */
export async function performCanvasNativeApproval(request, target, readState, fetchImpl = fetch) {
  if (!isNativeApprovalRequest(request)) {
    return { ok: false, error: "The native approval request contains invalid or unbounded values." };
  }
  if (!target || !Number.isInteger(target.brokerPort) || target.brokerPort < 1 ||
      target.brokerPort > 65535 || !boundedString(target.agentId, 256)) {
    return { ok: false, error: "The current Inspector target is invalid." };
  }

  const state = readState();
  const token = state?.nativeApprovalToken;
  if (!state || state.port !== target.brokerPort || !boundedString(token, 256) || !TOKEN.test(token)) {
    return { ok: false, error: "The local broker state does not match this Inspector or has no native approval token." };
  }

  const root = `http://localhost:${state.port}/inspector/${encodeURIComponent(target.agentId)}/api/workbench`;
  const approvedScope = wireScope(request.approvedScope);
  let issue;
  try {
    issue = await postJson(fetchImpl, `${root}/approval-confirmations/issue`, {
      action: "agent-request-approve",
      subjectId: request.approvalRequestId,
      approvedScope,
      grantDurationSeconds: request.grantDurationSeconds,
    }, { "X-DevFlow-Host-Approval-Token": token });
  } catch {
    return { ok: false, error: "The trusted local Inspector could not be reached to issue approval." };
  }
  const confirmationCapability = issue.body?.confirmationCapability;
  if (!issue.ok || !boundedString(confirmationCapability, 512) || !TOKEN.test(confirmationCapability)) {
    return { ok: false, error: responseError(issue.body, `Native approval confirmation was rejected (HTTP ${issue.status}).`) };
  }

  let approval;
  try {
    approval = await postJson(fetchImpl,
      `${root}/agent-requests/${encodeURIComponent(request.approvalRequestId)}/approve`,
      {
        approvedScope,
        grantDurationSeconds: request.grantDurationSeconds,
        confirmationCapability,
      });
  } catch {
    return { ok: false, error: "The trusted local Inspector could not apply the approved request." };
  }
  if (!approval.ok || approval.body?.ok !== true) {
    return { ok: false, error: responseError(approval.body, `Native approval was rejected (HTTP ${approval.status}).`) };
  }
  return {
    ok: true,
    status: typeof approval.body.message === "string"
      ? approval.body.message.slice(0, 1024)
      : "Approved by the trusted Canvas host.",
  };
}
