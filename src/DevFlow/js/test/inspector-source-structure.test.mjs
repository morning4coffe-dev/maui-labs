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
