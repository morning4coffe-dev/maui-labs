import assert from "node:assert/strict";
import test from "node:test";

import {
  LAYOUT_FINDING_LIMIT,
  chooseLayoutScopeRoot,
  createLayoutDataPayload,
  diffLayoutReports,
  filterLayoutFindings,
  formatLayoutReport,
  formatPerformanceSummary,
} from "../../../Cli/Microsoft.Maui.Cli/DevFlow/Inspector/Web/inspector-diagnostics.js";

const layoutReport = {
  schemaVersion: "1.0",
  ruleSetVersion: "1.0",
  platform: "Windows",
  scope: { elementsExamined: 42, maxElements: 2000, truncated: false },
  coverage: {
    overall: "partial",
    rules: [
      { ruleId: "layout.visible-zero-area", support: "full", confidence: "high", evaluated: 42, skipped: 0 },
      { ruleId: "layout.outside-window", support: "partial", confidence: "high", evaluated: 20, skipped: 22 },
    ],
    limitations: ["Findings are derived from managed MAUI layout state only."],
    neverCaptured: ["Element Text/Value content"],
  },
  summary: { violations: 1, observations: 2, incomplete: 1 },
  findings: [
    {
      id: "layout.visible-zero-area:e1:area",
      ruleId: "layout.visible-zero-area",
      outcome: "violation",
      confidence: "high",
      message: "Label was arranged with a non-positive width.",
      explanation: "A realized element with no area cannot draw.",
      element: {
        id: "e1",
        type: "Label",
        automationId: "Title",
        sourceFile: "Views/MainPage.xaml",
        sourceLine: 12,
      },
      limitations: ["A deliberately collapsed element matches this rule."],
    },
    {
      id: "layout.outside-window:scope:incomplete",
      ruleId: "layout.outside-window",
      outcome: "incomplete",
      confidence: "high",
      message: "layout.outside-window could not be evaluated for 22 element(s).",
      explanation: "Managed layout state did not expose the required measurements.",
      limitations: [],
    },
  ],
};

test("layout view reports coverage next to the counts", () => {
  const view = formatLayoutReport(layoutReport);

  assert.equal(view.title, "Layout · 1 violation");
  assert.match(view.summary, /1 violation/);
  assert.match(view.summary, /2 observations/);
  assert.match(view.summary, /1 incomplete/);
  assert.match(view.scope, /42 elements examined/);
  assert.equal(view.coverage, "Coverage: partial");
  assert.equal(view.version, "schema v1.0 · rules v1.0");
  assert.deepEqual(view.rules.map((rule) => rule.ruleId), [
    "layout.visible-zero-area",
    "layout.outside-window",
  ]);
  assert.equal(view.rules[1].detail, "20 evaluated · 22 skipped");
});

test("a clean scan is never summarised as a pass", () => {
  const view = formatLayoutReport({
    ...layoutReport,
    summary: { violations: 0, observations: 0, incomplete: 3 },
    findings: [],
  });

  assert.equal(view.title, "Layout · No violations in the evaluated elements");
  assert.match(view.summary, /3 incomplete/);
  assert.equal(view.findings.length, 0);
  assert.equal(view.coverage, "Coverage: partial");
});

test("findings expose the element to select and their source context", () => {
  const view = formatLayoutReport(layoutReport);

  assert.equal(view.findings[0].elementId, "e1");
  assert.equal(view.findings[0].outcomeLabel, "Violation");
  assert.equal(view.findings[0].context, "Label · #Title · MainPage.xaml:12");
  assert.deepEqual(view.findings[0].limitations, ["A deliberately collapsed element matches this rule."]);
  // An aggregate incomplete finding has no element to select.
  assert.equal(view.findings[1].elementId, null);
  assert.equal(view.findings[1].outcomeLabel, "Incomplete");
});

test("the rendered finding list is bounded", () => {
  const findings = Array.from({ length: LAYOUT_FINDING_LIMIT + 10 }, (_, index) => ({
    id: `layout.visible-zero-area:e${index}:area`,
    ruleId: "layout.visible-zero-area",
    outcome: "violation",
    message: "m",
    explanation: "e",
  }));

  const view = formatLayoutReport({ ...layoutReport, findings });

  assert.equal(view.findings.length, LAYOUT_FINDING_LIMIT);
  assert.equal(view.findingsTruncated, true);
});

test("layout view tolerates a malformed report", () => {
  const view = formatLayoutReport(undefined);

  assert.equal(view.findings.length, 0);
  assert.equal(view.coverage, "Coverage: unavailable");
  assert.deepEqual(view.limitations, []);
});

