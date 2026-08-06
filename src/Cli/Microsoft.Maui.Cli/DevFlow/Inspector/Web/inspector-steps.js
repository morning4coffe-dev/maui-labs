function clone(value) {
  return value == null ? value : JSON.parse(JSON.stringify(value));
}

function list(value) {
  return Array.isArray(value) ? value : [];
}

let selectedReviewStepKey = null;

function stepKey(step, index) {
  return String(step?.stepId || step?.seq || index + 1);
}

function resequence(flow) {
  flow.steps = list(flow.steps).map((step, index) => ({ ...step, seq: index + 1 }));
  return flow;
}

export function moveFlowStep(flow, index, offset) {
  const next = clone(flow);
  const target = index + offset;
  if (!Array.isArray(next?.steps) || index < 0 || index >= next.steps.length ||
      target < 0 || target >= next.steps.length) return next;
  [next.steps[index], next.steps[target]] = [next.steps[target], next.steps[index]];
  return resequence(next);
}

export function removeFlowStep(flow, index) {
  const next = clone(flow);
  if (!Array.isArray(next?.steps) || index < 0 || index >= next.steps.length) return next;
  next.steps.splice(index, 1);
  return resequence(next);
}

function el(tag, props = {}, children = []) {
  const node = document.createElement(tag);
  for (const [name, value] of Object.entries(props)) {
    if (value == null) continue;
    if (name === 'className') node.className = value;
    else if (name === 'text') node.textContent = value;
    else if (name === 'value') node.value = value;
    else if (name === 'checked') node.checked = !!value;
    else if (name === 'disabled') node.disabled = !!value;
    else node.setAttribute(name, String(value));
  }
  for (const child of children) node.append(child);
  return node;
}

function button(text, onClick, options = {}) {
  const node = el('button', {
    className: `df-workbench-action ${options.primary ? 'df-authoring-primary' : ''}`,
    type: 'button',
    text,
    disabled: options.disabled,
    title: options.title,
  });
  node.addEventListener('click', onClick);
  return node;
}

function field(label, control, hint = null) {
  const node = el('label', { className: 'df-authoring-field' });
  node.append(el('span', { className: 'df-authoring-field-label', text: label }), control);
  if (hint) node.append(el('span', { className: 'df-authoring-field-hint', text: hint }));
  return node;
}

function input(value, onInput, options = {}) {
  const node = el('input', {
    className: 'df-authoring-input',
    type: options.type || 'text',
    value: value ?? '',
    placeholder: options.placeholder,
    maxlength: options.maxlength || 4096,
  });
  node.addEventListener('input', () => onInput(node.value));
  return node;
}

function textarea(value, onInput, options = {}) {
  const node = el('textarea', { className: 'df-authoring-textarea', rows: options.rows || 2, maxlength: options.maxlength || 8192 });
  node.value = value ?? '';
  node.addEventListener('input', () => onInput(node.value));
  return node;
}

function parseFlow(markdown) {
  if (typeof markdown !== 'string') return null;
  const match = /```json maui-test\s*\r?\n([\s\S]*?)\r?\n```/.exec(markdown);
  if (!match) return null;
  try { return JSON.parse(match[1]); } catch { return null; }
}

function replaceFlowPayload(markdown, flow) {
  const payload = JSON.stringify(flow, null, 2);
  const expression = /(```json maui-test\s*\r?\n)[\s\S]*?(\r?\n```)/;
  if (typeof markdown === 'string' && expression.test(markdown))
    return markdown.replace(expression, `$1${payload}$2`);
  return `# Scenario: ${flow.name || 'scenario'}\n\n\`\`\`json maui-test\n${payload}\n\`\`\`\n`;
}

function effectiveSelector(step) {
  const selector = step?.args?.selector;
  return selector && Object.keys(selector).length ? selector : step?.target;
}

function selectorKind(selector) {
  if (!selector) return 'none';
  if (selector.automationId) return 'automationId';
  if (selector.text) return 'text';
  if (selector.typeIndex || selector.selectorKind === 'typeIndex') return 'typeIndex';
  if (selector.id) return 'runtimeId';
  return 'none';
}

