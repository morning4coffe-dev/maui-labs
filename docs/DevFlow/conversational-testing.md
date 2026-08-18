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

## Walkthrough: what the loop looks like

A worked end-to-end pass, from an empty terminal to a reviewed repair. Each
command here is the one an operator actually types.

### 1. Bring the pieces up

```powershell
$env:DEVFLOW_PREVIEW_WORKBENCH = 'true'
$env:DEVFLOW_PREVIEW_AGENT_AUTHORING = 'true'

maui devflow broker start
maui devflow skills install --scope project --target github
maui devflow mcp --profile test-agent
```

Both variables must be set **in the process environment that starts the broker,
Inspector, and MCP server**. They default to off; without
`DEVFLOW_PREVIEW_AGENT_AUTHORING` the `test-agent` profile refuses to start and
reports that it is a disabled preview surface.

> **The broker is a long-running daemon and does not re-read these.** If a
> broker is already running, exporting the variables in a shell, or setting them
> in an agent host's MCP configuration, changes nothing for it: the authoring
> route stays closed and every draft is refused with *"agent authoring preview is
> disabled"*. Stop and restart the broker from an environment that has the
> variables set:
>
> ```powershell
> $env:DEVFLOW_PREVIEW_AGENT_AUTHORING = 'true'
> maui devflow broker stop
> maui devflow broker start
> maui devflow approve --list      # ok:true means the gate is open
> ```
>
> An agent host that spawns the MCP server with its own `env` block covers the
> MCP process only. To avoid the mismatch entirely, set both variables at user
> or machine scope so every later process inherits them.

Start a Debug build of the app so its agent registers, confirm it with
`maui devflow list`, and open `maui devflow inspect` — the Inspector is the
trusted host that can approve work. Without it, drafts still get prepared but
every mutation stops at a pending request.

Keep exactly one instance of the app under test connected. Approvals bind to one
exact target, so a second instance makes `maui devflow approve` fail as
`AgentAmbiguous`, and a second instance on another platform can take the agent
port and turn runs into infrastructure failures.

### 2. Describe the journey

> "Write a test that adds a todo and checks the count goes to 4."

The skill does not begin drafting. It restates the journey as numbered steps in
the user's own vocabulary and marks each one `[known]`, `[unknown: selector]`,
`[unknown: oracle]`, or `[unknown: reset]`. `[known]` means *observed* in a tree
dump, a committed flow, or a user statement — a control that is "probably called
`AddButton`" is `[unknown: selector]`. See
[conversational intake](../../plugins/dotnet-maui/skills/maui-devflow-test/references/intake.md).

### 3. Answer one combined question

Every unknown arrives in a single message naming the step number and the
smallest answer that unblocks it — never one question per step, and never a
guessed default to keep things moving.

The oracle question is the one that decides whether the test can ever be
trusted. Answering "the label on screen" yields a run that can pass but reports
`verified: false`. Naming something independent — a record the test can query
outside the channel the agent drove — is what produces `verified: true`.

### 4. Approve, explicitly

When the agent needs to commit, run, or patch, it stops and names a pending
request:

```powershell
maui devflow approve --list --agent-port <port>
maui devflow approve <request-id> --agent-port <port>
maui devflow approve <request-id> --agent-port <port> --reject --reason scope-too-broad
```

Pass `--agent-port` whenever more than one app is connected; approvals bind to
one exact target and the command refuses to guess. The same decision is
available in the Inspector's agent-request surface. **A chat reply approves
nothing**; the skill is required to say so and point at the request rather than
proceed.

#### Decide promptly — there are two clocks

| Clock | Default | What it bounds |
|---|---|---|
| Authoring session | 30 min | How long the whole draft-and-review session stays alive |
| Approval decision window | 10 min | How long *you* have to decide on one request |
| Issued grant | 300 s (`--grant-seconds`, 1-900) | How long the approved action stays usable |

The decision window is **clamped to whatever remains of the authoring session**,
so it is often shorter than 10 minutes:

```csharp
expiresAt = request.ExpiresAt ??
    (defaultExpiresAt <= session.ExpiresAt ? defaultExpiresAt : session.ExpiresAt);
```

An agent that spends 25 minutes reading files, validating, and retrying leaves
you 5 minutes to approve, not 10. A request that is not decided in time moves to
`state: expired` with `reasonCode: decision-window-expired`, and
`maui devflow approve --list` then reports `pendingCount: 0`.

Nothing is lost when that happens — an unapproved draft writes no files — but
the work must be redone. Ask the agent to request approval again, and decide
while the request is fresh. `maui devflow approve --list` prints the `expires`
timestamp for exactly this reason.

### 5. Read the failure, review the repair

A failed run is classified as selector drift, an app regression, infrastructure,
or inconclusive. Only drift is repairable, and the proposal is inert until a
human approves it. An app regression must be reported, not "repaired": the test
was right. Thin evidence stays inconclusive rather than becoming a guess.

A legitimate repair re-points a test at what it always meant to check. Deleting
an assertion, flipping `verify: true` to `false`, relaxing an expected value to
match what happened, widening a selector until something matches, or adding
sleeps are all excluded.

### Operational notes

- **Close other instances of the app under test first.** A second instance takes
  the agent port; on Windows the next port may fall inside a Hyper-V/WSL reserved
  range, and the run fails as infrastructure rather than as a test result.
- **Local reproduction of a CI failure does not currently reach `matched: true`
  on Android.** Behaviour reproduces — step and runtime fingerprints agree — but
  package identity cannot, because Android packaging is not byte-reproducible
  across builds. Repair review therefore remains the end of the automated path.

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
