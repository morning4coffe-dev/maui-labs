# Conversational DevFlow collaborative testing

> **Experimental preview.** The conversational layer helps an AI agent prepare
> safe, reviewable DevFlow test work. It does not change the restricted
> test-agent protocol, authorize operations, or make an agent a test owner.

The optional `maui-devflow-test` skill is distributed in the `dotnet-maui`
plugin and bundled with the `maui` CLI. It is intentionally not part of
`maui devflow init`; install it explicitly with:

```text
maui devflow skills install
```

## First-time setup

Before authoring or running a conversational test, start a **Debug** build of
the app with the DevFlow agent connected. The Workbench and restricted agent
authoring surfaces are opt-in. Set both environment variables in the process
environment that starts the broker/Inspector:

```powershell
$env:DEVFLOW_PREVIEW_WORKBENCH = 'true'
$env:DEVFLOW_PREVIEW_AGENT_AUTHORING = 'true'
```

The advanced proposal/import surfaces are separate, optional opt-ins; enable
only the one needed:

```powershell
$env:DEVFLOW_PREVIEW_REPAIR_PROPOSALS = 'true'
$env:DEVFLOW_PREVIEW_SOURCE_PROPOSALS = 'true'
$env:DEVFLOW_PREVIEW_TRACE_IMPORT_EXPORT = 'true'
```

Restart the broker and Inspector after changing these variables, then open
`maui devflow inspect` and confirm that **Tests** is visible. A kill switch
that names `workbench` or `agent-authoring` in `DEVFLOW_PREVIEW_KILL_SWITCHES`
keeps the corresponding surface unavailable.

Install to the project skill directory used by the agent host. For example,
GitHub uses `.github/skills`:

```text
maui devflow skills install --scope project --target github
```

The target presets are `github` (`.github/skills`), `claude`
(`.claude/skills`), `agent` (`.agent/skills`), and `agents`
(`.agents/skills`). `--target auto` first reuses a directory containing a
current DevFlow skill, then any existing skill directory, and otherwise falls
back to `claude`. Specify the target explicitly when the project uses more
than one host.

The skill is for a conversation that may author an inert draft, guide a human
review, explain a run result, discuss a selector-repair handoff, recommend
application testability improvements, or summarize CI evidence. It works with
the restricted profile:

```text
maui devflow mcp --profile test-agent
```

See [Restricted DevFlow test-agent protocol](test-agent.md) for the actual
tool inventory, grants, and enforcement rules, and [human-authored testing](testing.md)
for the underlying flow lifecycle.

## Conversational contract

The skill asks questions only when an answer is necessary to bind the work
safely. It asks when several projects, devices, agents, artifacts, or durable
selectors match; when a repeated item needs a stable key; when a required
business oracle or reset policy is absent; or when a requested platform/action
is unavailable. It must never silently choose the first candidate.

A chat response is not authorization. Trusted VS Code Inspector and GitHub
Copilot Canvas hosts can issue opaque, short-lived grants for the exact process,
plan/flow revision and digest, selector/action scope, build/seed state, and
operation after an explicit native confirmation. Standalone browser and chat
remain non-authoritative, so the skill must stop at inert work or a pending
request when the `nativeApproval` host capability is absent. The skill cannot
manufacture, reuse, or broaden grants.

## Authoring and execution

Restricted MCP authoring is broker-owned and inert. The agent can prepare a
complete plan, actions, assertions, and review request, but it cannot broadly
automate the app, edit source, or commit without a human-issued commit grant.
Only the resulting committed Markdown flow is executable, and it requires a
separate human-approved run grant. Without a trusted host advertising
`nativeApproval`, stop at the inert draft or pending request.

For a repeatable or mutating flow, the plan must accurately declare its
side-effect and reset/seed policy. `test-tenant-resettable` is valid only when
the host really provides and verifies that reset/seed contract. A
`non-replayable` flow needs a distinct one-shot grant; it cannot be retried,
continued, or used for repair validation automatically.

A UI transition, toast, screenshot, or report does not independently prove a
business result. Where a verified pass or repair requires one, declare and
check an independent business oracle. An ordinary policy-permitted replay
without such an oracle must be reported as not independently verified.

## Failures, repairs, and testability

The conversation verifies route and recorded checkpoint facts before calling a
missing element selector drift. Wrong route, modal, build, seed, locale,
window, capability, assertion, or ambiguity failures are not selector
repairable.

The agent can keep an inert selector proposal or preview through
`maui_test_patch`, but cannot approve or apply it. Any repair requires a fresh
current local reproduction (including for imported evidence), then the separate
human Workbench validation, approval, apply, and verification ceremony.

Recommendations such as adding a unique app-owned `AutomationId`, stable
collection item key, reset provider, or business oracle are **testability
improvements**, not repairs. They remain separate from a selector proposal and
from source modification.

## CI evidence

Imported CI `flow-run.json` reports and `.mauitrace` evidence are diagnostic
only. Even independently attested provenance cannot authorize execution,
repair, source changes, platform qualification, or a physical-device claim. A
new local run must match current flow, app, target, failure, and checkpoint
facts before imported failure evidence can enter the human repair path.