test("layout filters combine outcome severity confidence rule and suppression state", () => {
  const findings = [
    { id: "a", ruleId: "layout.element-clipped", outcome: "violation", severity: "serious", confidence: "high" },
    { id: "b", ruleId: "layout.content-overflow", outcome: "observation", severity: "minor", confidence: "medium" },
    { id: "c", ruleId: "layout.element-clipped", outcome: "violation", severity: "critical", confidence: "exact", suppressed: true },
  ];

  const filtered = filterLayoutFindings(findings, {
    outcome: "violations",
    minimumSeverity: "serious",
    minimumConfidence: "high",
    rule: "clipped",
    includeSuppressed: false,
  });

  assert.deepEqual(filtered.map((finding) => finding.id), ["a"]);
});

test("layout view exposes rich finding metadata and related elements", () => {
  const view = formatLayoutReport({
    ...layoutReport,
    schemaVersion: "2.0",
    snapshot: { id: "s1", treeRevision: "tree1", diagnosticsRevision: "diag1", stable: false },
    findings: [{
      ...layoutReport.findings[0],
      severity: "serious",
      actionability: "fix",
      fixCategories: ["adjust-layout-constraints"],
      relatedElements: [{
        relation: "clipper",
        element: { id: "host", type: "Grid", automationId: "Host" },
      }],
    }],
  });

  assert.equal(view.snapshot.treeRevision, "tree1");
  assert.equal(view.findings[0].severity, "serious");
  assert.equal(view.findings[0].actionability, "fix");
  assert.equal(view.findings[0].relatedElements[0].relation, "clipper");
});

test("layout Copilot payload excludes captured text while retaining structural evidence", () => {
  const payload = createLayoutDataPayload({
    ...layoutReport,
    findings: [{
      ...layoutReport.findings[0],
      evidence: {
        fullRegion: { bounds: { x: 1, y: 2, width: 3, height: 4 }, points: [] },
        text: {
          kind: "label",
          isTruncated: true,
          textLength: 18,
          text: "private visible text",
          measurementSource: "native",
        },
      },
    }],
  }, "layout.visible-zero-area:e1:area");

  assert.equal(payload.findings[0].evidence.text.textLength, 18);
  assert.equal(Object.hasOwn(payload.findings[0].evidence.text, "text"), false);
  assert.doesNotMatch(JSON.stringify(payload), /private visible text/);
});

test("layout Copilot payload follows the active Data filters", () => {
  const payload = createLayoutDataPayload(layoutReport, null, {
    outcome: "incomplete",
    minimumSeverity: "info",
    minimumConfidence: "low",
    includeSuppressed: false,
  });

  assert.deepEqual(payload.findings.map((finding) => finding.outcome), ["incomplete"]);
});

test("layout report deltas distinguish added changed and removed findings", () => {
  const previous = {
    findings: [
      { id: "kept", ruleId: "a", outcome: "violation", severity: "serious", message: "before" },
      { id: "removed", ruleId: "b", outcome: "observation", severity: "minor" },
    ],
  };
  const current = {
    findings: [
      { id: "kept", ruleId: "a", outcome: "violation", severity: "serious", message: "after" },
      { id: "added", ruleId: "c", outcome: "incomplete", severity: "info" },
    ],
  };

  const delta = diffLayoutReports(previous, current);

  assert.deepEqual(delta.added.map((finding) => finding.id), ["added"]);
  assert.deepEqual(delta.updated.map((finding) => finding.id), ["kept"]);
  assert.deepEqual(delta.removed, ["removed"]);
  assert.equal(diffLayoutReports(current, current), null);
});

test("layout scans prefer the rendered page over the Shell root", () => {
  assert.equal(chooseLayoutScopeRoot([
    { id: "shell", type: "AppShell" },
    { id: "page", type: "MainPage" },
    { id: "grid", type: "Grid" },
  ]), "page");
  assert.equal(chooseLayoutScopeRoot([{ id: "root", type: "Grid" }]), "root");
});

const performanceSummary = {
  session: { sessionId: "s1", active: true, sampleCount: 12, sampledDurationMs: 5000, sampleIntervalMs: 250 },
  memory: {
    managedStartBytes: 1048576,
    managedEndBytes: 2097152,
    managedPeakBytes: 3145728,
    managedDeltaBytes: 1048576,
    nativeSupported: false,
    processSupported: true,
    processKind: "windows.working-set",
    processStartBytes: 4194304,
    processEndBytes: 5242880,
    processPeakBytes: 6291456,
    processDeltaBytes: 1048576,
  },
  gc: { supported: true, gen0Delta: 5, gen1Delta: 2, gen2Delta: 0 },
  cpu: { supported: true, averagePercent: 33.333, peakPercent: 61 },
  threads: { supported: true, peakCount: 33 },
  frames: { supported: false, unsupportedReason: "This agent can only estimate frame timings." },
  markers: { total: 5, ui: 2, network: 1, navigation: 1, spanCount: 3 },
  loss: { anyLoss: true, samplesLost: 40, markersLost: 3, spansLost: 1 },
  hotspots: [
    { kind: "ui.operation", name: "MainPage.Appearing", screen: "//main", count: 3, errorCount: 1, p95DurationMs: 90, maxDurationMs: 95 },
  ],
  capability: { platform: "Windows", mode: "debug", lowPerturbation: false, limitations: ["Triage only."] },
  warnings: [
    "Profiler buffers overwrote data before it was read (40 samples, 3 markers, 1 span).",
    "Measured in a non-profile build.",
  ],
};

