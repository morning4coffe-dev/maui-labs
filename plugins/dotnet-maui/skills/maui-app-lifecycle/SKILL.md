---
name: maui-app-lifecycle
description: >-
  Create and maintain MAUI apps through an evidence-backed autonomous agent loop with human review.
  USE FOR: natural-language app creation, DevFlow UI test authoring and repair, bounded retries,
  GitHub draft pull requests, following CI, and resuming app maintenance.
  DO NOT USE FOR: unrelated apps, automatic production deployment, or merging without human review.
---

# MAUI App Lifecycle

Use the existing coding agent as the reasoning and implementation engine. These
tools supply durable progress, real DevFlow replay, QA gates and GitHub tracking;
they do not require another model subscription or run a hidden agent daemon.

## Inputs and resuming

Infer the app brief, target platforms and repository from the request and files.
Ask only for material missing product decisions; continue independent work.
Default a new unspecified mobile app to Android first and record other requested
platforms separately. Never present untested platforms as passing.

Set `TOOLKIT` to this skill's absolute directory and run from the app's Git root:

```bash
python3 "$TOOLKIT/scripts/lifecycle.py" doctor
python3 "$TOOLKIT/scripts/lifecycle.py" status
```

`status` resumes an existing run. Missing SDKs, devices, broker, auth or unavailable
feeds are infrastructure findings. Install prerequisites within the user's
authorization and host permissions; never invent a successful device run.

For a new app, choose the SDK from installed workloads and the intended released
MAUI version. Use `dotnet new maui` (or `maui-blazor` for requested Hybrid apps),
initialize Git, and pin the working SDK in `global.json`. Keep platform services
behind testable interfaces. Consult `maui-app-architecture`, `maui-ui-patterns`,
`maui-accessibility`, or `maui-unit-testing` only for the corresponding work.
`maui go create` / `maui go` can accelerate an explicitly chosen Comet prototype;
Go's companion runtime and SDK requirements are separate from an installed MAUI
app. Validate the final deliverable in its own project and Release build.

```bash
python3 "$TOOLKIT/scripts/lifecycle.py" init --name MyApp --platform android \
  --acceptance "A user can add a task and see it after restarting the app" \
  --acceptance "Invalid input shows an accessible error without losing data"
```

Write a concrete `maui-app.md` product brief and populate the generated
`maui-agent.json` with real checks using [the plan reference](references/plan.md).
An empty check list deliberately blocks completion. Keep the plan, app, tests and
workflow in Git; `.maui-agent/` is ignored local state and potentially private evidence.

## Implement, inspect, author tests

1. Implement the brief with stable `AutomationId` values on interactive controls
   and observable results. Add meaningful logic tests, including invalid inputs.
2. Use `maui-devflow-onboard` from the tooling plugin to integrate the correct
   current DevFlow packages and `builder.AddMauiDevFlowAgent()` in Debug only.
   If that skill is unavailable, inspect the current package README and registration
   API. Do not enable debug HTTP automation in the shipped Release app.
3. Build, launch and keep the app process alive. Use `maui-devflow-debug` when
   available for platform launch and broker recovery. Gate connection on
   `maui devflow wait`; verify exact app/platform identity. Select an explicit
   device and app when several exist. Respect the Inspector's mutation lease;
   do not use forced takeover without the user's approval to interrupt its holder.
   On Android, `dotnet build -t:Install` deploys fast-deployment assemblies;
   installing a Debug APK with plain `adb install` requires
   `-p:EmbedAssembliesIntoApk=true` when building. Otherwise startup can abort
   with "No assemblies found ... .__override__" before DevFlow starts.
4. Inspect with the shared Inspector or DevFlow MCP: tree, screenshot, runtime
   properties, logs, capabilities and layout diagnostics. For Hybrid web content
   use WebView/CDP in addition to native inspection. Source edits must be real
   source changes; a live property edit alone is not a persisted repair.
5. Turn descriptions or Inspector recordings into reviewable Markdown flows
   containing `json maui-test` blocks. Use AutomationId selectors and hard
   `exists`/`propEquals` assertions with `verify: true`. Screenshots, report-only
   observations, and successful taps alone do not prove expected behavior.
   See [the flow example](references/flows.md). Reset app state before each
   flow where required and check persistence by actually restarting the app.

