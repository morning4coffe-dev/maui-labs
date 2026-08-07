import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const sourcePath = new URL('../src/extension.ts', import.meta.url);
const source = await readFile(sourcePath, 'utf8');

function normalizeAgentIdentityPart(value) {
  const trimmed = typeof value === "string" ? value.trim() : "";
  return trimmed ? trimmed : null;
}

function selectRefreshedAgent(agents, current) {
  const sameIdentityMatches = agents.filter((candidate) =>
    normalizeAgentIdentityPart(candidate.project) === normalizeAgentIdentityPart(current.project) &&
    normalizeAgentIdentityPart(candidate.tfm) === normalizeAgentIdentityPart(current.tfm) &&
    normalizeAgentIdentityPart(candidate.platform) === normalizeAgentIdentityPart(current.platform) &&
    normalizeAgentIdentityPart(candidate.appName) === normalizeAgentIdentityPart(current.appName)
  );
  const currentId = normalizeAgentIdentityPart(current.id);
  if (currentId) {
    const exactIdMatches = sameIdentityMatches.filter((candidate) => normalizeAgentIdentityPart(candidate.id) === currentId);
    if (exactIdMatches.length === 1) return exactIdMatches[0];
    if (exactIdMatches.length > 1) return null;
  }

  return sameIdentityMatches.length === 1 ? sameIdentityMatches[0] : null;
}

function requiresBridgeRequestId(type) {
  return type === 'devflow:sendToCopilot' ||
    type === 'devflow:attachCopilot' ||
    type === 'devflow:requestTestProposal' ||
    type === 'devflow:pickWorkflow' ||
    type === 'devflow:attachData' ||
    type === 'devflow:openSource' ||
    type === 'devflow:recordingComplete' ||
    type === 'devflow:saveTestBundle' ||
    type === 'devflow:loadTestBundle' ||
    type === 'devflow:pickTrace' ||
    type === 'devflow:openSourceDiff' ||
    type === 'devflow:applySourceProposal' ||
    type === 'devflow:applyCSharpSourceProposal' ||
    type === 'devflow:getCSharpSourceSelection';
}

test('state is panel-scoped and retainContextWhenHidden survives refresh', () => {
  assert.match(source, /const panelStates = new WeakMap<[^>]+>\(\)/);
  assert.match(source, /let activePanelState: PanelBridgeState \| null = null;/);
  assert.ok(!/currentSelection/.test(source));
  assert.ok(!/currentDataSnapshot/.test(source));
  assert.ok((source.match(/retainContextWhenHidden: true/g) ?? []).length >= 2);
});

test('reconnect selection fails closed on duplicate matches', () => {
  const current = { id: 'a', project: '/work/apps/demo/Demo.csproj', tfm: 'net10.0-windows10.0.19041.0', platform: 'windows', appName: 'Demo' };
  assert.equal(selectRefreshedAgent([{ id: 'a', project: '/work/apps/demo/Demo.csproj', tfm: 'net10.0-windows10.0.19041.0', platform: 'windows', appName: 'Demo' }], current)?.id, 'a');
  assert.equal(selectRefreshedAgent([{ id: 'b', project: '/work/apps/demo/Demo.csproj', tfm: 'net10.0-windows10.0.19041.0', platform: 'windows', appName: 'Demo' }], current)?.id, 'b');
  assert.equal(selectRefreshedAgent([{ id: 'b', project: '/work/apps/demo/Demo.csproj', tfm: 'net10.0-windows10.0.19041.0', platform: 'windows', appName: 'Demo' }, { id: 'c', project: '/work/apps/demo/Demo.csproj', tfm: 'net10.0-windows10.0.19041.0', platform: 'windows', appName: 'Demo' }], current), null);
  assert.equal(selectRefreshedAgent([{ id: 'a', project: '/work/apps/demo/Demo.csproj', tfm: 'net10.0-windows10.0.19041.0', platform: 'windows', appName: 'Demo' }, { id: 'b', project: '/work/apps/demo/Demo.csproj', tfm: 'net10.0-windows10.0.19041.0', platform: 'windows', appName: 'Demo' }], current)?.id, 'a');
});

test('reconnect selection rejects same appName across different project/platform', () => {
  const current = { id: 'a', project: '/work/apps/demo/Demo.csproj', tfm: 'net10.0-windows10.0.19041.0', platform: 'windows', appName: 'Demo' };
  assert.equal(selectRefreshedAgent([{ id: 'b', project: '/work/apps/other/Other.csproj', tfm: 'net10.0-android', platform: 'android', appName: 'Demo' }], current), null);
  assert.equal(selectRefreshedAgent([{ id: 'b', project: '/work/apps/demo/Demo.csproj', tfm: 'net10.0-android', platform: 'android', appName: 'Demo' }], current), null);
});

test('reconnect selection accepts the unique same-identity replacement', () => {
  const current = { id: 'a', project: '/work/apps/demo/Demo.csproj', tfm: 'net10.0-windows10.0.19041.0', platform: 'windows', appName: 'Demo' };
  assert.equal(selectRefreshedAgent([
    { id: 'b', project: '/work/apps/demo/Demo.csproj', tfm: 'net10.0-windows10.0.19041.0', platform: 'windows', appName: 'Demo' },
    { id: 'c', project: '/work/apps/other/Other.csproj', tfm: 'net10.0-windows10.0.19041.0', platform: 'windows', appName: 'Demo' },
  ], current)?.id, 'b');
});

test('request-gated bridge actions are enumerated', () => {
  assert.equal(requiresBridgeRequestId('devflow:attachData'), true);
  assert.equal(requiresBridgeRequestId('devflow:requestTestProposal'), true);
  assert.equal(requiresBridgeRequestId('devflow:selectionChanged'), false);
  assert.equal(requiresBridgeRequestId('devflow:openSource'), true);
  assert.match(source, /case "devflow:requestTestProposal":/);
  assert.match(source, /isPartialQuery: false/);
  assert.match(source, /'requestTestProposal'/);
});
