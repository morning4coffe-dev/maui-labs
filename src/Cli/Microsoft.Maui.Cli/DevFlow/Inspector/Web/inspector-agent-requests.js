const LIST_KEYS = Object.freeze({
  actions: 'allowedActions',
  selectors: 'allowedSelectors',
  routes: 'allowedRoutes',
  sideEffects: 'allowedSideEffectClasses',
});

function strings(values) {
  return [...new Set((Array.isArray(values) ? values : [])
    .filter((value) => typeof value === 'string' && value.trim())
    .map((value) => value.trim()))];
}

function readableAction(value) {
  return {
    'author-commit': 'save the test',
    run: 'run the test',
    cancel: 'cancel the run',
    assert: 'check an expected result',
    tap: 'tap a control',
    fill: 'enter text',
    scroll: 'scroll',
    navigate: 'navigate',
    back: 'go back',
  }[value] || readable(value);
}

export function agentRequestStarterPrompt(appName = null, platform = null) {
  const target = [appName, platform].filter(Boolean).join(' on ');
  return [
    'Use only the restricted DevFlow test-agent tools.',
    target
      ? `Discover and explicitly target ${target}.`
      : 'Discover and explicitly target the connected app.',
    'Help me define the Goal if needed, then prepare the complete test draft with steps and expected results.',
    'Request one commit review, then wait. Do not run until I approve a separate run request.',
    'Do not apply repairs or source changes automatically.',
  ].join(' ');
}

export function normalizeAgentRequestScope(scope = {}) {
  return {
    allowedActions: strings(scope.allowedActions),
    allowedSelectors: strings(scope.allowedSelectors),
    allowedRoutes: strings(scope.allowedRoutes),
    allowedSideEffectClasses: strings(scope.allowedSideEffectClasses),
    maxActionCount: Number.isInteger(scope.maxActionCount) && scope.maxActionCount > 0
      ? scope.maxActionCount
      : 1,
    maxValueBytes: Number.isInteger(scope.maxValueBytes) && scope.maxValueBytes >= 0
      ? scope.maxValueBytes
      : 0,
  };
}

export function isNarrowedAgentRequestScope(requested, approved) {
  const original = normalizeAgentRequestScope(requested);
  const candidate = normalizeAgentRequestScope(approved);
  const subset = (values, allowed) => values.every((value) => allowed.includes(value));
  return candidate.allowedActions.length > 0 &&
    subset(candidate.allowedActions, original.allowedActions) &&
    subset(candidate.allowedSelectors, original.allowedSelectors) &&
    subset(candidate.allowedRoutes, original.allowedRoutes) &&
    subset(candidate.allowedSideEffectClasses, original.allowedSideEffectClasses) &&
    candidate.maxActionCount <= original.maxActionCount &&
    candidate.maxValueBytes <= original.maxValueBytes;
}

export function agentRequestSummary(request) {
  const scope = normalizeAgentRequestScope(request?.requestedScope);
  const actions = scope.allowedActions.map(readableAction);
  const actionText = actions.length === 1
    ? actions[0]
    : `${scope.allowedActions.length} action types`;
  const selectorText = scope.allowedSelectors.length === 0
    ? 'no element selectors'
    : scope.allowedSelectors.length === 1
      ? '1 exact selector'
      : `${scope.allowedSelectors.length} exact selectors`;
  return `${actionText}, ${selectorText}, up to ${scope.maxActionCount} action${scope.maxActionCount === 1 ? '' : 's'}`;
}

export function agentRequestGrantDurationSeconds(request) {
  return request?.kind === 'run' ? 300 : 600;
}

function readable(value) {
  return String(value || 'unknown').replace(/-/g, ' ');
}

function requestTitle(kind) {
  return {
    commit: 'Your agent prepared a test',
    run: 'Your agent would like to run this test once',
    'draft-change': 'Your agent suggests a test update',
    assertion: 'Your agent suggests expected results',
    exploration: 'Your agent would like to inspect test controls',
  }[kind] || `Review ${readable(kind)}`;
}

function approvalLabel(kind) {
  return {
    commit: 'Save test',
    run: 'Allow one run',
    'draft-change': 'Allow update',
    assertion: 'Add expected results',
    exploration: 'Allow exploration',
  }[kind] || 'Approve request';
}

function formatExpiry(value) {
  const time = Date.parse(value || '');
  if (!Number.isFinite(time)) return 'unknown';
  const seconds = Math.max(0, Math.ceil((time - Date.now()) / 1000));
  if (seconds < 60) return `${seconds}s`;
  return `${Math.ceil(seconds / 60)}m`;
}

