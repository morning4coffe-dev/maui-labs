const TEST_WORKBENCH_CAPABILITIES = Object.freeze([
  'saveTestBundle',
  'loadTestBundle',
  'pickTrace',
  'attachTestContext',
  'requestTestProposal',
  'openSourceDiff',
  'applySourceProposal',
  'applyCSharpSourceProposal',
  'getCSharpSourceSelection',
]);

const MESSAGE_TYPES = Object.freeze({
  saveTestBundle: 'devflow:saveTestBundle',
  loadTestBundle: 'devflow:loadTestBundle',
  pickTrace: 'devflow:pickTrace',
  attachTestContext: 'devflow:attachTestContext',
  requestTestProposal: 'devflow:requestTestProposal',
  openSourceDiff: 'devflow:openSourceDiff',
  applySourceProposal: 'devflow:applySourceProposal',
  applyCSharpSourceProposal: 'devflow:applyCSharpSourceProposal',
  getCSharpSourceSelection: 'devflow:getCSharpSourceSelection',
});

const TEST_PROPOSAL_APPROVAL_RESULT = 'devflow:testProposalApprovalResult';

export { TEST_WORKBENCH_CAPABILITIES };

export function describeHostCapability(capabilities, capability) {
  const supported = Array.isArray(capabilities) && capabilities.includes(capability);
  const labels = {
    saveTestBundle: 'save test bundles',
    loadTestBundle: 'load test bundles',
    pickTrace: 'pick trace artifacts',
    attachTestContext: 'attach bounded test context',
    requestTestProposal: 'request test proposals',
    openSourceDiff: 'open source diffs',
    applySourceProposal: 'apply reviewed source proposals',
    applyCSharpSourceProposal: 'apply reviewed C# source proposals through the IDE',
    getCSharpSourceSelection: 'supply an active C# source selection',
  };
  const label = labels[capability] || capability;
  if (!supported && capability === 'pickTrace') {
    return 'This host does not support a bounded native trace picker. The shared Inspector can offer its browser file-picker fallback when the host permits it.';
  }
  if (!supported && capability === 'requestTestProposal') {
    return 'This host does not support direct agent requests. The shared Inspector will copy the bounded request for you instead.';
  }
  return supported
    ? `This host can ${label} through a bounded typed request.`
    : `This host does not support ${label}. The browser will offer a download fallback instead.`;
}

export function createInspectorHostBridge(windowLike = window) {
  const bridgeMatch = String(windowLike.location?.hash || '').match(/devflowBridge=([A-Za-z0-9_-]+)/);
  const bridgeId = bridgeMatch ? bridgeMatch[1] : null;
  const framed = windowLike.parent && windowLike.parent !== windowLike;
  let capabilities = [];
  let hostKind = 'browser';
  let sequence = 0;
  const listeners = new Set();
  const pending = new Map();

  function notify() {
    for (const listener of listeners) {
      try {
        listener([...capabilities]);
      } catch {
        // A host bridge observer must never stop the shared Inspector bridge.
      }
    }
  }

  function validMessage(event, message) {
    return !!(framed &&
      bridgeId &&
      event.source === windowLike.parent &&
      message &&
      message.bridgeId === bridgeId);
  }

  function onMessage(event) {
    const message = event.data;
    if (!validMessage(event, message)) return;
    if (message.type === 'devflow:host') {
      capabilities = Array.isArray(message.capabilities)
        ? [...new Set(message.capabilities.filter((value) => typeof value === 'string'))]
        : [];
      hostKind = typeof message.hostKind === 'string' && message.hostKind.length <= 128
        ? message.hostKind
        : 'embedded-host';
      notify();
      return;
    }
    if ((message.type !== 'devflow:hostResult' && message.type !== TEST_PROPOSAL_APPROVAL_RESULT) ||
        typeof message.requestId !== 'string') return;
    const request = pending.get(message.requestId);
    if (!request) return;
    pending.delete(message.requestId);
    windowLike.clearTimeout(request.timer);
    const approval = message.approval && typeof message.approval === 'object'
      ? {
        state: typeof message.approval.state === 'string' ? message.approval.state : undefined,
        grantId: typeof message.approval.grantId === 'string' ? message.approval.grantId : undefined,
        expiresAt: typeof message.approval.expiresAt === 'string' ? message.approval.expiresAt : undefined,
        reason: typeof message.approval.reason === 'string' ? message.approval.reason : undefined,
      }
      : undefined;
    request.resolve({
      ok: message.ok === true,
      message: typeof message.message === 'string' ? message.message : undefined,
      error: typeof message.error === 'string' ? message.error : undefined,
      approval,
      value: message.value ?? (
        typeof message.name === 'string' || typeof message.markdown === 'string' || typeof message.planJson === 'string'
          ? {
            name: typeof message.name === 'string' ? message.name : undefined,
            markdown: typeof message.markdown === 'string' ? message.markdown : undefined,
            planJson: typeof message.planJson === 'string' ? message.planJson : undefined,
          }
          : undefined),
    });
  }

  windowLike.addEventListener('message', onMessage);

  function request(capability, payload = {}, timeoutMs = 10000) {
    if (!MESSAGE_TYPES[capability]) {
      return Promise.resolve({ ok: false, error: `Unknown host capability '${capability}'.` });
    }
    if (!framed || !bridgeId || !capabilities.includes(capability)) {
      return Promise.resolve({
        ok: false,
        code: 'capability-missing',
        error: describeHostCapability(capabilities, capability),
      });
    }

    const requestId = `workbench-${Date.now().toString(36)}-${(++sequence).toString(36)}`;
    return new Promise((resolve) => {
      const timer = windowLike.setTimeout(() => {
        pending.delete(requestId);
        resolve({ ok: false, error: 'The host did not respond to the Test Workbench request.' });
      }, timeoutMs);
      pending.set(requestId, { resolve, timer });
      windowLike.parent.postMessage({
        v: 1,
        bridgeId,
        type: MESSAGE_TYPES[capability],
        requestId,
        ...payload,
      }, '*');
    });
  }

  return Object.freeze({
    capabilities: () => [...capabilities],
    hostKind: () => hostKind,
    has: (capability) => capabilities.includes(capability),
    request,
    onCapabilitiesChanged(listener) {
      if (typeof listener !== 'function') return () => {};
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    dispose() {
      windowLike.removeEventListener('message', onMessage);
      for (const request of pending.values()) {
        windowLike.clearTimeout(request.timer);
        request.resolve({ ok: false, error: 'The Test Workbench host bridge closed.' });
      }
      pending.clear();
      listeners.clear();
    },
  });
}
