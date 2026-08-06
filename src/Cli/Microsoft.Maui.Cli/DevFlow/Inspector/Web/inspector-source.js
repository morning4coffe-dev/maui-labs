export function renderSourceProposalPanel(helpers) {
  const root = helpers.root('Source');
  const source = helpers.source;
  const state = source?.state?.() || {};
  const language = state.language === 'CSharp' ? 'CSharp' : 'Xaml';
  const languageLabel = language === 'CSharp' ? 'C#' : 'XAML';
  const snapshot = state.proposal;
  const proposal = snapshot?.proposal || snapshot || null;
  const eligibility = state.eligibility || proposal?.eligibility || state.preview?.eligibility || null;
  const selected = state.selectedElement;
  const lifecycle = snapshot?.state || proposal?.state || null;
  const host = state.hostCapability || {};
  const hostCanApply = language === 'CSharp' ? host.canApplyCSharpSource : host.canApplySource;
  const canMutate = state.canMutate === true;

  if (!selected?.id)
    helpers.intro(root, 'Select a source-mapped control to add a durable AutomationId.');
  helpers.safety(root);

  if (state.error) {
    const error = document.createElement('p');
    error.className = 'df-workbench-safety';
    error.textContent = state.error;
    root.append(error);
  }

  if (!selected?.id) {
    const empty = document.createElement('section');
    empty.className = 'df-authoring-section df-tool-empty-state';
    const heading = document.createElement('h4');
    heading.textContent = 'Select a control first';
    const message = document.createElement('p');
    message.className = 'df-workbench-intro';
    message.textContent = 'Select a control in the app preview or element tree, then return here to improve its testability.';
    empty.append(heading, message);
    const inspect = helpers.action(empty, 'Inspect app control', () => helpers.inspectApp?.());
    inspect.classList.add('df-authoring-primary');
    root.append(empty);

    const rules = document.createElement('details');
    rules.className = 'df-tool-details';
    const summary = document.createElement('summary');
    summary.textContent = 'What Source can change';
    rules.append(summary);
    helpers.list(rules, [
      'One direct static AutomationId literal on the selected control.',
      'No flow selector, action, check, template, style, or running app value.',
      'C# changes require a Roslyn-proven selection and an explicit IDE-host apply.',
    ]);
    root.append(rules);
    return root;
  }

  if (!proposal) {
    const setup = document.createElement('section');
    setup.className = 'df-authoring-section df-tool-ready-state';
    const setupHeading = document.createElement('h4');
    setupHeading.textContent = `Improve ${selected.type || 'selected control'}`;
    const selection = document.createElement('p');
    selection.className = 'df-workbench-note';
    selection.textContent = `${languageLabel} source mapping will be checked before any proposal is created.`;
    setup.append(setupHeading, selection);

    const languageField = document.createElement('label');
    languageField.className = 'df-workbench-field';
    languageField.textContent = 'Source language';
    const languageSelect = document.createElement('select');
    for (const [label, value] of [['XAML', 'Xaml'], ['C# (Roslyn-proven)', 'CSharp']]) {
      const option = document.createElement('option');
      option.textContent = label;
      option.value = value;
      languageSelect.append(option);
    }
    languageSelect.value = language;
    languageSelect.addEventListener('change', () => source?.setLanguage?.(languageSelect.value));
    languageField.append(languageSelect);
    const sourceOptions = document.createElement('details');
    sourceOptions.className = 'df-tool-details df-source-options';
    const sourceOptionsSummary = document.createElement('summary');
    sourceOptionsSummary.textContent = `Source options · ${languageLabel}`;
    sourceOptions.append(sourceOptionsSummary, languageField);
    setup.append(sourceOptions);

    const inputLabel = document.createElement('label');
    inputLabel.className = 'df-workbench-field';
    inputLabel.textContent = 'New AutomationId';
    const input = document.createElement('input');
    input.type = 'text';
    input.maxLength = 128;
    input.autocomplete = 'off';
    input.value = state.proposedAutomationId || '';
    input.placeholder = 'SaveButton';
    inputLabel.append(input);
    setup.append(inputLabel);
    const analyze = helpers.action(setup, state.analyzing ? 'Checking source…' : 'Check source', () => source?.analyze?.());
    analyze.classList.add('df-authoring-primary');
    analyze.disabled = !source || state.analyzing || !String(state.proposedAutomationId || '').trim();
    input.addEventListener('input', () => {
      source?.setProposedAutomationId?.(input.value);
      analyze.disabled = !source || state.analyzing || !input.value.trim();
    });
    root.append(setup);
  }

  if (eligibility) {
    const reasons = Array.isArray(eligibility.reasons) ? eligibility.reasons : [];
    if (eligibility.eligible !== true) {
      const unavailable = document.createElement('section');
      unavailable.className = 'df-authoring-section df-tool-empty-state';
      const heading = document.createElement('h4');
      heading.textContent = 'This control cannot use a safe source proposal';
      const message = document.createElement('p');
      message.className = 'df-workbench-intro';
      message.textContent = reasons[0]?.message || 'The selected declaration is not a supported direct static AutomationId location.';
      unavailable.append(heading, message);
      root.append(unavailable);
    } else if (!snapshot) {
      const ready = document.createElement('section');
      ready.className = 'df-authoring-section df-tool-ready-state';
      const heading = document.createElement('h4');
      heading.textContent = 'Source change is eligible';
      const message = document.createElement('p');
      message.className = 'df-workbench-intro';
      message.textContent = 'Create an inert proposal to review the exact patch.';
      ready.append(heading, message);
      const propose = helpers.action(ready, state.proposing ? 'Creating proposal…' : 'Create source proposal', () => source?.propose?.());
      propose.classList.add('df-authoring-primary');
      propose.disabled = !source || state.proposing;
      root.append(ready);
    }
    if (reasons.length) {
      const reasonDetails = document.createElement('details');
      reasonDetails.className = 'df-tool-details';
      const summary = document.createElement('summary');
      summary.textContent = 'Eligibility details';
      reasonDetails.append(summary);
      helpers.list(reasonDetails, reasons.map((reason) =>
        `${reason.code || 'ineligible'}: ${reason.message || 'No explanation supplied.'}`));
      root.append(reasonDetails);
    }
  }

  if (proposal) {
    const operation = proposal.operation || {};
    const element = proposal.element || {};
    const uniqueness = proposal.uniqueness || {};
    const proposalCard = document.createElement('section');
    proposalCard.className = 'df-authoring-section df-tool-ready-state';
    const heading = document.createElement('h4');
    heading.textContent = `Source proposal · ${String(lifecycle || 'proposed').replace(/-/g, ' ')}`;
    proposalCard.append(heading);

    if (proposal.diff) {
      const label = document.createElement('h5');
      label.textContent = `Exact ${languageLabel} diff`;
      const diff = document.createElement('pre');
      diff.className = 'df-workbench-diff';
      diff.textContent = proposal.diff;
      proposalCard.append(label, diff);
    }

    if (lifecycle === 'proposed') {
      const preview = helpers.action(proposalCard, 'Preview exact change', () => source?.preview?.());
      preview.classList.add('df-authoring-primary');
    } else if (lifecycle === 'previewed') {
      const approve = helpers.action(proposalCard, 'Approve source change', () => source?.approve?.());
      approve.classList.add('df-authoring-primary');
      approve.disabled = !canMutate;
    } else if (lifecycle === 'approved' && hostCanApply) {
      const apply = helpers.action(
        proposalCard,
        language === 'CSharp' ? 'Apply in IDE' : 'Apply approved XAML change',
        () => source?.apply?.()
      );
      apply.classList.add('df-authoring-primary');
      apply.disabled = !snapshot?.grant || !canMutate;
    } else if (lifecycle === 'approved') {
      const download = helpers.action(proposalCard, 'Download approved patch', () => source?.downloadPatch?.());
      download.classList.add('df-authoring-primary');
    }

    if (proposal.diff) {
      helpers.action(proposalCard, 'Open diff', () => source?.openNativeDiff?.());
    }
    if (proposal.diff && ['approved', 'applying', 'applied', 'verified'].includes(lifecycle)) {
      helpers.action(proposalCard, 'Download patch', () => source?.downloadPatch?.());
    }
    if (['proposed', 'previewed'].includes(lifecycle)) helpers.action(proposalCard, 'Reject', () => source?.reject?.());
    if (lifecycle === 'rollback-required') {
      const rollback = helpers.action(proposalCard, 'Rollback source change', () => source?.rollback?.());
      rollback.classList.add('df-authoring-primary');
      rollback.disabled = !source || hostCanApply !== true || !canMutate;
    }
    root.append(proposalCard);

    const details = document.createElement('details');
    details.className = 'df-tool-details';
    const detailSummary = document.createElement('summary');
    detailSummary.textContent = 'Source change details';
    details.append(detailSummary);
    helpers.list(details, [
      `File: ${operation.fileRelativePath || 'unknown'}`,
      `Declaration: ${element.elementType || 'unknown'} line ${element.line || 'unknown'}`,
      `Change: ${operation.oldLiteral == null ? '(add)' : operation.oldLiteral} → ${operation.newLiteral || 'unknown'}`,
      `Project matches: ${uniqueness.projectMatchCount ?? 'unknown'}; live matches: ${uniqueness.liveScopeAvailable ? uniqueness.liveMatchCount : 'unavailable'}`,
      `Patch digest: ${proposal.patchDigest || 'unknown'}`,
    ]);
    const flows = Array.isArray(proposal.affectedFlows) ? proposal.affectedFlows : [];
    if (flows.length) {
      helpers.list(details, flows.map((flow) =>
        `${flow.flowPath || flow.flowId || 'flow'} needs a separate reviewed flow-selector update.`));
    }
    const platforms = Array.isArray(proposal.verification?.platforms) && proposal.verification.platforms.length
      ? proposal.verification.platforms
      : Array.isArray(proposal.affectedPlatforms) ? proposal.affectedPlatforms : [];
    if (platforms.length) {
      helpers.list(details, platforms.map((platform) =>
        `${platform.platform || platform.targetFramework || 'platform'}: build ${platform.buildState || 'pending'}; replay ${platform.replayState || 'pending'}.`));
    }
    if (!hostCanApply) {
      const hostLine = document.createElement('p');
      hostLine.className = 'df-workbench-note';
      hostLine.textContent = 'This host can review and download the patch but cannot apply it.';
      details.append(hostLine);
    }
    root.append(details);
  }

  const rules = document.createElement('details');
  rules.className = 'df-tool-details';
  const summary = document.createElement('summary');
  summary.textContent = 'How this stays safe';
  rules.append(summary);
  helpers.list(rules, [
    `Only one direct static ${languageLabel} AutomationId declaration can be changed.`,
    'Templates, styles, repeaters, generated files, bindings, WebViews, and dynamic construction are not eligible.',
    'Approval never changes a test selector; build and replay verification remain separate.',
  ]);
  helpers.capability(rules, language === 'CSharp' ? 'applyCSharpSourceProposal' : 'applySourceProposal');
  root.append(rules);
  return root;
}