export function isStrictAuthoringSelector(selector) {
  if (!selector || typeof selector !== 'object') return false;
  const forms = (selector.automationId ? 1 : 0) +
    (selector.text ? 1 : 0) +
    (selector.typeIndex || selector.selectorKind === 'typeIndex' ? 1 : 0) +
    (selector.id ? 1 : 0);
  return forms === 1 && !!(selector.automationId || selector.text || selector.typeIndex) &&
    selector.matchCount !== 0 && selector.matchCount !== undefined &&
    selector.matchCount !== null && selector.matchCount === 1 &&
    selector.quality !== 'ambiguous';
}

export function isObservationOnlyAssertion(kind) {
  return kind === 'pageChanged';
}

function selectorLabel(selector) {
  if (!selector) return 'No selector';
  if (selector.automationId) return `AutomationId: ${selector.automationId}`;
  if (selector.text) return `Text: ${selector.text}`;
  const typeIndex = selector.typeIndex || selector;
  if (typeIndex.type) return `Type index: ${typeIndex.type}[${typeIndex.index ?? 0}]`;
  if (selector.id) return 'Legacy runtime ID (not editable)';
  return 'No selector';
}

function looksSensitive(...values) {
  return values.filter(Boolean).join(' ').toLowerCase().match(/password|passcode|secret|token|apikey|api[_-]?key|credential|authorization|cookie|private|pin|otp|cvv|ssn/);
}

function makeSelectorFromSelected(selected) {
  if (!selected) return null;
  if (selected.automationId) return { automationId: selected.automationId };
  // Never copy live text from a selected control into a draft assertion. A text fallback can
  // accidentally persist an Entry/Editor value, so the composer requires an AutomationId.
  return null;
}

function details(parent, title, value) {
  const row = el('div', { className: 'df-step-detail' });
  row.append(el('strong', { text: `${title}: ` }), el('span', { text: value }));
  parent.append(row);
}

function findings(parent, values, type) {
  if (!list(values).length) return;
  const host = el('section', { className: `df-authoring-findings df-authoring-findings-${type}` });
  const lines = el('ul');
  for (const value of values) lines.append(el('li', { text: String(value) }));
  host.append(lines);
  parent.append(host);
}

function applyFlow(authoring, draft, flow, rerender = true) {
  authoring.update?.({
    flow,
    markdown: replaceFlowPayload(draft.markdown, flow),
    flowDigest: null,
    flowDirty: true,
    stale: false,
    errors: [],
    warnings: [],
  }, rerender);
}

function renderSelectorEditor(card, flow, stepIndex, draft, authoring) {
  const current = effectiveSelector(flow.steps[stepIndex]);
  const kind = selectorKind(current);
  const host = el('details', { className: 'df-selector-editor' });
  host.append(el('summary', { text: 'Selector (advanced)' }));
  const select = el('select', { className: 'df-authoring-select' });
  for (const [value, text] of [
    ['automationId', 'AutomationId'],
    ['text', 'Exact text'],
    ['typeIndex', 'Type + index (fragile)'],
  ]) select.append(el('option', { value, text }));
  select.value = kind === 'runtimeId' || kind === 'none' ? 'automationId' : kind;
  const value = input(
    current?.automationId || current?.text || current?.typeIndex?.type || current?.type || '',
    () => {},
    { placeholder: select.value === 'typeIndex' ? 'Button' : 'Identifier or exact text' }
  );
  const index = input(
    current?.typeIndex?.index ?? current?.index ?? 0,
    () => {},
    { type: 'number', placeholder: '0' }
  );
  const indexField = field('Index', index, 'Only used for Type + index.');
  const updateVisibility = () => {
    indexField.hidden = select.value !== 'typeIndex';
    value.placeholder = select.value === 'typeIndex' ? 'Button' :
      select.value === 'text' ? 'Exact visible text' : 'AutomationId';
  };
  select.addEventListener('change', updateVisibility);
  updateVisibility();
  host.append(field('Selector type', select), field('Selector value', value), indexField);
  host.append(button('Validate and apply selector', async () => {
    const candidate = select.value === 'automationId'
      ? { automationId: value.value.trim() }
      : select.value === 'text'
        ? { text: value.value }
        : { typeIndex: { type: value.value.trim(), index: Math.max(0, Number(index.value) || 0) } };
    if (!(candidate.automationId || candidate.text || candidate.typeIndex?.type)) {
      authoring.message?.('Provide a durable selector value before validating.');
      return;
    }
    const result = await authoring.verifySelector?.(candidate);
    if (!result || result.ok !== true || result.matchCount !== 1) {
      authoring.message?.((result && result.error) || 'Selector did not resolve exactly one element.');
      return;
    }
    const next = clone(flow);
    const step = next.steps[stepIndex];
    candidate.matchCount = result.matchCount;
    candidate.quality = result.quality || (candidate.automationId ? 'durable' : 'fragile');
    if (step.args?.selector) step.args.selector = candidate;
    else step.target = candidate;
    step.fragile = !candidate.automationId;
    applyFlow(authoring, draft, next);
    authoring.message?.('Selector verified with exactly one match and applied to the draft.');
  }, { primary: true }));
  card.append(host);
}

