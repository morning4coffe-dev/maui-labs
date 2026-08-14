# Restricted DevFlow test-agent protocol

> **Experimental preview.** `test-agent` is a deliberately restricted MCP profile for drafting
> and running reviewable DevFlow tests. It is not a general-purpose app automation or source-editing
> interface.

For the conversational skill that guides this protocol without granting
authority, see [conversational collaborative testing](conversational-testing.md).

Start it with:

```text
maui devflow mcp --profile test-agent
```

The default `maui devflow mcp` profile remains `full` and is backward compatible.

## Tool inventory

The restricted profile exposes only:

- `maui_test_agents`, `maui_test_status`, and `maui_test_capabilities` for explicit target discovery;
- `maui_test_author`, `maui_test_action`, `maui_test_assertion`, and `maui_test_validate`;
- `maui_test_run`, `maui_test_trace`, `maui_test_failure`, and `maui_test_improvements`;
- `maui_test_patch` for **inert** proposal/preview/reject storage.

It does not expose SecureStorage or preference mutation, files, raw network detail or bodies, CDP
evaluation/source, generic invoke/extension actions, arbitrary property mutation, shell/process
access, evidence capture, source proposals, source apply, repair apply, or lease takeover.

## Explicit targets and envelopes

Every effectful request names both `agentId` and `agentInstanceId`; there is no most-recent,
port-only, or default-target fallback. Requests use `MauiTestAgentRequestEnvelope` v1:

```json
{
  "schema": 1,
  "requestId": "req_...",
  "idempotencyKey": "uuid-or-opaque-key",
  "target": { "agentId": "stable-id", "agentInstanceId": "process-instance" },
  "correlation": {
    "authoringSessionId": "author_...",
    "planId": "plan_...",
    "planRevision": 3,
    "planDigest": "sha256...",
    "flowId": "flow_...",
    "flowRevision": 4,
    "flowDigest": "sha256...",
    "runId": null
  },
  "provenance": {
    "actorKind": "agent",
    "actorId": "host-session-agent",
    "channel": "mcp",
    "provider": "host-owned"
  },
  "intent": "Verify the saved profile",
  "approvalGrantId": "grant_opaque_when_mutating",
  "deadlineMs": 30000,
  "policyVersion": "test-agent-policy-v1"
}
```

`intent` is a short user-visible audit description, not hidden reasoning. The broker returns
typed code/category/retryability errors and safe artifact references. It does not store prompts,
raw app text, screenshots, source, logs, or secret values.

## Human approval and grants

An agent may begin an authoring session, submit a plan or exploration request, and inspect
read-only structure using the session's read capability. It cannot issue a mutation grant.

A submitted `approval-request` is persisted by the broker and appears in **Tests > Agent requests**
for the exact running app. The inbox shows the user-visible intent, target, actions, selectors,
routes, side-effect classes, action/value limits, and expiration. The default pending-review window
is ten minutes; a future issued grant would have its own shorter expiration.

> **Current approval availability:** trusted native approval is available in the VS Code Inspector
> and GitHub Copilot Canvas only when the broker reports approval available and the embedding host
> advertises `nativeApproval`. The standalone browser and chat remain read-only and
> non-authoritative; they cannot approve, narrow, reject, or issue a usable grant.

A trusted native host lets a human reduce the requested scope and issues an opaque, short-lived
grant server-side only after revalidating the live app instance and current plan/flow revision. The
browser never receives the host-approval token, confirmation capability, or opaque grant. The agent
retrieves the grant through `maui_test_author status` using its read capability. Typing `approved`
in agent chat has no authorization meaning and never creates a grant.

For a new agent-authored test, send the complete inert plan, steps, and assertions in
`maui_test_author begin`. The flow has two human decisions: approve the reviewed commit, then
separately approve one run. When native approval is unavailable, authoring stops at the inert draft
or pending request. Draft-change and assertion requests remain inert records for an existing human
demonstration or recorded draft.

Request grants one stage at a time. Draft changes alter `flowDigest`, and commit advances the plan
and flow revisions, so future-stage grants requested against an earlier snapshot become stale by
design. Every request envelope also needs a positive bounded `deadlineMs`.
Selector scope entries use canonical `automationId:<id>`,
`scopedItem:<collection>:<itemKey>:<automationId>`, or `typeIndex:<type>:<index>` keys. The
protocol also accepts equivalent typed selector objects and normalizes them to those keys.

For a plan with `sideEffectPolicy: non-replayable`, an approved `run` grant is the explicit one-shot
human authorization required for that single run. It does not permit a retry, repeated replay,
repair validation, or downstream continuation. Use `test-tenant-resettable` only when the host
actually provides and verifies the declared reset/seed contract.

