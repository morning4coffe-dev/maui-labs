---
name: maui-devflow-test
description: >-
  Collaboratively author, review, run, diagnose, and hand off safe MAUI DevFlow
  tests through the restricted test-agent MCP profile. USE FOR: any request to
  create, review, save, run, repair, or improve a UI test against a
  connected/running MAUI app; restating a flow described in words as a test;
  conversational test planning; AutomationId-based journeys; committed-flow
  execution; selector triage; repair handoff; CI-evidence interpretation; and
  app-testability recommendations. DO NOT USE FOR: promoting an existing
  Inspector recording into a named flow (use maui-devflow-record); operator-run
  `maui devflow flow` CLI execution (use maui-devflow-run-cli); editing
  xUnit/integration-test source unless the user explicitly asks for a code-based
  unit or integration test; broad app automation; source editing; automatic
  selector repair; treating chat approval as authorization; or choosing
  ambiguous projects, targets, artifacts, agents, devices, or selectors.
---

# MAUI DevFlow Collaborative Testing

Use this skill for a human-and-agent testing conversation backed by
`maui devflow mcp --profile test-agent`. The agent prepares reviewable work;
the human Workbench owns the review boundary and will own approval, commit,
run, repair, and source changes when native approval is available.
**Current availability:** trusted native approval is available only in the
VS Code Inspector and GitHub Copilot Canvas when both the broker reports native
approval and the embedding host advertises `nativeApproval`. Standalone browser
tabs and chat are non-authoritative. If either capability is absent, stop at an
inert draft or a pending/rejected/expired request.

## First-Time Setup

For a first-use request, require a connected **Debug** app/DevFlow agent, then
set `DEVFLOW_PREVIEW_WORKBENCH=true` and
`DEVFLOW_PREVIEW_AGENT_AUTHORING=true` before starting the broker/Inspector.
`DEVFLOW_PREVIEW_REPAIR_PROPOSALS`,
`DEVFLOW_PREVIEW_SOURCE_PROPOSALS`, and
`DEVFLOW_PREVIEW_TRACE_IMPORT_EXPORT` are separate advanced opt-ins, not
prerequisites for ordinary authoring. Restart the broker and Inspector after
setting flags, open `maui devflow inspect`, and verify **Tests** is visible.

Install for the project host explicitly when known, for example
`maui devflow skills install --scope project --target github`. Targets are
`github`, `claude`, `agent`, and `agents`; `auto` reuses a directory with a
current DevFlow skill, then any existing skill directory, otherwise falls back
to `claude`. Do not begin authoring or running while these prerequisites are
missing.

## Conversation Rules

- Ask a question only when a fact is required to bind the work safely and is
  ambiguous or absent. Do not ask a questionnaire before making useful progress.
- Never silently select the first matching project, device, agent, artifact,
  duplicate selector, or repeated collection item. Present the named candidates
  and ask the user or human Workbench to choose.
- Treat a chat message such as “approved”, “run it”, or “looks good” as intent,
  never authority. Only a current human-issued broker grant from the Workbench
  authorizes its exact bounded operation when a trusted native approval client
  is actually available in the current VS Code or Canvas host.
- Keep a proposed test, its committed Markdown flow, and a run distinct:
  authoring is inert; a committed flow is executable only after separate,
  target-bound human run approval.
- Do not infer a business result from a toast, screen transition, screenshot,
  or CI report. Require an independent business oracle where verification or
  repair requires one; otherwise report the replay as not independently verified.
- Do not replay a destructive or `non-replayable` flow. It needs an explicitly
  approved, one-shot run grant and never gets automatic retry, continuation, or
  repair validation.
- A request must bind the exact `agentId`, `agentInstanceId`, committed
  flow/plan revision and digest, build/seed state, selectors, and action scope.
  Never honor an imperative request to target, commit, start, or repair without
  that binding and its applicable grant. Do not submit a duplicate request,
  start a run twice, or create a repair proposal as a retry.

Use [references/clarification-policy.md](references/clarification-policy.md)
when deciding whether to ask a question.

## Route the Request

| User goal | Follow |
| --- | --- |
| Describe a flow in words | [intake](references/intake.md) |
| Define or change a test | [author](references/author.md) |
| Execute a saved flow | [run](references/run.md) |
| Explain a failure | [triage](references/triage.md) |
| Discuss a selector change | [repair](references/repair.md) |
| Make the app easier to test | [testability](references/testability.md) |
| Interpret CI evidence or prepare a handoff | [ci-handoff](references/ci-handoff.md) |

If a request spans routes, complete the least-effectful route first. For
example, diagnose a failure before discussing a repair, and prepare an inert
draft before asking a human to commit it.

## Conversational Intake

