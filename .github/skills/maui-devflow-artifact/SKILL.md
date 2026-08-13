---
name: maui-devflow-artifact
description: >-
  Diagnose explicit DevFlow flow-run.json and .mauitrace artifacts through the
  bounded read-only trust projection. USE FOR: Copilot coding-agent CI evidence
  review, artifact sufficiency, failure classification, and local reproduction
  handoff. DO NOT USE FOR: live app control, replay, source edits, GitHub writes,
  implicit latest-artifact discovery, or treating imported evidence as trusted.
---

# MAUI DevFlow Artifact Diagnosis

1. Require an explicit local artifact path. Do not search for the latest file.
2. Call `maui_artifact_inspect` with `flow-run` or `mauitrace`.
3. Stop when the artifact is malformed, mismatched, untrusted, or insufficient.
4. Report trust state, omissions, evidence sufficiency, typed failure facts,
   run ID, and digest before discussing a likely code area.
5. Cite the supplied run ID and digest in each conclusion.
6. Hand local reproduction to the documented DevFlow verify/import/reproduce
   workflow. Never replay or apply a repair from imported evidence.

The tool is suitable for a Copilot coding-agent environment after a trusted
workflow downloads a named artifact. Copilot code review requires the same
read-only tool through a separately reviewed remotely reachable MCP service;
do not assume the review host can launch this local stdio server.