function renderAssertionComposer(parent, flow, draft, authoring, options = {}) {
  const host = el('section', { className: 'df-authoring-section' });
  host.append(el('h4', { text: 'Add expected result' }));
  host.append(el('p', {
    className: 'df-authoring-section-hint',
    text: 'Describe one observable result that proves the selected step worked.',
  }));
  const selected = authoring.selectedElement?.();
  const fixedStepIndex = Number.isInteger(options.stepIndex) ? options.stepIndex : null;
  const stepSelector = fixedStepIndex == null ? null : effectiveSelector(flow.steps[fixedStepIndex]);
  const selectedSelector = makeSelectorFromSelected(selected) ||
    (stepSelector?.automationId ? { automationId: stepSelector.automationId } : null);
  const kind = el('select', { className: 'df-authoring-select' });
  for (const [value, text] of [
    ['exists', 'The step target exists'],
    ['propEquals', 'A target property equals a value'],
    ['routeIs', 'The current route equals a value'],
    ['pageChanged', 'The page changed (observation only)'],
  ]) kind.append(el('option', { value, text }));
  const target = fixedStepIndex == null ? el('select', { className: 'df-authoring-select' }) : null;
  if (target) {
    list(flow.steps).forEach((step, index) =>
      target.append(el('option', { value: String(index), text: `${step.seq || index + 1}. ${step.label || step.action}` })));
  }
  const property = input('Text', () => {}, { placeholder: 'Text' });
  // The selected element supplies only a durable selector and property default. Never copy its
  // current value into an authored assertion: an apparently ordinary Entry can still hold a secret.
  const expected = input('', () => {}, { placeholder: 'Expected value (never prefilled)' });
  const note = textarea('', () => {}, { rows: 2 });
  const selectorHint = el('p', {
    className: 'df-workbench-note',
    text: selectedSelector
      ? `Result target: ${selectorLabel(selectedSelector)}`
      : 'Select an element with an AutomationId before adding a target result.',
  });
  const updateKind = () => {
    const elementAssertion = kind.value === 'exists' || kind.value === 'propEquals';
    property.closest('label')?.toggleAttribute('hidden', kind.value !== 'propEquals');
    expected.closest('label')?.toggleAttribute('hidden', kind.value !== 'propEquals' && kind.value !== 'routeIs');
    selectorHint.hidden = !elementAssertion;
  };
  kind.addEventListener('change', updateKind);
  host.append(field('Expected result', kind));
  if (target) host.append(field('After step', target));
  host.append(
    selectorHint,
    field('Property', property),
    field('Expected value', expected),
    field('Observation note', note),
  );
  updateKind();
  const status = el('p', { className: 'df-workbench-status' });
  host.append(status);
  host.append(button('Check current app now (optional)', async () => {
    const assertion = buildAssertion();
    if (!assertion) return;
    const result = await authoring.verifyAssertion?.(assertion);
    status.textContent = !result
      ? 'Verification is unavailable.'
      : result.observationOnly
        ? 'This is an observation only and does not unlock Run.'
        : result.passed === true
          ? `Verification passed${result.matchCount ? ` with ${result.matchCount} unique match` : ''}.`
          : `Verification did not pass${result.error ? `: ${result.error}` : '.'}`;
  }));
  host.append(button('Add expected result', async () => {
    const assertion = buildAssertion();
    if (!assertion) return;
    if (assertion.kind !== 'routeIs' && assertion.kind !== 'pageChanged') {
      const checked = await authoring.verifySelector?.(assertion.selector);
      if (!checked || checked.ok !== true || checked.matchCount !== 1) {
        status.textContent = (checked && checked.error) || 'The selected selector is not uniquely resolvable.';
        return;
      }
      assertion.selector.matchCount = checked.matchCount;
      assertion.selector.quality = checked.quality || (assertion.selector.automationId ? 'durable' : 'fragile');
    }
    const next = clone(flow);
    const index = fixedStepIndex == null
      ? Math.max(0, Math.min(next.steps.length - 1, Number(target?.value) || 0))
      : Math.max(0, Math.min(next.steps.length - 1, fixedStepIndex));
    next.steps[index].asserts = list(next.steps[index].asserts);
    next.steps[index].asserts.push(assertion);
    applyFlow(authoring, draft, next);
    authoring.noteAssertionAdded?.(assertion);
    status.textContent = assertion.verify ? 'Expected result added to the draft.' : 'Observation note added to the draft.';
  }, { primary: true, disabled: !flow.steps.length }));

  function buildAssertion() {
    const result = { kind: kind.value, verify: kind.value !== 'pageChanged' };
    if (kind.value === 'exists') {
      if (!selectedSelector) {
        status.textContent = 'Select an element with a durable selector before adding this result.';
        return null;
      }
      result.selector = selectedSelector;
    } else if (kind.value === 'propEquals') {
      if (!selectedSelector) {
        status.textContent = 'Select an element with a durable selector before adding this result.';
        return null;
      }
      if (looksSensitive(property.value, selected?.automationId, selected?.text)) {
        status.textContent = 'Sensitive values are not persisted or used as property assertions.';
        return null;
      }
      result.selector = selectedSelector;
      result.name = property.value.trim() || 'Text';
      result.expected = expected.value;
    } else if (kind.value === 'routeIs') {
      result.expected = expected.value.trim();
      if (!result.expected) {
        status.textContent = 'Provide an expected route.';
        return null;
      }
    } else {
      result.note = note.value.trim() || 'Page change observed.';
    }
    return result;
  }
  parent.append(host);
}