The broker
binds it to:

- actor, provider, channel, exact agent process, app build, and seed/backend state only when a
  trusted live reset host attests those optional fingerprints;
- plan and flow IDs, revisions, and digests;
- allowed typed actions, durable selectors, routes, side-effect classes, action count, and value
  length limit;
- expiry, nonce, and policy version.

The broker consumes a single-use grant atomically before dispatch, or decrements a bounded
exploration grant. Changed instance/build/plan/flow, a changed attested seed/backend fingerprint,
a scope mismatch, an expired grant, or
a reused idempotency key fails before app dispatch. A lost response is reported as
`unknown-completion`; it is never retried automatically.

## Workflow

1. Use `maui_test_agents`, then copy an exact target into `maui_test_capabilities`.
2. Use `maui_test_author begin` with the complete proposed plan and flow. This is inert broker-owned
   authoring and does not mutate the app or source.
3. Use `maui_test_improvements` for bounded value-free selector discovery. It returns types,
   AutomationIds/native identities, uniqueness, and fragility, but never UI text, values, source,
   runtime IDs, bounds, or screenshots. Read-only discovery needs no mutation grant.
4. Present the complete draft, then submit one `commit` approval request. Use the returned
   `reviewUrl`; it opens the Inspector Workbench directly on Agent requests.
5. The human opens **Tests > Agent requests** and reviews the exact scope. In a trusted VS Code or
   Canvas host they explicitly confirm it natively; browser and chat cannot substitute for that step.
6. Use `maui_test_author await-approval` only after the native confirmation request is presented.
   If native approval is unavailable, keep the draft inert and do not poll or attempt a grant-dependent flow.
7. Only after a bound native-issued commit grant may the agent commit, refresh status, and request a
   separate run approval.
8. Only after that distinct native-issued run grant may it start exactly one run and use its
   run capability for status, trace, failure classification, or cancellation.

After each driven step, hard assertions use bounded settlement polling (up to roughly 2.25 seconds
with the default runner settings) so asynchronous MAUI layout, collection, and binding updates are
observed without adding arbitrary fixed sleeps.

When extending an existing draft instead of authoring it completely at `begin`, request only the
specific draft-change or assertion scope needed for that incremental edit. Those authoring scopes
are normalized to `authoring`, so plan vocabulary such as `non-replayable` cannot cause a corrective
reapproval loop.

`maui_test_patch` accepts proposal, preview, and reject only. `apply` and `approve` are rejected.
The current profile reports run pause/continue as unsupported rather than pretending to provide
them.

## Repair boundary

`maui_test_patch` is intentionally an **inert** review channel. It may retain a structured
selector-repair proposal, return its preview, or mark it rejected inside the bounded authoring
session. It cannot classify a live repair as eligible, mint a repair validation or approval grant,
apply a flow patch, write Markdown/history/source, run verification, or rollback. Requests named
`approve`, `apply`, and `rollback` fail closed with `patch-apply-forbidden`.

Only a human Workbench/host may move a separately broker-owned repair proposal through transient
validation, approval, apply, verification, and rollback. Agent-originated proposals remain visibly
agent-originated and have no Apply control until that human ceremony has occurred. This boundary
also means no model can weaken assertions, change expected values/actions/order, or obtain app
source-write authority through the test-agent profile.

## XAML and C# source-proposal boundary

The restricted profile cannot analyze, create, preview, approve, apply, acknowledge, report,
verify, or roll back an XAML or C# source proposal. In particular, `maui_test_patch` cannot carry
a source operation or turn a source/flow proposal into another proposal type. A human Workbench
and explicit local host own the separate XAML `AutomationId` lifecycle. C# is stricter: a
Roslyn-proven proposal is applied or reverted only by a native IDE host that acknowledges exact
pre/post hashes and patch digest; the broker never writes C# source. A source approval never
grants a selector repair, and a flow-repair approval never grants source writing.

## Audit and privacy

The broker retains a bounded append-only journal of intent digest, policy decision, grant digest,
action/result digest, target, revision, and run IDs. Retention and capacity are bounded. Raw
secrets, prompts, source, screenshots, UI text, logs, network content, and imported artifact
content are excluded. Imported data and UI/log/network observations are untrusted diagnostic input
and never become policy instructions or execution authority.

See [the protocol schema](spec/schemas/maui-test-agent-protocol-v1.json),
[human-authored testing](testing.md), and [the Testing package README](../../src/DevFlow/Microsoft.Maui.DevFlow.Testing/README.md).
