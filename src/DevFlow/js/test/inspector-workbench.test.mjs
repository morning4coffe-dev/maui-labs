import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const sourceRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const webRoot = resolve(sourceRoot, "Cli/Microsoft.Maui.Cli/DevFlow/Inspector/Web");
const read = (file) => readFileSync(resolve(webRoot, file), "utf8");
const readVscodeHost = () => readFileSync(
  resolve(sourceRoot, "DevFlow/js/vscode-inspector/src/extension.ts"),
  "utf8",
);

async function loadWorkbenchStateModule() {
  const source = read("inspector-workbench.js")
    .replace(/^import .*;\r?\n/gm, "");
  const stubs = [
    "const describeHostCapability = () => '';",
    "const renderPlanPanel = () => null;",
    "const renderStepsPanel = () => null;",
    "const renderRunPanel = () => null;",
    "const renderTracePanel = () => null;",
    "const renderRepairPanel = () => null;",
    "const renderImprovePanel = () => null;",
    "const renderSourceProposalPanel = () => null;",
  ].join("\n");
  return import(`data:text/javascript;base64,${Buffer.from(`${stubs}\n${source}`).toString("base64")}`);
}
async function loadPanelModule(file) {
  return import(`data:text/javascript;base64,${Buffer.from(read(file)).toString("base64")}`);
}