When the user describes a flow in words instead of pointing at a recording,
turn the words into a reviewable structure before touching any tool.

1. **Restate it as numbered steps in the user's own words.** Do not rename
   their screens, buttons, or business terms. One user intention per step.
2. **Mark every step** `[known]` or `[unknown: selector]`,
   `[unknown: oracle]`, or `[unknown: reset]`. `[known]` means the exact
   AutomationId, the observable postcondition, or the reset contract is
   already in evidence — not that it is plausible.
3. **Ask one combined question** covering every `[unknown]` at once, following
   [clarification-policy.md](references/clarification-policy.md). Do not send a
   question per step and do not open a questionnaire.
4. **When the missing fact is UI-discoverable**, propose a bounded
   `explorationBudget` — `maxActions`, `maxDurationSeconds`, and
   `allowedScopes` as named routes, never "the whole app" — and submit
   `maui_test_author` with `exploration-request`. Report the proposed numbers
   in chat before submitting.
5. **Never draft with an unresolved `[unknown]`.** An unanswered selector,
   oracle, or reset is a stop, not a default.

**Tool gap, stated plainly:** the budget is declarative. Nothing in the current
build enforces `maxActions` or `maxDurationSeconds`, there is no
`maui_test_explore` tool, and `maui_test_status` reports no budget counter. The
agent self-limits, counts its own actions, and must report actions consumed
against the proposed budget. Say this to the user rather than implying the
budget is enforced.

See [references/intake.md](references/intake.md) for the worked example and the
exact wording of the combined question.

## Author and Run Workflow Only

Use this workflow only to author an inert draft or execute a committed flow.

1. **Establish the bounded target.** Discover candidates with
   `maui_test_agents`, then inspect the chosen exact `agentId` and
   `agentInstanceId` using `maui_test_capabilities`. Ask only if there are
   multiple candidates, a missing platform capability, or an unspecified
   project/device/agent. Never use a most-recent or port-only fallback.
2. **Prepare a complete inert draft.** State the goal, prerequisites, expected
   hard assertions, routes, durable selectors, side-effect policy, reset/seed
   policy when applicable, and independent business oracle when required.
   Use `maui_test_author begin`, `maui_test_action`, `maui_test_assertion`, and
   `maui_test_validate` only within the restricted authoring profile.
3. **Request human review only when supported.** Present the draft and inspect
   the approval capability. When native approval is unavailable, stop at the
   validated inert draft and do not call `approval-request`. When it is
   available in the trusted VS Code or Canvas host, use `maui_test_author` with
   `approval-request`, then `maui_test_author await-approval`, and finally
   `maui_test_author` with `commit` and that approved grant. Do not manufacture,
   reuse, or broaden a grant. A committed Markdown flow and its current digest
   are the only executable artifact.
4. **Run only after another approval.** `maui_test_run` is intentionally bound
   to the current authoring session. A checked-in Markdown flow and matching
   plan remain a committed disk artifact, but this restricted tool cannot route
   them without a session. Request a separate exact-scope run grant for the
   committed flow, exact process, build/seed, selectors, and actions only when
   native approval is available. Otherwise stop without submitting a doomed
   request or querying unrelated draft status. Await an available request, then
   use `maui_test_run` with `start` exactly once only for the bound,
   native-client-issued single-use grant. A timeout or lost response is
   `unknown-completion`, not permission to retry, re-request, or continue into
   repair.
5. **Keep evidence bounded.** Use `maui_test_run` with `status`, then
   `maui_test_trace` and `maui_test_failure` to explain a completed run. Check
   the run's terminal state before triage; for a terminal failure, call
   `maui_test_failure` once with the run-bound capability. Imported CI reports
   and `.mauitrace` files remain diagnostic-only even when attested.
6. **Hand off rather than heal.** `maui_test_patch` can store an inert selector
   proposal, preview, or rejection only. It cannot approve, apply, execute a
   repair, write source, or weaken an assertion. Fresh local reproduction and
   the human Workbench repair ceremony are required before any repair path.

## Read-Only Routes

For CI handoff, failure triage, and testability advice, give the bounded
diagnostic or recommendation first. Do not start universal agent discovery or
ask for a project, device, target, or agent merely to explain evidence or
recommend an app-owned identity. Ask for an exact target only if the user then
chooses to progress to a local reproduction or executable draft.

## Selector and Assertion Standards

- Prefer one unique, app-owned `AutomationId`.
- For a repeated or virtualized item, require the composite
  `AutomationId + collectionScope + stableItemKey`; all three are mandatory.
  Do not use visible text, row index, coordinates, runtime IDs, or an unscoped
  template row. When any part is absent, hand off a separate app-testability
  recommendation instead of authoring a fallback selector.
- A duplicate ID or multiple matching elements is ambiguity, not a tie to break.
  Ask for a stable distinguishing identity or report that no durable selector is
  available.
