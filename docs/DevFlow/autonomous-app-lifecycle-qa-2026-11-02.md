# Autonomous lifecycle QA — November 2–3, 2026

The toolkit is improved and locally tested; end-to-end app qualification remains
blocked by Android host/device health and GitHub workflow startup. This is not a
claim that arbitrary MAUI apps or unattended maintenance are fully qualified.

## Current foundation

Merged upstream `main` through `e2f4a89e8d3ccc0ead6fabef2a4f43bdd241ce82`
(October 31). This includes Inspector retry, source-preservation and preview
fixes, native dialog tooling and background-job capability corrections. The
Android CI template now pins that revision. No SDK/workload upgrade was installed.
The fork's qualification base was fast-forwarded to the same upstream revision.

## Improvements and feature boundaries

| Stage | Implemented surface | Qualification boundary |
| --- | --- | --- |
| Brief and creation | Lifecycle skill, product acceptance criteria, real MAUI template, SDK pin, durable plan | The coding agent writes the app; the helper is not a hidden LLM service. |
| Development | Shared Inspector, live identity, mutation leases, source context and scoped literal refresh | Previous two-way same-PID literal refresh remains historical evidence; standard IDE/C#/structural Hot Reload is unqualified. |
| Resumption | New `next` CLI/MCP queue includes dependencies, consecutive passes, remaining failure budgets and concrete next actions | Read-only; it does not infer fixes, reset cycles or record human approval. |
| Check execution | New `advance` CLI/MCP runs 1–10 eligible checks, stops at first failure and resumes from sealed evidence | Existing same-source failures require inspection and an explicit retry. Batch success is separate from app readiness. |
| Device preflight | Explicit Android selection, service/window checks, screen/keyguard policy, complete PNG | Storage unlock cannot hide visible keyguard. Unknown/asleep/conflicting policy stops readiness. It does not satisfy product QA. |
| App QA | Unit/TRX, Debug DevFlow replay, repeated independent Release Appium checks, variants, binary identity and source freshness | Current PocketTasks UI checks remain unavailable. Other platforms/physical devices are not qualified here. |
| Repair | Evidence classification, bounded retries, original expectations and fresh-source regression checks | The agent diagnoses and edits; classification alone is not proof of a repair. |
| Human review | Acceptance observations and current review packet; explicit review/commit/CI/human boundaries in queue | Neither batching nor MCP can approve acceptance, merge or release. |
| GitHub and CI | Authorized draft PR publication, exact-HEAD required workflows, failure logs/startup annotations | GitHub still rejects workflow dispatch; no hosted run was created. |
| Maintenance | Versioned plan/tests, scheduled CI asset, trusted failure-issue intake and maintenance query using the actual remote default branch | Assets require installation/default-branch merge. No unattended coding-agent host or scheduler was installed. |

Android UI batching requires a check-level `device` declaration matching both
`advance --device` and the reviewed command. Every such check gets a fresh
readiness probe before consuming an app-test attempt. Existing explicit `check`
calls remain compatible. Other-platform UI execution stays explicit.

## Fresh local evidence

- Lifecycle behavior and real subprocess/MCP transport: **79 passed**. Includes
  dependency ordering, repeated-run progress, first-failure stopping, refusal to
  automatically retry a classified failure, budgets across source edits,
  mismatched devices, unhealthy readiness without spending attempts, acceptance
  boundaries and missing/failed/passed exact-HEAD CI.
  A recovered but unexplained failure routes back to repair, not acceptance.
  Batch readiness reports keep unique directories, and maintenance does not
  mistake a PR/release base for the repository's actual default branch.
- Current-main CLI build succeeds. Selected native-dialog/job/SDK regressions:
  **77 passed**. Inspector command/project scan/host operations: **13 passed**.
- Inspector JavaScript suites: **105 passed, 1 skipped**, no failures.
- Whole-repository npm audit: all three projects verified, **no high/critical
  advisories**. This does not claim zero advisories of every severity.
  Clean Comet npm installation also succeeds; it has no npm test script.
- Skill validation, YAML parsing, Python compilation, shell syntax and whitespace
  checks pass. New prompt-evaluation scenarios are authored, not model-evaluated.
- Real DevFlow stdio MCP advertises **86 tools**, including native dialog
  detect/respond and fold capabilities/state. Discovery does not qualify native
  execution. Native prompt response keeps upstream user-confirmation requirements.
- Real lifecycle MCP resumes the existing PocketTasks plan and returns a
  structured `prepare-runtime` result without executing UI or spending attempts.
  An ordinary stopped batch is now a structured tool result; invalid arguments
  remain MCP errors.
- Real maintenance MCP resolves the qualification repository's default branch
  as `main`, despite its different configured PR base. It returns zero issues
  and zero default-branch runs, and leaves the app source unchanged. This is
  successful read-only intake, not proof of an installed maintenance worker.
- PocketTasks host tests rerun: **16 passed**, zero skips/failures, source
  `0af2a1c6b42bb3fc7c9ce4951957dd93182af30690375963657fbdc7edcdee35`.
  Its previously passing Debug/Release builds remain unchanged; they were not
  rerun or relabeled as fresh native UI validation.

Inspector browser qualification: **39 passed, zero skipped/failed**, in 6m42s.
These fixtures exercise the real Inspector HTTP listener and shipped JavaScript
against isolated loopback fixture agents: recovery, property/preview updates,
layout and shared-UI synchronization. They cannot establish native app behavior.
The initial `--no-restore` build found missing test-project assets (NETSDK1004);
restoring the project resolved setup. The subsequent build succeeds with zero
warnings/errors, and the matching Chromium installation succeeds.

## Blockers and preserved state

The old owned emulator process remains in uninterruptible exit state; ADB reports
its selected target `emulator-5556` offline. The read-only MCP readiness probe
rejects it at `connection`. The other owned qualification AVD remains stopped.
No unrelated applications/devices were stopped, global security settings changed,
or retry budgets reset. Fresh device tests need host recovery or a healthy target.

GitHub reports Actions permission enabled, but an actual `skill-check` dispatch
still returns HTTP 422, “Actions has been disabled for this repository.” No
account/billing cause is inferred from that contradiction. Hosted CI is blocked
until the service/repository owner resolves startup.

PocketTasks remains committed locally at
`29de606be85ac4ae54a6e89633736b2b6e1ff17c`; its unqualified app changes were not
pushed. Cycle 3 and the remaining failure allowances are preserved. Its frozen
plan lacks the new optional batch device declarations: explicit checks remain
usable after target recovery. Adding declarations or copying updated helpers
changes source and requires the complete QA set again.
Go companion graduation, physical-device behavior, other MAUI heads, screen
readers and permission-heavy app scenarios remain outside this qualification.

The ignored app evidence includes `resumption-20261102.json`,
`readiness-20261102/report.json` and the new host-test attempt
`0888ff51a25943ccb7d883885519ca4d`. Toolkit logs and TRX files are under ignored
`artifacts/qa/`; no private runtime evidence is published with this report.

See the [previous qualification](autonomous-app-lifecycle-qa-2026-10-28.md) for
the historical literal-refresh demonstration, native assertions and earlier
failure evidence. Research adopted from Playwright, Maestro and AOSP is linked
in the [testing strategy](../../plugins/dotnet-maui/skills/maui-app-lifecycle/references/testing-strategy.md).