function appendText(doc, parent, tag, text, className = null) {
  const element = doc.createElement(tag);
  if (className) element.className = className;
  element.textContent = text;
  parent.append(element);
  return element;
}

function createDraft(scope) {
  const normalized = normalizeAgentRequestScope(scope);
  return {
    allowedActions: new Set(normalized.allowedActions),
    allowedSelectors: new Set(normalized.allowedSelectors),
    allowedRoutes: new Set(normalized.allowedRoutes),
    allowedSideEffectClasses: new Set(normalized.allowedSideEffectClasses),
    maxActionCount: normalized.maxActionCount,
    maxValueBytes: normalized.maxValueBytes,
  };
}

function scopeFromDraft(draft) {
  return {
    allowedActions: [...draft.allowedActions],
    allowedSelectors: [...draft.allowedSelectors],
    allowedRoutes: [...draft.allowedRoutes],
    allowedSideEffectClasses: [...draft.allowedSideEffectClasses],
    maxActionCount: draft.maxActionCount,
    maxValueBytes: draft.maxValueBytes,
  };
}

function scopeGroup(doc, parent, label, values, selected, disabled, onChange) {
  if (values.length === 0) return;
  const fieldset = doc.createElement('fieldset');
  fieldset.className = 'df-agent-request-scope-group';
  const legend = doc.createElement('legend');
  legend.textContent = label;
  fieldset.append(legend);
  for (const value of values) {
    const row = doc.createElement('label');
    row.className = 'df-agent-request-choice';
    const checkbox = doc.createElement('input');
    checkbox.type = 'checkbox';
    checkbox.checked = selected.has(value);
    checkbox.disabled = disabled;
    checkbox.addEventListener('change', () => onChange(value, checkbox.checked));
    const text = doc.createElement('span');
    text.textContent = value;
    row.append(checkbox, text);
    fieldset.append(row);
  }
  parent.append(fieldset);
}

