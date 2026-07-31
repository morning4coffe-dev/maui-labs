/**
 * Presentation logic for the Inspector's Layout and Performance data tabs.
 *
 * Kept in its own module (like inspector-evidence.js) so the formatting rules are unit-testable
 * without a DOM. The rules here are part of the product contract:
 *
 *  - A layout report is never summarised as "no problems". Absent findings are reported alongside
 *    the coverage that produced them, because unavailable geometry is `incomplete`, not a pass.
 *  - A performance summary never invents a frame rate. When the agent cannot measure frames the
 *    row states why instead of showing a modelled number.
 */

/** Findings rendered in the dock before the list is truncated (JSON/MCP still return everything). */
export const LAYOUT_FINDING_LIMIT = 200;

const OUTCOME_LABELS = Object.freeze({
  violation: 'Violation',
  observation: 'Observation',
  incomplete: 'Incomplete',
});

function outcomeLabel(outcome) {
  return OUTCOME_LABELS[outcome] || 'Finding';
}

function text(value, fallback = '') {
  return value === null || value === undefined ? fallback : String(value);
}

function shortFileName(path) {
  if (!path) return null;
  const normalized = String(path).replace(/\\/g, '/');
  const index = normalized.lastIndexOf('/');
  return index >= 0 ? normalized.slice(index + 1) : normalized;
}

/** Builds the view model the Layout tab renders. Pure: no DOM, no network. */
export function formatLayoutReport(report) {
  const summary = (report && report.summary) || {};
  const scope = (report && report.scope) || {};
  const coverage = (report && report.coverage) || {};
  const findings = Array.isArray(report && report.findings) ? report.findings : [];

  const violations = Number(summary.violations) || 0;
  const observations = Number(summary.observations) || 0;
  const incomplete = Number(summary.incomplete) || 0;

  const headline = violations > 0
    ? `${violations} violation${violations === 1 ? '' : 's'}`
    : 'No violations in the evaluated elements';

  const parts = [
    headline,
    `${observations} observation${observations === 1 ? '' : 's'}`,
    `${incomplete} incomplete`,
  ];

  const scopeText = [
    `${Number(scope.elementsExamined) || 0} element${(Number(scope.elementsExamined) || 0) === 1 ? '' : 's'} examined`,
    scope.rootElementId ? `under ${scope.rootElementId}` : null,
    scope.truncated ? `truncated at ${Number(scope.maxElements) || 0}` : null,
  ].filter(Boolean).join(' · ');

  const truncated = findings.length > LAYOUT_FINDING_LIMIT;
  const shown = truncated ? findings.slice(0, LAYOUT_FINDING_LIMIT) : findings;

  return {
    title: `Layout · ${headline}`,
    summary: parts.join(' · '),
    scope: scopeText,
    coverage: `Coverage: ${text(coverage.overall, 'unavailable')}`,
    version: `schema v${text(report && report.schemaVersion, '?')} · rules v${text(report && report.ruleSetVersion, '?')}`,
    rules: (Array.isArray(coverage.rules) ? coverage.rules : []).map((rule) => ({
      ruleId: text(rule.ruleId),
      support: text(rule.support, 'unavailable'),
      detail: `${Number(rule.evaluated) || 0} evaluated · ${Number(rule.skipped) || 0} skipped`,
    })),
    findings: shown.map((finding) => ({
      id: text(finding.id),
      ruleId: text(finding.ruleId),
      outcome: text(finding.outcome, 'observation'),
      outcomeLabel: outcomeLabel(finding.outcome),
      confidence: text(finding.confidence, 'medium'),
      message: text(finding.message),
      explanation: text(finding.explanation),
      elementId: finding.element && finding.element.id ? String(finding.element.id) : null,
      context: [
        finding.element ? finding.element.type : null,
        finding.element && finding.element.automationId ? `#${finding.element.automationId}` : null,
        finding.element && finding.element.sourceFile
          ? `${shortFileName(finding.element.sourceFile)}${finding.element.sourceLine ? `:${finding.element.sourceLine}` : ''}`
          : null,
      ].filter(Boolean).join(' · '),
      limitations: Array.isArray(finding.limitations) ? finding.limitations.map(String) : [],
    })),
    findingsTruncated: truncated,
    limitations: Array.isArray(coverage.limitations) ? coverage.limitations.map(String) : [],
    neverCaptured: Array.isArray(coverage.neverCaptured) ? coverage.neverCaptured.map(String) : [],
  };
}

function formatBytes(bytes) {
  if (bytes === null || bytes === undefined || Number.isNaN(Number(bytes))) return 'n/a';
  const value = Number(bytes);
  const units = ['B', 'KB', 'MB', 'GB'];
  let size = Math.abs(value);
  let unit = 0;
  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024;
    unit += 1;
  }
  const rounded = size >= 100 ? size.toFixed(0) : size.toFixed(1);
  return `${value < 0 ? '-' : ''}${rounded} ${units[unit]}`;
}

function formatDelta(bytes) {
  if (bytes === null || bytes === undefined) return 'n/a';
  const value = Number(bytes);
  return `${value >= 0 ? '+' : ''}${formatBytes(value)}`;
}

function formatNumber(value, suffix = '') {
  if (value === null || value === undefined || Number.isNaN(Number(value))) return 'n/a';
  const rounded = Math.round(Number(value) * 100) / 100;
  return `${rounded}${suffix}`;
}

