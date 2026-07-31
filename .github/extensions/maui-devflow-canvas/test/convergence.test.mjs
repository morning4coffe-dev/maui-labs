import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const source = readFileSync(new URL("../extension.mjs", import.meta.url), "utf8");

test("Canvas production replay delegates to the shared CSharp Inspector engine", () => {
  assert.doesNotMatch(source, /import\s+\{\s*replayTest\s*\}\s+from\s+["']\.\/replay\.mjs["']/);
  assert.match(source, /st\.recorder\.load/);
  assert.doesNotMatch(source, /\/api\/flows\/files\/load/);
  assert.match(source, /\/api\/flows\/replay/);
  assert.match(source, /replaySharedFlow/);
  assert.match(source, /canonical C# FlowReplayer/);
});