function expectedResultLabel(assertion) {
  if (assertion?.kind === 'exists') return 'Target exists';
  if (assertion?.kind === 'propEquals')
    return `${assertion.name || 'Property'} equals ${assertion.expected ?? '(empty)'}`;
  if (assertion?.kind === 'routeIs') return `Route equals ${assertion.expected || '(empty)'}`;
  if (assertion?.kind === 'pageChanged') return assertion.note || 'Page changed';
  return assertion?.kind || 'Expected result';
}

function renderReviewEditor(root, flow, draft, authoring, helpers) {
  root.classList.add('df-review-workspace');
  const steps = list(flow.steps);
  const requestedIndex = steps.findIndex((step, index) => stepKey(step, index) === selectedReviewStepKey);
  const selectedIndex = requestedIndex >= 0 ? requestedIndex : steps.length ? 0 : -1;
  if (selectedIndex >= 0 && selectedReviewStepKey == null)
    selectedReviewStepKey = stepKey(steps[selectedIndex], selectedIndex);
  const layout = el('div', { className: 'df-review-layout' });
  const rail = el('div', {
    className: 'df-review-step-list',
    role: 'listbox',
    'aria-label': 'Recorded steps',
  });

  for (let index = 0; index < steps.length; index++) {
    const step = steps[index];
    const key = stepKey(step, index);
    const selected = index === selectedIndex;
    const row = el('button', {
      className: `df-review-step-row${selected ? ' df-selected' : ''}`,
      type: 'button',
      role: 'option',
      'aria-selected': String(selected),
    });
    row.append(
      el('span', { className: 'df-review-step-number', text: String(index + 1) }),
      el('span', { className: 'df-review-step-title', text: step.label || step.action || 'Step' }),
      el('span', {
        className: 'df-review-step-summary',
        text: `${step.action || 'action'} · ${list(step.asserts).filter((assertion) => assertion?.verify !== false).length} expected result${list(step.asserts).filter((assertion) => assertion?.verify !== false).length === 1 ? '' : 's'}`,
      }),
    );
    row.addEventListener('click', () => {
      selectedReviewStepKey = key;
      helpers.rerender?.();
    });
    rail.append(row);
  }
  layout.append(rail);

  const detail = el('section', { className: 'df-review-step-editor' });
  if (selectedIndex < 0) {
    detail.append(
      el('h4', { text: 'Select a step' }),
      el('p', {
        className: 'df-workbench-intro',
        text: 'Choose a recorded step to edit, reorder, remove, or add its expected result.',
      }),
    );
    layout.append(detail);
    root.append(layout);
    return;
  }

  const step = steps[selectedIndex];
  detail.append(el('h4', { text: `Step ${selectedIndex + 1}: ${step.label || step.action || 'Step'}` }));
  details(detail, 'Recorded action', step.action || 'unknown');
  details(detail, 'Target', selectorLabel(effectiveSelector(step)));

  const editActions = el('div', { className: 'df-authoring-actions' });
  editActions.append(
    button('Move up', () => {
      const next = moveFlowStep(flow, selectedIndex, -1);
      const nextIndex = Math.max(0, selectedIndex - 1);
      selectedReviewStepKey = stepKey(next.steps[nextIndex], nextIndex);
      applyFlow(authoring, draft, next);
    }, { disabled: selectedIndex === 0 }),
    button('Move down', () => {
      const next = moveFlowStep(flow, selectedIndex, 1);
      const nextIndex = Math.min(next.steps.length - 1, selectedIndex + 1);
      selectedReviewStepKey = stepKey(next.steps[nextIndex], nextIndex);
      applyFlow(authoring, draft, next);
    }, { disabled: selectedIndex === steps.length - 1 }),
    button('Remove step', () => {
      const next = removeFlowStep(flow, selectedIndex);
      const nextIndex = Math.min(selectedIndex, next.steps.length - 1);
      selectedReviewStepKey = nextIndex >= 0 ? stepKey(next.steps[nextIndex], nextIndex) : null;
      applyFlow(authoring, draft, next);
    }),
  );
  detail.append(editActions);

  const results = el('section', { className: 'df-review-expected-results' });
  results.append(el('h5', { text: 'Expected results' }));
  const assertions = list(step.asserts);
  if (!assertions.length) {
    results.append(el('p', {
      className: 'df-workbench-safety',
      text: 'Add one expected result to unlock Run.',
    }));
  } else {
    for (let assertionIndex = 0; assertionIndex < assertions.length; assertionIndex++) {
      const assertion = assertions[assertionIndex];
      const row = el('div', { className: 'df-review-expected-result' });
      row.append(
        el('span', {
          text: `${assertion.verify === false ? 'Observation' : 'Verified result'}: ${expectedResultLabel(assertion)}`,
        }),
        button('Remove', () => {
          const next = clone(flow);
          next.steps[selectedIndex].asserts = list(next.steps[selectedIndex].asserts);
          next.steps[selectedIndex].asserts.splice(assertionIndex, 1);
          applyFlow(authoring, draft, next);
        }),
      );
      results.append(row);
    }
  }
  const addResult = el('details', { className: 'df-expected-result-editor' });
  addResult.append(el('summary', { text: 'Add expected result' }));
  addResult.open = !assertions.some((assertion) => assertion?.verify !== false);
  renderAssertionComposer(addResult, flow, draft, authoring, { stepIndex: selectedIndex });
  results.append(addResult);
  detail.append(results);

  const stepDetails = el('details', { className: 'df-review-step-details' });
  stepDetails.append(el('summary', { text: 'Step details (optional)' }));
  const label = input(step.label, () => {}, { placeholder: step.action || 'Step name' });
  const intent = textarea(step.intent, () => {}, { rows: 2 });
  stepDetails.append(
    field('Step name', label, 'Optional label shown in the step list.'),
    field('Purpose', intent, 'Optional plain-language explanation.'),
    button('Save step', () => {
      const next = clone(flow);
      next.steps[selectedIndex].label = label.value.trim() || undefined;
      next.steps[selectedIndex].intent = intent.value.trim() || undefined;
      applyFlow(authoring, draft, next);
    }, { primary: true }),
  );
  renderSelectorEditor(stepDetails, flow, selectedIndex, draft, authoring);
  detail.append(stepDetails);
  layout.append(detail);
  root.append(layout);
}