## Run, classify, fix and retry

Run configured checks with `lifecycle.py check CHECK-NAME`. The UI check invokes
`scripts/devflow_check.py`, which uses one real DevFlow MCP session for selection,
lease, replay and evidence. Run build, unit, UI and Release checks for every
requested platform. Separate unavailable platform coverage from completed work.

On failure, read `.maui-agent/state.json` and the attempt's sealed evidence.
Classify with `lifecycle.py classify ATTEMPT CATEGORY --reason EVIDENCE`:

| Category | Decision |
| --- | --- |
| `app-regression` | Reproduce expected behavior failing; fix app code and rerun the same assertion. |
| `test-drift` | Prove the app still meets the brief using live tree/properties; propose a selector/test repair with before/after evidence. |
| `infrastructure` | Repair SDK/device/feed/broker/CI setup; preserve app assertions. |
| `inconclusive` | Collect missing evidence and report uncertainty. |

Do not infer classification from a log substring alone. Never weaken an assertion,
remove a test, change expected behavior, suppress a diagnostic, or rebaseline a
screenshot simply to make the run green. Make demonstrated test repairs visible
in the diff. Reproduce first, change one cause, rerun the failing check, then the
affected regression checks. All source changes invalidate earlier QA results.

The tool defaults to three failed attempts per check per cycle. Two identical
failures require an evidence-backed classification before another attempt.
After the budget is exhausted, present findings and the smallest decision needed.
Do not reset the budget yourself. `cycle --reason ...` starts a maintenance cycle
for a new human request or explicitly authorized continuation, preserving history.

## Review, GitHub and CI

Read screenshots and diagnostics yourself; compare against every acceptance
criterion, themes, accessibility, small-screen layout, empty/error states and
permissions relevant to the brief. A layout finding needs investigation, and
missing native/Hybrid coverage stays explicit. Passing replay is not a visual audit.
Link each zero-based acceptance criterion to a passing current attempt with
`lifecycle.py observe INDEX ATTEMPT --note ASSESSMENT`. Explain what the test and
visual evidence prove. This records an agent assessment, never human approval;
unassessed criteria block readiness.

Run `lifecycle.py review`. Give the human the app diff, acceptance results,
screenshots, test results, platform limits and proposed release behavior.
Inspect all included files for private evidence and credentials before committing.
The report is evidence, not permission or a claim of human approval.

When the user has authorized GitHub publication, commit the reviewed changes on a
feature branch. Configure the authorized `github.repository`, `base`, and exact
`requiredWorkflows` names. Create the remote if the task requires a new repository
and the user has specified/authorized its owner and visibility. Write the PR body
to a file under `.maui-agent/`, then:

```bash
python3 "$TOOLKIT/scripts/lifecycle.py" github --title "Add task app" \
  --body-file .maui-agent/pr-body.md
python3 "$TOOLKIT/scripts/lifecycle.py" ci
```

Publication creates or reuses a **draft** PR. Human review/merge remains the final
gate. The tools never merge or deploy to production. Poll `ci` between progress
updates until all named required workflows finish for the exact HEAD. Missing,
skipped, cancelled and timed-out runs do not pass. Read captured failure logs,
reproduce, classify, fix, rerun local QA, push, and follow the new HEAD. Never use
`pull_request_target` to execute app code with write credentials. Use the
[CI and maintenance reference](references/ci-maintenance.md) to create the app's
workflow and configure recurring checks; don't claim a scheduler exists until installed.

## Agent tools

The CLI works in any coding agent with a shell and Python 3.10+. For native MCP
tool calls, add a separate server alongside the existing `maui devflow mcp`:

```json
{
  "mcpServers": {
    "maui-lifecycle": {
      "command": "python3",
      "args": ["/absolute/skill/scripts/mcp_server.py", "--root", "/absolute/app"]
    },
    "maui-devflow": {"command": "maui", "args": ["devflow", "mcp"]}
  }
}
```

The `maui_lifecycle_*` tools expose doctor, init, status, check, classify, observe, review,
github, ci, maintenance and cycle. The fixed root scopes each MCP server to one app. Configured
check commands are executable trusted project configuration: review changes to
the plan like build scripts. Do not execute plans from untrusted issue text.