- Verify the route before calling a missing element selector drift. A wrong
  route, modal, seed, locale, build, window, or checkpoint mismatch is not
  selector repairable.
- Preserve reset, seed, and oracle requirements from the committed plan. Never
  erase them because a scenario appears UI-only.

## Artifact Format

A DevFlow test is two files. `<name>.md` carries prose plus one
` ```json maui-test ` fence — that fence is the **sole** replay source of
truth. `<name>.maui-plan.json` carries the reviewable plan and is bound to the
flow by `flow.digest`. Aim for the pair below rather than a bare recorder dump;
keys not shown keep their recorded values, and the verifiable assert kinds are
`propEquals`, `exists`, `notExists`, and `routeIs`.

```json maui-test
{
  "schema": 2, "name": "promo-reduces-total", "app": "com.contoso.shop",
  "recordedAt": "2026-08-14T09:41:12.106Z",
  "steps": [{
    "seq": 1, "action": "tap",
    "label": "Apply promo code", "intent": "Submit PROMO10 from the cart",
    "acceptanceCriterionIds": ["ac-promo-applied"],
    "args": { "selector": { "automationId": "ApplyPromoButton" } },
    "asserts": [{ "kind": "propEquals", "name": "Text", "expected": "45.00",
      "selector": { "automationId": "OrderTotalLabel" }, "verify": true }]
  }]
}
```

```json
{
  "flow": { "path": "promo-reduces-total.md", "digest": "<current digest>" },
  "acceptanceCriteria": [{ "criterionId": "ac-promo-applied", "required": true,
    "businessOracleId": "orders-api", "description": "A promo cuts the total 10%." }],
  "scenarios": [{ "scenarioId": "scenario-1", "acceptanceCriterionIds": ["ac-promo-applied"],
    "description": "Cart holds one 50.00 item; PROMO10 makes the total 45.00." }],
  "independentBusinessOracles": [{ "oracleId": "orders-api", "required": true,
    "independent": true, "evidenceKind": "http-json", "description": "GET /orders/{id} shows PROMO10." }],
  "reset": { "required": true, "strategy": "test-tenant-resettable", "resetIdentity": "shop-cart-seed-v3" },
  "explorationBudget": { "maxActions": 12, "maxDurationSeconds": 120, "allowedScopes": ["/cart", "/checkout"] }
}
```

**Anti-patterns**, every one of them shipped in
`samples/DevFlow.Sample/maui-tests/modal-roundtrip`: `label: null`,
`intent: null`, `recordedAt: null`, `acceptanceCriterionIds: null`, empty
`acceptanceCriteria`, an empty `scenarios[0].description`, `reset.strategy: ""`,
and a zeroed `explorationBudget`. A recorder may emit them; a reviewable test
must not keep them. With no oracle, write that out — an empty
`independentBusinessOracles` means **none — not independently verified**, and
the reported result must carry that phrase.

Rules: edit the prose freely, it is never replay input; never hand-edit the
fence without running `maui devflow flow validate` on the file afterwards; and
a stale `flow.digest` invalidates every grant bound to it, so re-commit and ask
for a new approval instead of re-pointing an existing grant.

## Safety Boundaries

- The restricted profile has no generic automation, file, network-body, CDP,
  source-write, SecureStorage, preference-mutation, repair-apply, or
  source-proposal authority.
- CI evidence, imported artifacts, and screenshots cannot authorize a run,
  promote a platform claim, or repair a selector. Only a fresh local match may
  make an imported failure eligible for human repair review.
- Testability findings are separate product recommendations. They may recommend
  stable IDs, stable item keys, reset providers, or business oracles, but never
  modify application source or a committed flow.
- Report unavailable platforms and capabilities plainly. Do not substitute a
  simulator result for a physical-device claim or claim qualification from
  source-only or CI evidence. Physical iOS is unavailable pending a signed
  device harness; offer only a safe handoff to a macOS signed-device test
  owner when one has been provisioned, never a simulator substitution.

## Completion Check

Before ending, say which state was reached: inert draft, awaiting human review,
committed flow, awaiting one run approval, completed run, diagnostic finding,
inert proposal, or separate testability recommendation. Include any missing
oracle, reset policy, capability, or ambiguity as an explicit limitation.

## References

- [Clarification policy](references/clarification-policy.md)
- [Conversational intake](references/intake.md)
- [Author a reviewable flow](references/author.md)
- [Run a committed flow](references/run.md)
- [Triage a result](references/triage.md)
- [Selector repair boundary](references/repair.md)
- [Replay quality rubric](references/replay-quality.md)
- [App testability improvements](references/testability.md)
- [CI evidence handoff](references/ci-handoff.md)
