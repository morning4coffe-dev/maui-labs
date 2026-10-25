# Agent-led MAUI app lifecycle

The `maui-app-lifecycle` skill in the `dotnet-maui` plugin combines existing
MAUI tooling with a durable, evidence-gated agent workflow. A coding agent
implements the product brief; Python tools track checks, acceptance evidence,
review packets and GitHub CI. Humans review changes and decide what merges.

## Tooling assessment

The implementation was based on upstream `main` at
`391101d3f39c747cf6c23253e669382309083618` (2026-10-24).

| Existing surface | Reused responsibility |
| --- | --- |
| `maui doctor`, workload/platform/device commands | Environment and device diagnosis; distinguish setup failures from app regressions. |
| `maui go` and companion app | Optional Comet prototype loop; final app validation uses its own project/runtime. |
| DevFlow agent, broker and portable client | Live app identity, readiness, visual trees, runtime properties, actions and logs. |
| `maui devflow mcp` | Existing UI actions and hard-assertion flow replay; one session preserves mutation ownership. |
| Shared Inspector across browser/VS Code/Canvas | Human/agent shared selection, screenshots, source context, recording and reviewable edits. |
| Layout diagnostics and native inspection | Evidence of rendered problems with capability/platform limits recorded explicitly. |
| `maui ai` and plugin marketplace | Existing distribution and discovery of app-building and specialist skills. |
| GitHub CLI and Actions | Draft PR publication, exact-HEAD required-workflow tracking and failed logs. |

The new layer supplies the missing durable lifecycle: source fingerprints,
sealed attempt evidence, meaningful test-result gates, acceptance assessments,
failure classification, retry budgets, resumability and human handoff. It does
not introduce a replacement device driver or a second LLM orchestration service.
Independent Android Release checks use Appium/UIAutomator2 alongside DevFlow's
Debug inspection; a small adapter records native evidence in the same lifecycle.

## Use

Install the app-development plugin and, for specialist setup/recovery, the
tooling plugin. See [plugin installation](../../plugins/README.md).
Ask the agent to use
[`maui-app-lifecycle`](../../plugins/dotnet-maui/skills/maui-app-lifecycle/SKILL.md)
to create an app from a brief or repair an existing app/CI issue.

The skill includes command-line tools and an optional stdio MCP server exposing
`maui_lifecycle_doctor`, `init`, `status`, `check`, `classify`, `observe`, `review`,
`github`, `ci`, `maintenance` and `cycle` (each with the `maui_lifecycle_` prefix). The agent
uses normal source editing and the existing DevFlow tools to perform the work.
Python 3.10+ is the only extra tool runtime; no pip packages are required.

The plan and acceptance brief live in Git. Private local progress/evidence lives
under ignored `.maui-agent/`. An app edit invalidates earlier checks even if HEAD
is unchanged. Unit checks require a fresh, nonempty passing TRX. UI checks require
real evaluated assertions, app identity and captured evidence. A draft PR needs
current complete checks, acceptance assessments, a review packet and committed
changes on the authorized feature branch.

## Qualification and limits

Run the behavioral suite from the repository root:

```bash
python3 -m unittest discover -s tests/dotnet-maui/maui-app-lifecycle -p 'test_*.py' -v
```

It exercises real temporary Git repositories and child processes: freshness,
missing/empty reports, tampered evidence, failures retaining artifacts, process
timeouts, retry budgets, acceptance links, MCP transport and exact-commit CI
selection. The skill-check workflow runs it on Linux, Windows and macOS.
These tests do not substitute for real MAUI runtime qualification.

The device qualification creates a separate MAUI counter app, reproduces an
intentional `+2` increment through both xUnit and Android DevFlow replay, fixes
the app to `+1` while preserving assertions, and reruns QA. Environment and
coverage results are recorded in the qualification report accompanying the PR.
See [the qualification results and remaining limits](autonomous-app-lifecycle-qa.md).
The [broader app qualification](autonomous-app-lifecycle-qa-2026-10-25.md) adds an
offline task app, native dialogs, process persistence, repeated Release tests
and appearance/font/orientation variants. Its research and reusable test recipe
live in the skill's [testing strategy](../../plugins/dotnet-maui/skills/maui-app-lifecycle/references/testing-strategy.md).

The workflow does not automatically infer that a failure is test drift, weaken
tests, approve its own work, merge PRs or deploy production. Local state is an
audit trail rather than a security boundary against a writer of that state.
Submodules are explicitly unsupported by source fingerprinting. Log redaction
is best effort; screenshots/app data require privacy review before publication.

Scheduled CI provides recurring validation after installation on a default
branch. Unattended issue-to-fix work requires a separately configured coding-agent
host and its authorization. Do not describe an unconfigured scheduler, unavailable
device or untested platform as completed coverage.
