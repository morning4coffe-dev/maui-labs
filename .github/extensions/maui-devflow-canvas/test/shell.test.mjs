import assert from "node:assert/strict";
import test from "node:test";
import { renderDisconnected, renderShell } from "../shell.mjs";

function nonceOf(html) {
  return html.match(/<script nonce="([^"]+)">/)?.[1] || "";
}

test("Canvas shell uses an exact frame origin and a separate CSP nonce", () => {
  const bridgeId = "bridge-secret";
  const first = renderShell("http://localhost:19223/inspector/app/?embed=token", "App", bridgeId);
  const second = renderShell("http://localhost:19223/inspector/app/?embed=token", "App", bridgeId);
  const nonce = nonceOf(first);

  assert.match(first, /const frameOrigin = "http:\/\/localhost:19223";/);
  assert.match(first, /if \(e\.origin !== frameOrigin\) return;/);
  assert.doesNotMatch(first, /postMessage\([\s\S]{0,500},\s*['"]\*['"]\)/);
  assert.ok(first.includes(`#devflowBridge=${bridgeId}`));
  assert.notEqual(nonce, bridgeId);
  assert.equal(first.match(/script-src 'nonce-([^']+)'/)?.[1], nonce);
  assert.notEqual(nonceOf(second), nonce);
});

test("Disconnected shell rotates its script nonce", () => {
  assert.notEqual(nonceOf(renderDisconnected("App")), nonceOf(renderDisconnected("App")));
});

test("Canvas shell relays test saves and direct agent requests without adding replay semantics", () => {
  const html = renderShell("http://localhost:19223/inspector/app/?embed=token", "App", "bridge-secret");

  assert.match(html, /"protocol":\{"version":2,"minimumVersion":1,"maximumVersion":2\}/);
  assert.match(html, /"hostId":"canvas"/);
  assert.match(html, /"interactionSessionId":"bridge-secret"/);
  assert.match(html, /"capabilityDescriptors":\[/);
  assert.match(html, /'saveTestBundle'/);
  assert.match(html, /action: 'saveTestBundle', bundle: d\.bundle/);
  assert.match(html, /'requestTestProposal'/);
  assert.match(html, /devflow:requestTestProposal/);
  assert.match(html, /action: 'requestTestProposal', prompt: d\.prompt/);
  assert.match(html, /devflow:hostResult/);
  assert.doesNotMatch(html, /devflow:loadTestBundle/);
  assert.doesNotMatch(html, /const capabilities = \[[^\]]*'attachTestContext'/);
  assert.doesNotMatch(html, /devflow:attachTestContext/);
});

test("Canvas shell advertises native approval and never posts after a human cancellation", () => {
  const html = renderShell("http://localhost:19223/inspector/app/?embed=token", "App", "bridge-secret");
  assert.match(html, /"nativeApproval"/);
  assert.match(html, /d\.type === 'devflow:nativeApproval'/);
  assert.match(html, /window\.confirm/);
  assert.match(html, /if \(!confirmed\)[\s\S]{0,500}cancelled: true/);
  assert.match(html, /fetch\('\/native-approval'/);
  assert.match(html, /JSON\.stringify\(\{ bridgeId: bridgeId, approval: approval \}\)/);
});