/** Builds the view model the Performance tab renders. Pure: no DOM, no network. */
export function formatPerformanceSummary(summary) {
  const session = (summary && summary.session) || {};
  const memory = (summary && summary.memory) || {};
  const gc = (summary && summary.gc) || {};
  const cpu = (summary && summary.cpu) || {};
  const threads = (summary && summary.threads) || {};
  const frames = (summary && summary.frames) || {};
  const markers = (summary && summary.markers) || {};
  const capability = (summary && summary.capability) || {};

  const metrics = [
    {
      label: 'Managed memory',
      value: `${formatBytes(memory.managedStartBytes)} → ${formatBytes(memory.managedEndBytes)}`,
      detail: `peak ${formatBytes(memory.managedPeakBytes)} · delta ${formatDelta(memory.managedDeltaBytes)}`,
    },
    {
      label: 'Process memory',
      value: memory.processSupported && memory.processEndBytes !== null && memory.processEndBytes !== undefined
        ? `${formatBytes(memory.processStartBytes)} → ${formatBytes(memory.processEndBytes)}`
        : 'not observable',
      detail: memory.processSupported && memory.processEndBytes !== null && memory.processEndBytes !== undefined
        ? `${text(memory.processKind, 'unknown')} · peak ${formatBytes(memory.processPeakBytes)} · delta ${formatDelta(memory.processDeltaBytes)}`
        : text(memory.processUnsupportedReason, 'This platform does not expose a process resident/physical memory counter.'),
    },
    {
      label: 'Native heap',
      value: memory.nativeSupported && memory.nativeEndBytes !== null && memory.nativeEndBytes !== undefined
        ? `${formatBytes(memory.nativeStartBytes)} → ${formatBytes(memory.nativeEndBytes)}`
        : 'not observable',
      detail: memory.nativeSupported && memory.nativeEndBytes !== null && memory.nativeEndBytes !== undefined
        ? `${text(memory.nativeKind, 'unknown')} · peak ${formatBytes(memory.nativePeakBytes)} · delta ${formatDelta(memory.nativeDeltaBytes)}`
        : text(memory.nativeUnsupportedReason, 'This platform does not expose a native-heap-specific counter.'),
    },
    {
      label: 'GC collections',
      value: gc.supported
        ? `gen0 +${Number(gc.gen0Delta) || 0} · gen1 +${Number(gc.gen1Delta) || 0} · gen2 +${Number(gc.gen2Delta) || 0}`
        : 'not observable',
      detail: gc.supported ? 'Counted across the retained sample window.' : '',
    },
    {
      label: 'CPU',
      value: cpu.supported && cpu.averagePercent !== null && cpu.averagePercent !== undefined
        ? `avg ${formatNumber(cpu.averagePercent, '%')} · peak ${formatNumber(cpu.peakPercent, '%')}`
        : 'not observable',
      detail: '',
    },
    {
      label: 'Threads',
      value: threads.supported && threads.peakCount !== null && threads.peakCount !== undefined
        ? `peak ${threads.peakCount}`
        : 'not observable',
      detail: '',
    },
    {
      label: 'Frames',
      value: frames.supported
        ? `avg ${formatNumber(frames.averageFps)} fps · min ${formatNumber(frames.minimumFps)} fps`
        : 'not measured',
      detail: frames.supported
        ? `${[frames.source, frames.quality].filter(Boolean).join(' / ')} · ` +
          `p95 ${formatNumber(frames.frameTimeMsP95)} ms · worst ${formatNumber(frames.worstFrameTimeMs)} ms · ` +
          `jank ${frames.jankFrameCount === null || frames.jankFrameCount === undefined ? 'n/a' : frames.jankFrameCount} · ` +
          `stalls ${frames.uiThreadStallCount === null || frames.uiThreadStallCount === undefined ? 'n/a' : frames.uiThreadStallCount}`
        : text(frames.unsupportedReason, 'No native frame timing source is available.'),
    },
    {
      label: 'Markers',
      value: `${Number(markers.total) || 0} marker${(Number(markers.total) || 0) === 1 ? '' : 's'} · ${Number(markers.spanCount) || 0} span${(Number(markers.spanCount) || 0) === 1 ? '' : 's'}`,
      detail: `ui ${Number(markers.ui) || 0} · network ${Number(markers.network) || 0} · navigation ${Number(markers.navigation) || 0}`,
    },
  ];

  const warnings = Array.isArray(summary && summary.warnings) ? summary.warnings.map(String) : [];

  return {
    title: session.active ? 'Performance · recording' : 'Performance · stopped',
    active: !!session.active,
    session: `${Number(session.sampleCount) || 0} sample${(Number(session.sampleCount) || 0) === 1 ? '' : 's'} · ` +
      `${formatNumber((Number(session.sampledDurationMs) || 0) / 1000)} s · every ${Number(session.sampleIntervalMs) || 0} ms`,
    mode: `${text(capability.platform, 'unknown')} · mode ${text(capability.mode, 'unknown')}` +
      (capability.lowPerturbation ? ' · read-only profile build' : ''),
    perturbed: !capability.lowPerturbation,
    perturbationNote: capability.lowPerturbation
      ? 'Explicit profile mode: the agent is read-only and low-perturbation.'
      : 'Debug mode: Hot Reload, the debugger, and DevFlow diagnostics perturb these numbers. Compare runs, do not trust absolute values.',
    metrics,
    hotspots: (Array.isArray(summary && summary.hotspots) ? summary.hotspots : []).map((hotspot) => ({
      name: `${text(hotspot.kind)}/${text(hotspot.name)}`,
      screen: hotspot.screen ? String(hotspot.screen) : null,
      p95: formatNumber(hotspot.p95DurationMs, ' ms'),
      max: formatNumber(hotspot.maxDurationMs, ' ms'),
      count: Number(hotspot.count) || 0,
      errorCount: Number(hotspot.errorCount) || 0,
    })),
    warnings,
    limitations: Array.isArray(capability.limitations) ? capability.limitations.map(String) : [],
  };
}