export function createAgentRequestController(options = {}) {
  const doc = options.document || document;
  const win = options.window || window;
  const api = options.inspectorApi;
  const panel = options.panel || doc.getElementById('df-agent-requests');
  const body = options.body || doc.getElementById('df-agent-requests-body');
  const tab = options.tab || doc.getElementById('df-workbench-tab-requests');
  const toolbarBadge = options.toolbarBadge || doc.getElementById('df-test-agent-request-badge');
  const tabBadge = options.tabBadge || doc.getElementById('df-agent-requests-badge');
  const openPanel = typeof options.openPanel === 'function' ? options.openPanel : () => {};
  const setStatus = typeof options.setStatus === 'function' ? options.setStatus : () => {};
  const copyText = typeof options.copyText === 'function'
    ? options.copyText
    : async (text) => {
      if (typeof win.navigator?.clipboard?.writeText !== 'function') return false;
      try {
        await win.navigator.clipboard.writeText(text);
        return true;
      } catch {
        return false;
      }
    };
  const onTransition = typeof options.onTransition === 'function' ? options.onTransition : () => {};
  if (!api || !panel || !body) {
    return Object.freeze({
      start: () => {},
      stop: () => {},
      refresh: async () => {},
      pendingCount: () => 0,
    });
  }

  const drafts = new Map();
  const confirmations = new Set();
  const busy = new Set();
  const expandedRequests = new Set();
  const knownStates = new Map();
  let requests = [];
  let appName = null;
  let platform = null;
  let timer = null;
  let stopped = false;
  let responseFingerprint = null;

  function updateBadge(element, count) {
    if (!element) return;
    if (count === 0) {
      element.hidden = true;
      element.textContent = '';
      element.removeAttribute('aria-label');
      return;
    }
    element.hidden = false;
    element.textContent = count > 99 ? '99+' : String(count);
    element.setAttribute('aria-label', `${count} pending agent request${count === 1 ? '' : 's'}`);
  }

  function syncChrome() {
    const pending = requests.filter((request) => request.state === 'pending').length;
    const available = requests.length > 0;
    updateBadge(toolbarBadge, pending);
    updateBadge(tabBadge, pending);
    if (tab) {
      tab.dataset.available = String(available);
      tab.classList.toggle('df-agent-request-pending', pending > 0);
      tab.disabled = !available && !tab.classList.contains('df-active');
      tab.setAttribute('aria-disabled', String(tab.disabled));
      const label = pending > 0
        ? `Agent requests, ${pending} pending`
        : 'Agent requests';
      tab.setAttribute('aria-label', label);
      tab.title = !available
        ? 'Agent requests appear here after your agent prepares a test or asks to run it.'
        : pending > 0
        ? `${pending} agent request${pending === 1 ? '' : 's'} waiting for review`
        : 'Review requests from test agents';
    }
  }

  function ensureDraft(request) {
    const id = request.approvalRequestId;
    if (!drafts.has(id)) drafts.set(id, createDraft(request.approvedScope || request.requestedScope));
    return drafts.get(id);
  }

  function renderScope(request, pending, expandByDefault = false) {
    const requested = normalizeAgentRequestScope(request.requestedScope);
    const draft = ensureDraft(request);
    const details = doc.createElement('details');
    details.className = 'df-agent-request-details';
    details.open = expandedRequests.has(request.approvalRequestId) || expandByDefault;
    details.addEventListener('toggle', () => {
      if (details.open) expandedRequests.add(request.approvalRequestId);
      else expandedRequests.delete(request.approvalRequestId);
    });
    const summary = doc.createElement('summary');
    summary.textContent = pending ? 'Review what your agent can do' : 'Reviewed permissions';
    details.append(summary);
    appendText(
      doc,
      details,
      'p',
      pending
        ? 'Only the buttons in this review grant permission. You may remove permissions or reduce limits.'
        : 'This is the exact scope that was reviewed.',
      'df-agent-request-help'
    );

    const grid = doc.createElement('div');
    grid.className = 'df-agent-request-scope';
    scopeGroup(doc, grid, 'Actions', requested.allowedActions, draft.allowedActions, !pending, (value, checked) => {
      checked ? draft.allowedActions.add(value) : draft.allowedActions.delete(value);
      render();
    });
    scopeGroup(doc, grid, 'Controls', requested.allowedSelectors, draft.allowedSelectors, !pending, (value, checked) => {
      checked ? draft.allowedSelectors.add(value) : draft.allowedSelectors.delete(value);
      render();
    });
    scopeGroup(doc, grid, 'Routes', requested.allowedRoutes, draft.allowedRoutes, !pending, (value, checked) => {
      checked ? draft.allowedRoutes.add(value) : draft.allowedRoutes.delete(value);
      render();
    });
    scopeGroup(doc, grid, 'App changes', requested.allowedSideEffectClasses, draft.allowedSideEffectClasses, !pending, (value, checked) => {
      checked ? draft.allowedSideEffectClasses.add(value) : draft.allowedSideEffectClasses.delete(value);
      render();
    });

    const limits = doc.createElement('div');
    limits.className = 'df-agent-request-limits';
    for (const [label, key, maximum] of [
      ['Maximum actions', 'maxActionCount', requested.maxActionCount],
      ['Maximum value bytes', 'maxValueBytes', requested.maxValueBytes],
    ]) {
      const row = doc.createElement('label');
      row.className = 'df-agent-request-limit';
      const name = doc.createElement('span');
      name.textContent = label;
      const input = doc.createElement('input');
      input.type = 'number';
      input.min = key === 'maxActionCount' ? '1' : '0';
      input.max = String(maximum);
      input.value = String(draft[key]);
      input.disabled = !pending;
      input.addEventListener('change', () => {
        const floor = key === 'maxActionCount' ? 1 : 0;
        draft[key] = Math.max(floor, Math.min(maximum, Number.parseInt(input.value, 10) || floor));
        render();
      });
      row.append(name, input);
      limits.append(row);
    }
    grid.append(limits);
    details.append(grid);
    return details;
  }

  function renderRequest(request, expandByDefault = false) {
    const pending = request.state === 'pending';
    const card = doc.createElement('article');
    card.className = `df-agent-request-card df-agent-request-${request.state || 'unknown'}`;
    card.dataset.approvalRequestId = request.approvalRequestId || '';

    const header = doc.createElement('div');
    header.className = 'df-agent-request-card-header';
    appendText(doc, header, 'h4', requestTitle(request.kind));
    appendText(doc, header, 'span', readable(request.state), 'df-agent-request-state');
    card.append(header);
    appendText(doc, card, 'p', request.intent || 'No user-visible intent was supplied.', 'df-agent-request-intent');
    appendText(
      doc,
      card,
      'p',
      `${agentRequestSummary(request)} · expires in ${formatExpiry(request.expiresAt)}`,
      'df-agent-request-meta'
    );
    if (pending) {
      const review = renderScope(request, true, expandByDefault);
      card.append(review);
      const id = request.approvalRequestId;
      const draft = ensureDraft(request);
      const approvedScope = scopeFromDraft(draft);
      const validScope = isNarrowedAgentRequestScope(request.requestedScope, approvedScope);
      const confirmation = doc.createElement('label');
      confirmation.className = 'df-agent-request-confirm';
      const checkbox = doc.createElement('input');
      checkbox.type = 'checkbox';
      checkbox.checked = confirmations.has(id);
      checkbox.addEventListener('change', () => {
        checkbox.checked ? confirmations.add(id) : confirmations.delete(id);
        render();
      });
      const confirmationText = doc.createElement('span');
      confirmationText.textContent = `I reviewed what my agent wants to do in ${appName || 'this app'}${platform ? ` on ${platform}` : ''}.`;
      confirmation.append(checkbox, confirmationText);
      review.append(confirmation);

      if (!validScope) {
        appendText(doc, review, 'p', 'Keep at least one requested action. Limits may only be reduced.', 'df-agent-request-error');
      }

      const actions = doc.createElement('div');
      actions.className = 'df-agent-request-actions';
      const approve = doc.createElement('button');
      approve.type = 'button';
      approve.className = 'df-workbench-action df-authoring-primary df-agent-request-approve';
      approve.textContent = busy.has(id) ? 'Approving...' : approvalLabel(request.kind);
      approve.disabled = busy.has(id) || !confirmations.has(id) || !validScope;
      approve.addEventListener('click', () => approveRequest(request, approvedScope));
      const reject = doc.createElement('button');
      reject.type = 'button';
      reject.className = 'df-workbench-action';
      reject.textContent = busy.has(id) ? 'Working...' : 'Reject';
      reject.disabled = busy.has(id);
      reject.addEventListener('click', () => rejectRequest(request));
      actions.append(approve, reject);
      review.append(actions);
    } else {
      const message = {
        approved: 'Approved. Your agent can continue; you do not need to copy anything into chat.',
        consumed: 'Completed. This approval was used and cannot be used again.',
        rejected: 'Rejected. Your agent cannot continue with this request.',
        expired: 'Expired. Ask your agent to submit a fresh request.',
        stale: 'The app or test changed. Ask your agent to submit a fresh request.',
      }[request.state] || 'This request is no longer pending.';
      appendText(doc, card, 'p', message, 'df-agent-request-result');
      const reviewed = doc.createElement('details');
      reviewed.className = 'df-agent-request-details';
      const summary = doc.createElement('summary');
      summary.textContent = 'Reviewed scope';
      reviewed.append(summary);
      appendText(doc, reviewed, 'p', agentRequestSummary(request), 'df-agent-request-help');
      card.append(reviewed);
    }
    return card;
  }

  function render() {
    body.replaceChildren();
    const pending = requests.filter((request) => request.state === 'pending');
    const recent = requests.filter((request) => request.state !== 'pending').slice(0, 6);
    if (requests.length === 0) {
      const empty = doc.createElement('section');
      empty.className = 'df-authoring-section df-tool-empty-state df-agent-request-empty-state';
      appendText(doc, empty, 'h4', 'Work with your agent');
      appendText(
        doc,
        empty,
        'p',
        `Ask your coding agent to prepare or improve a DevFlow test${appName ? ` for ${appName}` : ''}. Its save and run requests will appear here for review.`,
        'df-workbench-intro'
      );
      const steps = doc.createElement('ol');
      steps.className = 'df-workbench-list';
      for (const text of [
        'Your agent prepares the draft and expected results.',
        'You review the test before saving it.',
        'You separately decide whether to run it once.',
      ]) appendText(doc, steps, 'li', text);
      empty.append(steps);
      const copy = doc.createElement('button');
      copy.type = 'button';
      copy.className = 'df-workbench-action';
      copy.textContent = 'Copy prompt for your agent';
      copy.addEventListener('click', async () => {
        const copied = await copyText(agentRequestStarterPrompt(appName, platform));
        setStatus(copied
          ? 'Copied instructions. Paste them into your coding agent chat.'
          : 'Could not copy the agent prompt.');
      });
      empty.append(copy);
      body.append(empty);
      syncChrome();
      return;
    }

    if (pending.length > 0) {
      appendText(
        doc,
        body,
        'p',
        `Your agent is waiting for ${pending.length === 1 ? 'a decision' : `${pending.length} decisions`}. Review what it wants to do. Nothing changes until you approve.`,
        'df-agent-request-intro'
      );
      pending.forEach((request, index) => body.append(renderRequest(request, index === 0)));
    }
    if (recent.length > 0) {
      const history = doc.createElement('details');
      history.className = 'df-agent-request-history';
      const summary = doc.createElement('summary');
      summary.textContent = `Recent decisions (${recent.length})`;
      history.append(summary);
      for (const request of recent) history.append(renderRequest(request));
      body.append(history);
    }
    syncChrome();
  }

  async function approveRequest(request, approvedScope) {
    const id = request.approvalRequestId;
    busy.add(id);
    render();
    const response = await api.postDetailed(`/api/workbench/agent-requests/${encodeURIComponent(id)}/approve`, {
      humanConfirmed: true,
      approvedScope,
      grantDurationSeconds: agentRequestGrantDurationSeconds(request),
    });
    busy.delete(id);
    if (!response.ok || !response.body?.ok) {
      setStatus(response.body?.error?.message || response.body?.error || response.error || 'Agent request approval failed.');
    } else {
      setStatus(response.body.message || 'Agent request approved.');
      confirmations.delete(id);
    }
    await refresh(true);
  }

  async function rejectRequest(request) {
    const id = request.approvalRequestId;
    busy.add(id);
    render();
    const response = await api.postDetailed(`/api/workbench/agent-requests/${encodeURIComponent(id)}/reject`, {
      humanConfirmed: true,
      reasonCode: 'human-rejected',
    });
    busy.delete(id);
    if (!response.ok || !response.body?.ok) {
      setStatus(response.body?.error?.message || response.body?.error || response.error || 'Agent request rejection failed.');
    } else {
      setStatus(response.body.message || 'Agent request rejected.');
      confirmations.delete(id);
    }
    await refresh(true);
  }

  async function refresh(force = false) {
    if (stopped) return;
    if (!force && panel.contains?.(doc.activeElement)) return;
    const response = await api.getDetailed('/api/workbench/agent-requests');
    if (!response.ok || !response.body?.ok) {
      if (requests.length === 0) render();
      return;
    }
    const nextRequests = Array.isArray(response.body.requests) ? response.body.requests : [];
    const nextAppName = response.body.appName || null;
    const nextPlatform = response.body.platform || null;
    const nextFingerprint = JSON.stringify({
      appName: nextAppName,
      platform: nextPlatform,
      requests: nextRequests,
    });
    if (!force && nextFingerprint === responseFingerprint) return;
    for (const request of nextRequests) {
      const id = request.approvalRequestId;
      const state = request.state;
      if (!id || !state) continue;
      const previous = knownStates.get(id);
      if (previous === undefined && state === 'pending') {
        onTransition('agent-requested', { approvalRequestId: id, durationMs: 0 });
        setStatus('An agent request is waiting in Tests > Agent requests.');
      } else if (previous && previous !== state) {
        const createdAt = Date.parse(request.createdAt || '');
        onTransition(`agent-${state}`, {
          approvalRequestId: id,
          durationMs: Number.isFinite(createdAt) ? Math.max(0, Date.now() - createdAt) : null,
        });
      }
      knownStates.set(id, state);
    }
    responseFingerprint = nextFingerprint;
    requests = nextRequests;
    appName = nextAppName;
    platform = nextPlatform;
    render();
  }

  function start() {
    if (timer || stopped) return;
    refresh();
    timer = win.setInterval(refresh, 2000);
  }

  async function openRequest(approvalRequestId = null) {
    openPanel();
    await refresh(true);
    const card = approvalRequestId
      ? [...body.querySelectorAll('[data-approval-request-id]')]
        .find((candidate) => candidate.dataset.approvalRequestId === approvalRequestId)
      : null;
    if (approvalRequestId && !card) {
      setStatus('That agent request is no longer available. Review recent decisions or wait for a fresh request.');
    }
    const focusTarget = card?.querySelector('input, button, summary') ||
      body.querySelector('input, button, summary');
    card?.scrollIntoView?.({ block: 'nearest' });
    focusTarget?.focus?.({ preventScroll: true });
  }

  function stop() {
    stopped = true;
    if (timer) win.clearInterval(timer);
    timer = null;
  }

  return Object.freeze({
    start,
    stop,
    refresh,
    pendingCount: () => requests.filter((request) => request.state === 'pending').length,
    open: openRequest,
  });
}
