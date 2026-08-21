import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const here = dirname(fileURLToPath(import.meta.url));
const sourcePath = resolve(
  here,
  '../../../Cli/Microsoft.Maui.Cli/DevFlow/Inspector/Web/devflow.js');
const source = readFileSync(sourcePath, 'utf8');
const html = readFileSync(resolve(
  here,
  '../../../Cli/Microsoft.Maui.Cli/DevFlow/Inspector/Web/inspector.html'), 'utf8');
const evidence = readFileSync(resolve(
  here,
  '../../../Cli/Microsoft.Maui.Cli/DevFlow/Inspector/Web/inspector-evidence.js'), 'utf8');
const steps = readFileSync(resolve(
  here,
  '../../../Cli/Microsoft.Maui.Cli/DevFlow/Inspector/Web/inspector-steps.js'), 'utf8');
const todoService = readFileSync(resolve(
  here,
  '../../../../samples/DevFlow.Sample/TodoService.cs'), 'utf8');
const todoItem = readFileSync(resolve(
  here,
  '../../../../samples/DevFlow.Sample/TodoItem.cs'), 'utf8');

test('Inspector composition script parses as an ES module', () => {
  const result = spawnSync(process.execPath, ['--check', sourcePath], { encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr || result.stdout);
});

test('Problems renderer is a top-level dock renderer, not nested inside Logs', () => {
  const problems = source.indexOf('  function renderProblems(j) {');
  const logs = source.indexOf('  function renderLogs(j) {');
  const network = source.indexOf('  function networkSignature(');

  assert.ok(problems >= 0);
  assert.ok(logs > problems);
  assert.ok(network > logs);
  assert.equal(source.slice(logs, network).includes('function renderProblems'), false);
});

test('workflow rebuild resume presents a stored recording capability', () => {
  assert.match(source, /recordingCapabilityStorageKey = 'maui-devflow-recording-capabilities-v1'/);
  assert.match(
    source,
    /body: JSON\.stringify\(candidate \? \{ recordingId: candidate \} : \{\}\)/);
  assert.doesNotMatch(source, /requestRecordingStatus\(null\)/);
});

test('Inspector accessibility structure keeps hidden inputs out of tab order', () => {
  assert.match(html, /id="df-workflow-file-input"[^>]*tabindex="-1"[^>]*aria-hidden="true"/);
  assert.match(html, /id="df-hit-candidates"[^>]*role="group"/);
  assert.doesNotMatch(source, /setAttribute\('role', 'option'\)/);
  assert.match(html, /id="df-problems-status"[^>]*aria-live="polite"/);
});

test('Layout discovery uses a smart top-level toolbar entry and shared cached state', () => {
  assert.match(
    html,
    /id="df-layout-entry"[\s\S]*id="df-toolbar-secondary"[\s\S]*id="df-toggle-fit"/);
  assert.match(html, /id="df-layout-entry-status"[^>]*data-state="idle"[^>]*>Start</);
  assert.match(source, /function openLayoutDiagnostics\(options = \{\}\)/);
  assert.match(source, /propertyGrid\.refreshDiagnostics\(\)/);
  assert.match(source, /openLayoutDiagnostics\(\{ forceScan: !latestLayoutReport \}\)/);
  assert.match(source, /if \(options\.forceScan === true\)[\s\S]*await runLayoutScan\(\)/);
  assert.doesNotMatch(source, /function renderLegacyLayoutDiagnostics/);
  assert.ok(source.indexOf('  function renderLayoutDiagnostics() {') <
    source.indexOf('  async function runLayoutScan() {'));
  assert.match(source, /tab\.textContent = latestLayoutReport && issues\.length \? `Layout \(\$\{issues\.length\}\)` : 'Layout'/);
  assert.match(source, /title: layoutScanBusy \? 'Checking layout' : layoutError \? 'Layout check unavailable' : 'Layout not checked'/);
  assert.match(html, /id="df-dock-panel" role="tabpanel"[\s\S]*id="df-dock-action-strip"[\s\S]*id="df-dock-body" role="region"/);
  assert.doesNotMatch(source, /class: 'df-layout-strip'/);
  assert.doesNotMatch(source, /class: 'df-performance-strip'/);
});

test('Evidence dialog redirects tab focus back inside when focus escapes', () => {
  assert.match(evidence, /if \(!box\.contains\(document\.activeElement\)\)/);
});

test('workflow assertions never read Text from password entries', () => {
  assert.match(steps, /looksSensitive/);
  assert.match(steps, /Expected value \(never prefilled\)/);
  assert.match(steps, /Never copy its[\s\S]*current value into an authored assertion/);
});

test('sample repeated items expose deterministic stable test identity', () => {
  assert.match(todoService, /_nextItemId = 1/);
  assert.match(todoService, /\$"todo-\{_nextItemId\+\+:D4\}"/);
  assert.match(todoItem, /IDevFlowStableItemKey/);
  assert.match(todoItem, /DevFlowStableItemKey => Id/);
});