export function renderStepsPanel(helpers) {
  const stage = helpers.workbenchState?.().selectedStage || 'record';
  const reviewing = stage === 'review';
  const root = helpers.root(reviewing ? 'Review' : 'Steps');
  const authoring = helpers.authoring;
  if (!authoring || typeof authoring.state !== 'function') {
    helpers.intro(root, 'Add a Goal before recording steps.');
    return root;
  }
  const draft = authoring.state();
  const readiness = draft.readiness || {};
  const flow = clone(draft.flow || parseFlow(draft.markdown));
  const hasSteps = !!flow && list(flow.steps).length > 0;

  if (!readiness.goal && !draft.recording) {
    helpers.intro(root, 'Add the Goal first. Steps become available after the test has an outcome to prove.');
    const actions = el('div', { className: 'df-authoring-actions' });
    actions.append(button('Go to Goal', () => helpers.focusGoal?.(), { primary: true }));
    root.append(actions);
    helpers.safety(root);
    return root;
  }

  if (!reviewing && draft.recording) {
    helpers.intro(root, draft.appendingRecording
      ? 'Recording more steps. Interact with the app, then stop to append them to the draft.'
      : 'Recording is active. Interact with the app, then stop when the required steps are captured.');
    const actions = el('div', { className: 'df-authoring-actions' });
    actions.append(
      button('Stop recording', () => authoring.stopRecording?.(), {
        primary: true,
        disabled: !draft.canDrive || !!draft.recordingStopping,
      }),
      button('Discard recording', () => authoring.cancelRecording?.(), {
        disabled: !draft.canDrive || !!draft.recordingStopping,
      }),
    );
    root.append(actions, el('p', {
      className: 'df-workbench-state',
      text: `${draft.recordingSteps || 0} step${draft.recordingSteps === 1 ? '' : 's'} captured`,
    }));
    helpers.safety(root);
    return root;
  }

  findings(root, draft.errors, 'error');
  findings(root, draft.warnings, 'warning');
  if (draft.saving) root.append(el('p', { className: 'df-workbench-note', text: 'Saving test…' }));

  if (!reviewing && !hasSteps) {
    helpers.intro(root, 'Record the app interactions that prove the Goal. Recording does not start a test run.');
    const actions = el('div', { className: 'df-authoring-actions' });
    actions.append(button('Start recording', () => authoring.startRecording?.(), {
      primary: true,
      disabled: !draft.canDrive || !!draft.recordingStopping,
    }));
    root.append(actions);
    helpers.safety(root);
    return root;
  }

  if (reviewing && !hasSteps) {
    helpers.intro(root, 'There are no steps to review yet.');
    const actions = el('div', { className: 'df-authoring-actions' });
    actions.append(button('Go to Steps', () => helpers.selectStage?.('record'), { primary: true }));
    root.append(actions);
    helpers.safety(root);
    return root;
  }

  if (!reviewing) {
    helpers.intro(root, `${flow.steps.length} recorded step${flow.steps.length === 1 ? '' : 's'}. Select one to edit it in Review.`);
    const actions = el('div', { className: 'df-authoring-actions' });
    actions.append(button('Continue to Review', () => helpers.selectStage?.('review'), { primary: true }));
    root.append(actions);
    const timeline = el('div', {
      className: 'df-review-step-list df-steps-summary-list',
      role: 'list',
      'aria-label': 'Captured steps',
    });
    for (let index = 0; index < flow.steps.length; index++) {
      const step = flow.steps[index];
      const verified = list(step.asserts).filter((assertion) => assertion?.verify !== false).length;
      const row = el('button', {
        className: 'df-review-step-row',
        type: 'button',
        role: 'listitem',
      });
      row.append(
        el('span', { className: 'df-review-step-number', text: String(index + 1) }),
        el('span', { className: 'df-review-step-title', text: step.label || step.action || 'Step' }),
        el('span', {
          className: 'df-review-step-summary',
          text: `${step.action || 'action'} · ${verified} expected result${verified === 1 ? '' : 's'}`,
        }),
      );
      row.addEventListener('click', () => {
        selectedReviewStepKey = stepKey(step, index);
        helpers.selectStage?.('review');
      });
      timeline.append(row);
    }
    root.append(timeline);

    const options = el('details', { className: 'df-recording-options' });
    options.append(el('summary', { text: 'Recording options' }));
    const optionActions = el('div', { className: 'df-authoring-actions' });
    optionActions.append(
      button('Start a new recording', () => authoring.startRecording?.(), { disabled: !draft.canDrive }),
      button('Download recording draft', () => authoring.downloadRecordingDraft?.()),
    );
    if (draft.workspaceAvailable === false || draft.rawDraft) {
      optionActions.append(button('Save draft with host', () => authoring.saveRecordingDraftFallback?.(), {
        title: 'Uses the host save fallback when available; otherwise downloads the Markdown draft.',
      }));
    }
    options.append(optionActions);
    root.append(options);
    helpers.safety(root);
    return root;
  }

  helpers.intro(root, draft.bindingStale
    ? 'The recorded steps changed after this plan was saved. Review them, then save the updated test.'
    : readiness.savedBundle && readiness.hardOutcomeCheck
      ? 'Review the recorded steps. This saved test is ready to run.'
      : readiness.hardOutcomeCheck
        ? 'Review the recorded steps, then check and save the test.'
        : 'Review the recorded steps and add one expected result before saving.');

  const reviewActions = el('div', { className: 'df-authoring-actions df-review-toolbar' });
  reviewActions.append(button('Record more steps', () => authoring.startAppendingRecording?.(), {
    disabled: !draft.canDrive,
  }));
  root.append(reviewActions);
  renderReviewEditor(root, flow, draft, authoring, helpers);

  const actions = el('div', { className: 'df-authoring-actions df-review-actions' });
  if (readiness.savedBundle && readiness.hardOutcomeCheck && !draft.flowDirty && !draft.planDirty) {
    actions.append(button('Continue to Run', () => helpers.selectStage?.('run'), { primary: true }));
  } else if (readiness.hardOutcomeCheck) {
    actions.append(button('Check test', () => authoring.validateFlow?.(), { disabled: !!draft.saving }));
    if (draft.flowDirty || draft.planDirty || draft.diff) {
      actions.append(button('Review changes', () => authoring.diffFlow?.(), { disabled: !!draft.saving }));
    }
    actions.append(button(
      draft.stale ? 'Overwrite saved test' : draft.bindingStale ? 'Save updated test' : 'Save test',
      () => authoring.commitBundle?.(draft.stale === true), {
      primary: true,
      disabled: !!draft.saving,
      title: 'Saves the flow and managed plan together. Saving never starts a run.',
    }));
  }
  if (actions.childElementCount) root.append(actions);

  if (draft.diff) {
    const diff = el('details', { className: 'df-authoring-diff', open: true });
    diff.append(el('summary', { text: 'Changes to save' }), el('pre', { text: draft.diff }));
    root.append(diff);
  }
  if ((draft.errors?.length || draft.workspaceAvailable === false) && draft.markdown) {
    const recovery = el('details', { className: 'df-review-recovery' });
    recovery.append(el('summary', { text: 'Draft recovery' }));
    const recoveryActions = el('div', { className: 'df-authoring-actions' });
    recoveryActions.append(button('Download current draft', () => authoring.downloadRecordingDraft?.()));
    recovery.append(recoveryActions);
    root.append(recovery);
  }
  helpers.safety(root);
  return root;
}