test("performance view formats the metric rows a triage read needs", () => {
  const view = formatPerformanceSummary(performanceSummary);

  assert.equal(view.title, "Performance · recording");
  assert.equal(view.active, true);
  assert.match(view.session, /12 samples/);
  assert.match(view.session, /every 250 ms/);
  assert.equal(view.mode, "Windows · mode debug");

  const managed = view.metrics.find((metric) => metric.label === "Managed memory");
  assert.equal(managed.value, "1.0 MB → 2.0 MB");
  assert.match(managed.detail, /peak 3.0 MB/);
  assert.match(managed.detail, /delta \+1.0 MB/);

  const gc = view.metrics.find((metric) => metric.label === "GC collections");
  assert.equal(gc.value, "gen0 +5 · gen1 +2 · gen2 +0");

  const process = view.metrics.find((metric) => metric.label === "Process memory");
  assert.equal(process.value, "4.0 MB → 5.0 MB");
  assert.match(process.detail, /windows\.working-set/);

  const cpu = view.metrics.find((metric) => metric.label === "CPU");
  assert.equal(cpu.value, "avg 33.33% · peak 61%");
});

test("estimated frame timings never surface as a frame rate", () => {
  const view = formatPerformanceSummary(performanceSummary);
  const frames = view.metrics.find((metric) => metric.label === "Frames");

  assert.equal(frames.value, "not measured");
  assert.match(frames.detail, /estimate/i);
});

test("native frame statistics are shown when they are authoritative", () => {
  const view = formatPerformanceSummary({
    ...performanceSummary,
    frames: {
      supported: true,
      source: "native.android.framemetrics",
      quality: "native.exact",
      averageFps: 51.25,
      minimumFps: 42,
      frameTimeMsP95: 31,
      worstFrameTimeMs: 96,
      jankFrameCount: 6,
      uiThreadStallCount: 2,
    },
  });
  const frames = view.metrics.find((metric) => metric.label === "Frames");

  assert.equal(frames.value, "avg 51.25 fps · min 42 fps");
  assert.match(frames.detail, /native\.android\.framemetrics \/ native\.exact/);
  assert.match(frames.detail, /p95 31 ms/);
  assert.match(frames.detail, /jank 6/);
});

test("buffer loss is promoted to the top of the warnings", () => {
  const view = formatPerformanceSummary(performanceSummary);

  assert.match(view.warnings[0], /overwrote data/);
  assert.match(view.warnings[0], /40 samples/);
  assert.equal(view.warnings.length, 2);
});

test("debug mode is called out as perturbing the measurement", () => {
  const view = formatPerformanceSummary(performanceSummary);

  assert.equal(view.perturbed, true);
  assert.match(view.perturbationNote, /Hot Reload/);
});

test("an explicit profile build reports the low-perturbation state", () => {
  const view = formatPerformanceSummary({
    ...performanceSummary,
    session: { ...performanceSummary.session, active: false },
    capability: { ...performanceSummary.capability, mode: "profile", lowPerturbation: true },
  });

  assert.equal(view.title, "Performance · stopped");
  assert.equal(view.perturbed, false);
  assert.match(view.perturbationNote, /read-only/i);
  assert.match(view.mode, /read-only profile build/);
});

test("unsupported metrics say so instead of showing a zero", () => {
  const view = formatPerformanceSummary({
    ...performanceSummary,
    memory: { ...performanceSummary.memory, nativeSupported: false },
    cpu: { supported: false },
    threads: { supported: false },
  });

  assert.equal(view.metrics.find((metric) => metric.label === "Native heap").value, "not observable");
  assert.equal(view.metrics.find((metric) => metric.label === "CPU").value, "not observable");
  assert.equal(view.metrics.find((metric) => metric.label === "Threads").value, "not observable");
});

test("performance view tolerates a malformed summary", () => {
  const view = formatPerformanceSummary(undefined);

  assert.equal(view.active, false);
  assert.equal(view.metrics.length > 0, true);
  assert.deepEqual(view.hotspots, []);
});