test("workbench shell separates the guided journey from contextual test tools", () => {
  const html = read("inspector.html");
  const css = read("devflow.css");
  const workbenchCss = read("inspector-workbench.css");

  assert.match(html, /id="df-toggle-workbench"[^>]*>.*?Tests/s);
  assert.match(html, /aria-label="Tests"/);
  assert.match(html, /class="df-tool-btn df-workbench-entry"/);
  assert.match(html, /id="df-workbench-tabs"[^>]*class="df-workbench-tabs"/);
  assert.match(html, /class="df-panel-tab-list df-workbench-stage-list"[^>]*role="tablist"[^>]*aria-label="Test workflow"/);
  assert.match(html, /id="df-workbench-advanced-tools"[^>]*class="df-workbench-tools"/);
  assert.match(html, /class="df-panel-tab-list df-workbench-tool-list"[^>]*role="tablist"[^>]*aria-label="Test tools"/);
  for (const stage of ["Goal", "Steps", "Review", "Run", "Results"]) {
    assert.match(html, new RegExp(`data-workbench-stage="[^"]+"[^>]*[\\s\\S]*?>${stage}<`));
  }
  for (const tab of ["requests", "repair", "improve", "source"]) {
    assert.match(html, new RegExp(`data-workbench-tab="${tab}"`));
  }
  assert.match(html, /Approve or reject actions requested by a test agent/);
  assert.match(html, /id="df-test-agent-request-badge"/);
  for (const tab of ["record", "review", "run", "results"]) {
    assert.match(html, new RegExp(`id="df-workbench-stage-${tab}"[^>]*disabled`));
  }
  assert.doesNotMatch(html, /id="df-import-result"/);
  assert.doesNotMatch(html, /id="df-load-flow"/);
  assert.doesNotMatch(html, /id="df-toggle-replay"/);
  assert.doesNotMatch(html, /id="df-resume-(save|restore|clear)"/);
  for (const tab of ["requests", "repair", "improve", "source"]) {
    assert.match(html, new RegExp(`id="df-workbench-tab-${tab}"[^>]*disabled`));
  }
  assert.match(html, /class="df-workbench-journey"/);
  assert.doesNotMatch(html, /<summary>Advanced tools<\/summary>/);
  assert.equal((html.match(/id="df-workbench-panel-[^"]+"[^>]*role="tabpanel"/g) || []).length, 8);
  assert.match(html, /id="df-agent-requests"[^>]*role="tabpanel"/);
  assert.match(css, /#df-dock-tabs,\s*\.df-panel-tabs/);
  assert.match(css, /\.df-dock-tab-list,\s*\.df-panel-tab-list/);
  assert.match(workbenchCss, /\.df-review-step-list[\s\S]*position: sticky/);
  assert.match(workbenchCss, /\.df-review-step-summary[\s\S]*opacity: \.9/);
  assert.match(html, /id="df-timeline"/);
  assert.match(html, /id="df-workbench-alert"[^>]*role="alert"/);
});

test("workbench state preserves hints and safety transitions without invoking a host", async () => {
  const workbench = await loadWorkbenchStateModule();
  let state = workbench.createInitialWorkbenchState("?test=flows%2Flogin.md&trace=run.json&agentRequest=approval_123");
  assert.equal(state.startupHints.test, "flows/login.md");
  assert.equal(state.startupHints.trace, "run.json");
  assert.equal(state.startupHints.agentRequest, "approval_123");
  assert.equal(state.selectedStage, "goal");
  state = workbench.normalizeWorkbenchState(state, {
    selectedTab: "repair",
    run: "unknown-completion",
    repair: "verification-failed",
  });
  assert.equal(state.selectedTab, "repair");
  assert.equal(state.selectedStage, "goal");
  assert.match(workbench.workbenchSafetyMessage(state), /Unknown or orphaned completion/);
  state = workbench.normalizeWorkbenchState(state, { run: "failed", repair: "rollback-required" });
  assert.match(workbench.workbenchSafetyMessage(state), /Rollback requires explicit human handling/);
});

test("guided stage tabs preserve Review as a distinct destination", async () => {
  const workbench = await loadWorkbenchStateModule();
  let state = workbench.createInitialWorkbenchState();
  state = workbench.normalizeWorkbenchState(state, { selectedTab: "review" });
  assert.equal(state.selectedTab, "review");
  assert.equal(state.selectedStage, "review");
  state = workbench.normalizeWorkbenchState(state, { run: "preflight" });
  assert.equal(state.selectedStage, "review");
  assert.match(read("inspector-workbench.js"), /STAGE_TABS/);
  assert.match(read("inspector-workbench.js"), /renderJourney/);
  assert.match(read("inspector-workbench.js"), /const reviewed = saved && readiness\.hardOutcomeCheck === true/);
  assert.match(read("inspector-workbench.js"), /function toolAvailability/);
  assert.match(
    read("inspector-workbench.js"),
    /tabs\.filter\(\(button\) => !button\.disabled && button\.closest\('\[role="tablist"\]'\) === tabList\)/,
  );
  assert.match(read("inspector-workbench.js"), /scrollIntoView/);
});

test("workbench state model includes safety states and confirmation-only shortcuts", () => {
  const workbench = read("inspector-workbench.js");

  for (const state of [
    "unknown-completion", "orphaned", "approval-expired", "awaiting-host-apply",
    "verification-failed", "rollback-required", "rollback-failed",
  ]) {
    assert.match(workbench, new RegExp(`['"]${state}['"]`));
  }
  assert.match(workbench, /Run check opened\. Review and explicitly approve before starting\./);
  assert.match(workbench, /Cancellation confirmation opened\. No run was changed yet\./);
  assert.match(workbench, /isEditableTarget/);
});

test("repair panel exposes human-only selector repair boundaries", () => {
  const repair = read("inspector-repair.js");

  for (const text of [
    "Nothing to repair yet", "Check latest failure", "Create suggested update",
    "Review suggested update", "Try this update", "Approve update", "Apply update",
    "Diagnose with your agent",
    "How this stays safe",
  ]) {
    assert.match(repair, new RegExp(text));
  }
  assert.match(repair, /one selector only; actions, checks, values, order, and source stay unchanged/i);
  assert.match(repair, /only a current local missing-selector failure can qualify/i);
  assert.match(repair, /agent-originated suggestions are never applied directly/i);
});

test("source proposal panel keeps XAML/C# review and flow repair separate", () => {
  const source = read("inspector-source.js");

  for (const text of [
    "Select a control first", "Check source", "Create source proposal",
    "Preview exact change", "Approve source change", "Apply approved XAML change",
    "Apply in IDE", "Download patch", "How this stays safe",
  ]) {
    assert.match(source, new RegExp(text));
  }
  assert.match(source, /Exact \$\{languageLabel\} diff/);
  assert.match(source, /Approval never changes a test selector/i);
  assert.match(source, /C# changes require a Roslyn-proven selection/i);
  assert.match(read("devflow.js"), /hasSource: info\.hasSource/);
  assert.match(read("devflow.js"), /\/api\/workbench\/source\/csharp/);
  assert.match(read("devflow.js"), /applySourceProposal/);
  assert.match(read("devflow.js"), /applyCSharpSourceProposal/);
});

test("workbench assets are embedded, routed, and responsive", () => {
  const server = read("../InspectorServer.cs");
  const css = read("inspector-workbench.css");

  for (const asset of [
    "inspector-host-bridge.js", "inspector-agent-requests.js", "inspector-workbench.js", "inspector-plan.js",
    "inspector-steps.js", "inspector-run.js", "inspector-trace.js",
    "inspector-repair.js", "inspector-improve.js", "inspector-source.js", "inspector-study.js", "inspector-workbench.css",
  ]) {
    assert.match(server, new RegExp(`/${asset.replace(".", "\\.")}`));
  }
  assert.match(css, /clamp\(300px, var\(--df-workbench-height, 46vh\)/);
  assert.match(css, /data-host-layout="compact"/);
  assert.match(css, /data-host-layout="narrow"/);
  assert.match(css, /data-host-layout="short"/);
  assert.match(css, /forced-colors: active/);
  assert.match(css, /\.df-agent-guide/);
});

test("agent request inbox permits narrowing only and never treats chat as approval", async () => {
  const approvals = await loadPanelModule("inspector-agent-requests.js");
  const requested = {
    allowedActions: ["fill", "tap"],
    allowedSelectors: ["automationId:NewTodoEntry", "automationId:AddButton"],
    allowedSideEffectClasses: ["ui"],
    maxActionCount: 2,
    maxValueBytes: 64,
  };

  assert.equal(approvals.isNarrowedAgentRequestScope(requested, {
    allowedActions: ["tap"],
    allowedSelectors: ["automationId:AddButton"],
    allowedSideEffectClasses: ["ui"],
    maxActionCount: 1,
    maxValueBytes: 0,
  }), true);
  assert.equal(approvals.isNarrowedAgentRequestScope(requested, {
    allowedActions: ["tap", "run"],
    allowedSelectors: ["automationId:AddButton"],
    allowedSideEffectClasses: ["ui"],
    maxActionCount: 2,
    maxValueBytes: 64,
  }), false);
  assert.equal(approvals.isNarrowedAgentRequestScope(requested, {
    allowedActions: ["tap"],
    allowedSelectors: ["automationId:AddButton"],
    allowedSideEffectClasses: ["ui"],
    maxActionCount: 3,
    maxValueBytes: 64,
  }), false);
  assert.match(approvals.agentRequestSummary({ requestedScope: requested }), /2 action types.*2 exact selectors.*up to 2 actions/);
  assert.equal(approvals.agentRequestGrantDurationSeconds({ kind: "commit" }), 600);
  assert.equal(approvals.agentRequestGrantDurationSeconds({ kind: "run" }), 300);
  assert.match(
    approvals.agentRequestStarterPrompt("MauiTodo", "WinUI"),
    /restricted DevFlow test-agent tools.*MauiTodo on WinUI.*commit review.*separate run request/i
  );

  const source = read("inspector-agent-requests.js");
  assert.match(source, /humanConfirmed: true/);
  assert.match(source, /\/api\/workbench\/agent-requests/);
  assert.match(source, /Your agent can continue; you do not need to copy anything into chat/i);
  assert.match(source, /scrollIntoView/);
  assert.match(source, /openPanel/);
  assert.match(source, /df-workbench-tab-requests/);
  assert.doesNotMatch(source, /Back to tests|df-agent-requests-open|setOpen\(|hasNewPending|seenPending/);
  assert.match(source, /Allow one run/);
  assert.match(source, /Your agent prepared a test/);
  assert.match(source, /Copy prompt for your agent/);
  assert.doesNotMatch(source, /approvalGrantId/);
});

test("host bridge exposes optional workbench capability names", () => {
  const bridge = read("inspector-host-bridge.js");

  for (const capability of [
    "saveTestBundle", "loadTestBundle", "pickTrace", "attachTestContext", "requestTestProposal", "openSourceDiff", "applySourceProposal", "applyCSharpSourceProposal", "getCSharpSourceSelection",
  ]) {
    assert.match(bridge, new RegExp(`['"]${capability}['"]`));
  }
  assert.match(bridge, /capability-missing/);
  assert.match(bridge, /bounded native trace picker/);
  assert.match(bridge, /testProposalApprovalResult/);
  assert.match(bridge, /grantId/);
});

test("run preflight summaries retain action classes but never fill values", async () => {
  const run = await loadPanelModule("inspector-run.js");
  const summary = run.summarizePlannedEffects({
    steps: [
      { action: "tap", args: { selector: { automationId: "save" } } },
      { action: "fill", args: { selector: { automationId: "password" }, value: "CorrectHorseBatteryStaple", secretRef: "test-login" } },
      { action: "navigate", args: { route: "/private/orders" } },
      { action: "setProperty", args: { name: "Text", value: "hidden" } },
    ],
  }).join("\n");

  assert.match(summary, /tap/);
  assert.match(summary, /fill/);
  assert.match(summary, /navigation/);
  assert.match(summary, /property change/);
  assert.match(summary, /secret reference: test-login/);
  assert.doesNotMatch(summary, /CorrectHorseBatteryStaple|\/private\/orders|hidden/);
  assert.equal(run.runStateIsTerminal("unknown-completion"), true);
  assert.equal(run.runStateIsTerminal("running"), false);

  const limited = run.runReadinessIssues({
    preflight: {
      admission: {
        reasons: [{
          code: "independent-oracle-absent",
          message: "No required independent business oracle is declared.",
          blocking: false,
        }],
      },
    },
  });
  assert.deepEqual(limited.blockers, []);
  assert.match(limited.notes[0], /can run.*not be marked independently verified/i);

  const stale = run.runReadinessIssues({
    preflight: { ok: true, admission: { reasons: [] } },
    stalePlan: true,
  });
  assert.match(stale.blockers[0], /plan no longer matches/i);
});

test("trace rendering selects divergence and uses disclosure envelopes only", async () => {
  const trace = await loadPanelModule("inspector-trace.js");
  const report = {
    divergenceStepId: "step-2",
    steps: [
      { stepId: "step-1", action: "tap" },
      { stepId: "step-2", action: "fill", failureClass: "assertion-failed" },
    ],
  };
  assert.equal(trace.firstDivergenceStepId(report), "step-2");
  const disclosure = trace.disclosureText({
    state: "redacted",
    type: "string",
    length: 24,
    digest: "sha256:abc",
    value: "CorrectHorseBatteryStaple",
  });
  assert.match(disclosure, /redacted.*length 24.*sha256:abc/);
  assert.doesNotMatch(disclosure, /CorrectHorseBatteryStaple/);

  const untrusted = trace.importedTrustPresentation({ verification: { state: "untrusted" } });
  const attested = trace.importedTrustPresentation({ verification: { state: "attested" } });
  assert.match(untrusted.explanation, /cannot execute/i);
  assert.match(attested.explanation, /diagnostic-only/i);
});

test("terminal locator ambiguity routes to guided resolution instead of repair or a guess", async () => {
  const trace = await loadPanelModule("inspector-trace.js");
  const report = {
    outcome: { status: "failed", terminal: true },
    failure: { class: "locator-ambiguous", stepId: "step-3" },
    steps: [{
      stepId: "step-3",
      sequence: 3,
      failureClass: "locator-ambiguous",
      candidateCount: 4,
      targetResolution: { matchCount: 4 },
    }],
  };

  assert.equal(trace.isAmbiguousLocatorFailure(report), true);
  assert.equal(trace.ambiguityMatchCount(report), 4);
  assert.equal(trace.isAmbiguousLocatorFailure({
    failure: { legacyKind: "ambiguous", stepId: "step-1" },
    steps: [{ stepId: "step-1", candidateCount: 2 }],
  }), true);
  assert.equal(trace.isAmbiguousLocatorFailure({
    failure: { class: "locator-not-found" },
  }), false);
});

test("run and trace use broker adapters and preserve imported read-only boundaries", () => {
  const devflow = read("devflow.js");
  const run = read("inspector-run.js");
  const trace = read("inspector-trace.js");

  for (const route of [
    "/api/workbench/run/preflight",
    "/api/workbench/run/start",
    "/api/workbench/run/journal",
    "/api/workbench/artifacts/import",
  ]) {
    assert.match(devflow, new RegExp(route.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
  }
  assert.match(devflow, /setCapturedTraceMode/);
  assert.match(devflow, /state\.mode = 'reproduction'/);
  assert.match(devflow, /Captured result mode is read-only/);
  assert.match(read("inspector-workbench.js"), /event\.key === '\['/);
  assert.match(read("inspector-workbench.js"), /event\.key === '\]'/);
  assert.doesNotMatch(run, /\/api\/flows\/replay/);
  assert.doesNotMatch(trace, /\/api\/flows\/replay/);
  assert.match(trace, /Reproduce locally/);
  assert.match(trace, /Download linked \.mauitrace v1/);
});

test("human plan draft includes required test-plan-v1 authoring fields", async () => {
  const plan = await loadPanelModule("inspector-plan.js");
  const draft = plan.createPlanDraft("login.md", "a".repeat(64));

  for (const field of [
    "schema", "planId", "revision", "flow", "goal", "scenarios", "preconditions",
    "reset", "acceptanceCriteria", "sideEffectPolicy", "provenance",
  ]) {
    assert.ok(Object.hasOwn(draft, field), `missing ${field}`);
  }
  assert.equal(draft.flow.path, "login.md");
  assert.equal(draft.flow.digest, "a".repeat(64));
  assert.equal(draft.provenance.actorKind, "human");
  assert.equal(draft.provenance.channel, "inspector");
  assert.match(
    plan.agentPreparationPrompt("Adding a todo updates the count"),
    /restricted DevFlow test-agent tools.*Adding a todo updates the count.*commit review.*separate run request/i
  );
});

test("typed assertion and selector authoring keep hard and observation semantics distinct", async () => {
  const steps = await loadPanelModule("inspector-steps.js");

  assert.equal(steps.isObservationOnlyAssertion("pageChanged"), true);
  assert.equal(steps.isObservationOnlyAssertion("exists"), false);
  assert.equal(steps.isStrictAuthoringSelector({
    automationId: "save", matchCount: 1, quality: "durable",
  }), true);
  assert.equal(steps.isStrictAuthoringSelector({
    automationId: "TodoCheckBox",
    stableItemKey: "todo-42",
    collectionScope: "TodoList",
    matchCount: 1,
    quality: "stable-item-key",
  }), true);
  assert.equal(steps.isStrictAuthoringSelector({
    id: "runtime-42", matchCount: 1, quality: "fragile",
  }), false);
  assert.equal(steps.isStrictAuthoringSelector({
    text: "Save", matchCount: 2, quality: "ambiguous",
  }), false);

  const flow = {
    steps: [
      { stepId: "a", seq: 1, action: "tap" },
      { stepId: "b", seq: 2, action: "fill" },
      { stepId: "c", seq: 3, action: "tap" },
    ],
  };
  const moved = steps.moveFlowStep(flow, 1, -1);
  assert.deepEqual(moved.steps.map((step) => step.stepId), ["b", "a", "c"]);
  assert.deepEqual(moved.steps.map((step) => step.seq), [1, 2, 3]);
  const removed = steps.removeFlowStep(flow, 1);
  assert.deepEqual(removed.steps.map((step) => step.stepId), ["a", "c"]);
  assert.deepEqual(removed.steps.map((step) => step.seq), [1, 2]);
  assert.deepEqual(steps.usableSelectorFromMatch({
    automationId: "TodoCheckBox",
    stableItemKey: "todo-42",
    collectionScope: "TodoList",
  }, [], false), {
    automationId: "TodoCheckBox",
    stableItemKey: "todo-42",
    collectionScope: "TodoList",
  });
  assert.equal(steps.usableSelectorFromMatch(
    { automationId: "TodoCheckBox" },
    [{ automationId: "TodoCheckBox" }, { automationId: "TodoCheckBox" }],
    false
  ), null);
  const issues = steps.authoringIssues({
    errors: ["step 1: selector must resolve exactly one element; it currently reports 9 matches."],
  });
  assert.equal(issues[0].stepSequence, 1);
  assert.equal(issues[0].remediation, "resolve-selector");
});

test("authoring panels retain explicit recording, validation, diff, and commit controls", () => {
  const plan = read("inspector-plan.js");
  const steps = read("inspector-steps.js");
  const devflow = read("devflow.js");
  const bridge = read("inspector-host-bridge.js");

  assert.match(plan, /Reload saved test/);
  assert.match(plan, /Download current draft/);
  assert.match(plan, /What should this test prove\? \(required\)/);
  assert.match(plan, /Create your first test/);
  assert.match(plan, /Create this test with your agent/);
  assert.match(plan, /required: true/);
  assert.match(plan, /ariaInvalid/);
  assert.match(plan, /ariaDescribedBy/);
  for (const expander of [
    "Name and file (optional)",
    "Scenarios and outcomes (optional)",
    "Setup, safety, and platforms (optional)",
    "Review metadata (optional)",
  ]) {
    assert.match(plan, new RegExp(expander.replace(/[()]/g, "\\$&")));
  }
  assert.match(plan, /Optional\. The Goal remains the required description\./);
  assert.ok(plan.indexOf("df-goal-input") < plan.indexOf("df-test-name-input"));
  assert.doesNotMatch(plan, /Advanced: Quick record without a managed plan|Quick record raw draft/);
  assert.doesNotMatch(plan, /Save plan draft only|Advanced test settings|className: 'df-plan-more'/);
  assert.doesNotMatch(plan, /helpers\.capability\(root, 'saveTestBundle'\)/);
  for (const text of [
    "Go to Goal", "Start recording", "Stop recording", "Discard recording", "Continue to Review",
    "Go to Steps", "Check test", "Review changes", "Save test", "Continue to Run",
    "Record more steps", "Select a step", "Save step", "Move up", "Move down", "Remove step",
    "Step details \\(optional\\)",
    "Add expected result", "Download recording draft", "Check current app now", "exactly one element", "never prefilled",
    "Resolve step", "Check matching controls", "Use this control", "stable item key",
  ]) {
    assert.match(steps, new RegExp(text));
  }
  assert.match(steps, /addResult\.open = !assertions\.some/);
  assert.match(steps, /draft\.checkPassed !== true/);
  assert.match(steps, /draft\.diffReviewed !== true/);
  assert.match(steps, /function selectorCheckKey/);
  assert.match(steps, /selectorChecks\.clear\(\)/);
  assert.doesNotMatch(steps, /Recording status|className: 'df-steps-more'/);
  assert.doesNotMatch(steps, /At least one hard outcome check|Typed assertion composer/);
  assert.match(devflow, /const retainedPlan = source === 'recording'/);
  assert.match(devflow, /startAppendingRecording/);
  assert.match(devflow, /appendRecordingBase/);
  assert.match(devflow, /lastWorkbenchCanDrive/);
  assert.match(devflow, /authoringChanged \|\| driveAuthorityChanged/);
  assert.match(devflow, /preservePanel: !rerender/);
  assert.match(read("inspector-workbench.js"), /options\.preservePanel === true/);
  assert.match(devflow, /merged\.steps =/);
  assert.match(devflow, /bindingStale/);
  assert.match(devflow, /older flow digest/);
  assert.match(devflow, /openGoalForRecovery\('Add a Goal before recording actions\.'/);
  assert.match(devflow, /A Goal is required to save this test\. Your recorded draft is still here\./);
  assert.match(devflow, /Recording draft saved —/);
  assert.match(devflow, /downloadRecordingDraft/);
  assert.match(devflow, /Save the test before running it\. Your recorded draft is still here\./);
  assert.match(bridge, /loadTestBundle/);
  assert.match(bridge, /download fallback/i);
});

test("modal focus and Goal recovery include editable controls and focus restoration", () => {
  const workbench = read("inspector-workbench.js");
  const dialog = read("inspector-dialog.js");
  for (const source of [workbench, dialog]) {
    assert.match(source, /input:not\(\[disabled\]\):not\(\[type="hidden"\]\)/);
    assert.match(source, /textarea:not\(\[disabled\]\)/);
    assert.match(source, /select:not\(\[disabled\]\)/);
    assert.match(source, /\[contenteditable\]:not\(\[contenteditable="false"\]\)/);
  }
  assert.match(workbench, /returnFocus/);
  assert.match(workbench, /focusGoal/);
  assert.match(workbench, /pendingGoalFocus/);
  assert.match(workbench, /win\.clearTimeout\(pendingGoalFocus\)/);
  assert.match(workbench, /if \(!opened \|\| state\.selectedStage !== 'goal'\) return;/);
  assert.match(workbench, /if \(event\.key === 'Escape' && opened\)[\s\S]*?close\(true\);[\s\S]*?if \(trapFocus\(event\)\) return;/);
  assert.match(workbench, /if \(trapFocus\(event\)\) return;\s+if \(isEditableTarget\(event\.target\)\) return;/);
  assert.doesNotMatch(read("inspector.html"), /id="df-toggle-record"/);
  assert.match(read("inspector-plan.js"), /df-goal-help/);
});

test("toolbar keeps toggle semantics when actions move into More", () => {
  const devflow = read("devflow.js");
  const css = read("devflow.css");

  assert.match(devflow, /menuitemcheckbox/);
  assert.match(devflow, /aria-checked/);
  assert.match(devflow, /function setToolbarToggleState/);
  assert.match(devflow, /setToolbarToggleState\(tb\.bounds, on\)/);
  assert.match(devflow, /setToolbarToggleState\(toggleDockBtn, true\)/);
  assert.match(css, /\.df-toolbar-overflow \.df-tool-btn\.df-active[\s\S]*var\(--df-accent\)/);
});

test("VS Code host advertises implemented capabilities and refreshes after broker restarts", () => {
  const extension = readVscodeHost();
  const capabilities = extension.match(/const capabilities = \[([^\]]+)\];/)?.[1] || "";

  assert.doesNotMatch(capabilities, /attachTestContext|requestTestProposal/);
  assert.doesNotMatch(extension, /devflow:attachTestContext|devflow:requestTestProposal/);
  assert.match(extension, /const restartWatcher = setInterval/);
  assert.match(extension, /inspectorConnectionSignature/);
  assert.match(extension, /currentSelection = null/);
  assert.match(extension, /currentDataSnapshot = null/);
  assert.match(extension, /panel\.webview\.options =/);
  assert.match(extension, /function withInspectorStartupHints/);
  assert.match(extension, /\["test", hints\?\.test\]/);
  assert.match(extension, /\["trace", hints\?\.trace\]/);
  assert.match(extension, /\["agentRequest", hints\?\.agentRequest\]/);
});

test("run uses a concise confirmed run check with visual progress and advanced legacy replay", () => {
  const devflow = read("devflow.js");
  const run = read("inspector-run.js");

  assert.match(devflow, /async openPreflight\(\)/);
  assert.match(devflow, /async function legacyQuickReplay/);
  assert.match(run, /Review and start/);
  assert.match(run, /Go to Review/);
  assert.match(run, /readiness\?\.hardOutcomeCheck !== true/);
  assert.match(run, /Run details \(optional\)/);
  assert.match(run, /Legacy quick replay \(advanced\)/);
  assert.doesNotMatch(run, /df-run-advanced-actions/);
  assert.match(run, /df-run-progress-visual/);
  assert.match(run, /df-run-step-/);
  assert.match(devflow, /scheduleRefresh\(125\)/);
  assert.match(devflow, /openStage\?\.\('results'/);
  assert.match(devflow, /focusResults/);
});

test("results show persistent summaries and contextual next actions", () => {
  const trace = read("inspector-trace.js");

  for (const text of [
    "Test passed", "Test needs attention", "Run again", "Improve test",
    "View failed step", "Review repair", "Improve selector", "Resolve", "Technical trace details",
  ]) {
    assert.match(trace, new RegExp(text));
  }
  assert.match(trace, /Diagnose this failure with your agent/);
  assert.match(trace, /df-results-summary/);
  assert.match(trace, /df-results-banner/);
  for (const next of ["Go to Goal", "Go to Steps", "Go to Review", "Go to Run"]) {
    assert.match(trace, new RegExp(next));
  }
  assert.match(trace, /resultsNextStep/);
  assert.match(trace, /Open a result from another run/);
  assert.match(trace, /without running or changing the connected app/);
});

test("empty Results routes to the first unmet workflow prerequisite", async () => {
  const trace = await loadPanelModule("inspector-trace.js");

  assert.equal(trace.resultsNextStep({}).stage, "goal");
  assert.equal(trace.resultsNextStep({ goal: true }).stage, "record");
  assert.equal(trace.resultsNextStep({ goal: true, recordedSteps: true }).stage, "review");
  assert.equal(trace.resultsNextStep({
    goal: true,
    recordedSteps: true,
    hardOutcomeCheck: true,
  }).stage, "review");
  assert.equal(trace.resultsNextStep({
    goal: true,
    recordedSteps: true,
    hardOutcomeCheck: true,
    savedBundle: true,
  }).stage, "run");
});

test("Tests landing, recovery, and fast-run handoff stay visible", () => {
  const plan = read("inspector-plan.js");
  const workbenchCss = read("inspector-workbench.css");
  const devflow = read("devflow.js");
  const workbench = read("inspector-workbench.js");

  assert.match(plan, /This is the only required field before adding steps/);
  assert.match(plan, /Record steps/);
  assert.match(plan, /Open saved test/);
  assert.match(plan, /df-saved-test-picker/);
  assert.match(plan, /Choose Markdown file/);
  assert.match(plan, /Reload saved test/);
  assert.match(plan, /Download current draft/);
  assert.match(workbenchCss, /df-workbench-recovery/);
  assert.match(workbenchCss, /df-workbench-tool-list/);
  assert.match(devflow, /900 - visibleFor/);
  assert.match(workbench, /Tests opened\. Enter a Goal or open a saved test\./);
});

test("ambiguity controller re-verifies a human choice and changes only a draft selector", () => {
  const devflow = read("devflow.js");
  const improve = read("inspector-improve.js");
  const repair = read("inspector-repair.js");
  const trace = read("inspector-trace.js");
  const helperStart = devflow.indexOf("async applyHumanSelectedSelector");
  const helperEnd = devflow.indexOf("async verifyAssertion", helperStart);
  const helper = devflow.slice(helperStart, helperEnd);
  const improveStart = devflow.indexOf("async resolveAmbiguity");
  const improveEnd = devflow.indexOf("async openSource", improveStart);
  const ambiguityController = devflow.slice(improveStart, improveEnd);

  assert.ok(helperStart >= 0 && helperEnd > helperStart);
  assert.ok(improveStart >= 0 && improveEnd > improveStart);
  assert.match(trace, /Resolve \$\{count\} matches/);
  assert.match(trace, /will not choose automatically/i);
  assert.match(improve, /Highlight in app/);
  assert.match(improve, /Improve app testability/);
  assert.match(ambiguityController, /authoringController\.verifySelector/);
  assert.match(ambiguityController, /testWorkbench\?\.open\?\.\('improve'/);
  assert.match(ambiguityController, /testWorkbench\?\.open\?\.\('source'/);
  assert.match(ambiguityController, /applyHumanSelectedSelector/);
  assert.match(ambiguityController, /state\.filters\.step/);
  assert.match(ambiguityController, /await analyze\(true\)/);
  assert.doesNotMatch(ambiguityController, /repairController|\/repair\/propose/);
  assert.match(helper, /authoringStepIndexForFailure/);
  assert.match(helper, /verification\.matchCount !== 1/);
  assert.match(helper, /authoringDraft\.flowDirty = true/);
  assert.match(helper, /action, assertion, expected value/i);
  assert.match(helper, /Save test, then rerun it/);
  assert.doesNotMatch(helper, /commitBundle|runController|startRun|legacyQuickReplay/);
  assert.match(repair, /The selector matched more than one control/);
  assert.match(repair, /Resolve in Improve/);
});
