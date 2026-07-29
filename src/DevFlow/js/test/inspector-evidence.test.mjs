import assert from "node:assert/strict";
import test from "node:test";

import {
  buildCaptureBody,
  evidenceFileName,
  formatEvidencePlan,
} from "../../../Cli/Microsoft.Maui.Cli/DevFlow/Inspector/Web/inspector-evidence.js";

const plan = {
  schema: "maui-devflow-evidence",
  formatVersion: 1,
  redactionVersion: 1,
  source: "inspector",
  app: { name: "Sample App" },
  platform: { name: "Windows" },
  included: [
    { name: "manifest.json", description: "Bundle description", bytes: 900 },
    { name: "tree.json", description: "Element structure", count: 42, bytes: 1200 },
  ],
  excluded: [{ name: "screenshot.png", reason: "Screenshots are opt-in and were not requested." }],
  neverIncluded: ["Element Text/Value content", "Preferences and secure storage values"],
  screenshot: { requested: false, included: false, omittedReason: "Screenshots are opt-in and were not requested." },
  counts: { treeElements: 42, problems: 3, logs: 120, networkRequests: 8 },
  limits: { logs: 200, network: 100, treeElements: 5000 },
  warnings: ["Network capture unavailable: capture is off."],
  suggestedFileName: "SampleApp-20260729-112233.mauitrace",
};

test("plan preview lists inclusions, exclusions, and never-captured classes", () => {
  const view = formatEvidencePlan(plan);

  assert.equal(view.title, "Share evidence from Sample App · Windows");
  assert.match(view.summary, /42 elements/);
  assert.match(view.summary, /3 problems/);
  assert.match(view.summary, /8 request summaries/);
  assert.match(view.limits, /200 logs/);
  assert.equal(view.redaction, "Format v1 · redaction ruleset v1");
  assert.deepEqual(view.includes.map((entry) => entry.name), ["manifest.json", "tree.json"]);
  assert.match(view.includes[1].detail, /Element structure/);
  assert.deepEqual(view.excludes.map((entry) => entry.name), ["screenshot.png"]);
  assert.equal(view.never.length, 2);
  assert.deepEqual(view.warnings, ["Network capture unavailable: capture is off."]);
});

test("screenshot stays opt-out in the preview by default", () => {
  const view = formatEvidencePlan(plan);

  assert.equal(view.screenshotRequested, false);
  assert.match(view.screenshotNote, /opt-in/);
});

test("screenshot note warns when a screenshot will be included", () => {
  const view = formatEvidencePlan({
    ...plan,
    screenshot: { requested: true, included: true },
  });

  assert.equal(view.screenshotRequested, true);
  assert.match(view.screenshotNote, /may show on-screen data/);
});

test("a malformed plan degrades instead of throwing", () => {
  const view = formatEvidencePlan(null);

  assert.deepEqual(view.includes, []);
  assert.deepEqual(view.excludes, []);
  assert.deepEqual(view.never, []);
  assert.equal(view.title, "Share evidence bundle");
  assert.match(view.fileName, /\.mauitrace$/);
});

test("download names are sanitized and never escape a directory", () => {
  assert.equal(evidenceFileName(plan), "SampleApp-20260729-112233.mauitrace");
  assert.equal(
    evidenceFileName({ suggestedFileName: "../../etc/evil.mauitrace" }),
    "evil.mauitrace",
  );
  assert.equal(
    evidenceFileName({ suggestedFileName: "C:\\Windows\\system32\\evil.mauitrace" }),
    "evil.mauitrace",
  );
  assert.match(evidenceFileName({ suggestedFileName: "report.html" }), /^devflow-.*\.mauitrace$/);
  assert.match(evidenceFileName({}), /^devflow-.*\.mauitrace$/);
});

test("the capture body carries only what the dialog confirmed", () => {
  const workflow = "1. Fill Password = \"hunter2\"";

  const declined = buildCaptureBody({
    choice: { includeScreenshot: false, includeWorkflow: false },
    elementId: "e1",
    workflow,
  });
  assert.deepEqual(declined, { includeScreenshot: false, elementId: "e1" });

  const accepted = buildCaptureBody({
    choice: { includeScreenshot: true, includeWorkflow: true },
    elementId: null,
    workflow,
  });
  assert.deepEqual(accepted, { includeScreenshot: true, workflow });

  // A confirmed opt-in with nothing loaded must not invent a workflow field.
  assert.deepEqual(
    buildCaptureBody({ choice: { includeWorkflow: true }, elementId: null, workflow: "   " }),
    { includeScreenshot: false },
  );
  assert.deepEqual(buildCaptureBody({}), { includeScreenshot: false });
});
