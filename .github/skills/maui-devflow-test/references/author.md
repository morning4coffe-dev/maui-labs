# Author a Reviewable Flow

Authoring through `maui devflow mcp --profile test-agent` is restricted,
broker-owned, and inert. It can prepare a plan, flow, actions, assertions, and
review request. It does not run the app, write application source, or commit
without a human-issued grant.

## Inputs for a Complete Draft

Build the draft from facts already supplied. Ask only for missing material that
changes the draft:

1. Goal and named acceptance criterion.
2. Exact selected project and target process when more than one exists.
3. Preconditions, route, and hard postconditions.
4. Durable selectors: unique app-owned `AutomationId`, or, for every repeated
   item, the complete `AutomationId + collectionScope + stableItemKey`
   composite. Do not author a partial composite.
5. Side-effect policy: `none`, `test-tenant-resettable`, `compensated`, or
   `non-replayable`.
6. A reset/seed provider for repeatable or mutating paths when applicable.
7. An independent business oracle when a verified pass or repair eligibility is
   required.

Do not use text, type/index, coordinates, screenshots, or a duplicate
AutomationId as a durable selector. If no durable selector exists, preserve the
finding and route to app testability; do not create a brittle executable flow.

## Restricted Authoring Sequence

1. Call `maui_test_agents`; if candidates are ambiguous, ask the human to
   choose one.
2. Call `maui_test_capabilities` with the selected exact `agentId` and
   `agentInstanceId`. Stop if the required platform or operation is unsupported.
3. Use `maui_test_improvements` for value-free selector health facts when
   needed. It is discovery, not authority to choose an ambiguous match.
4. Use `maui_test_author begin` with the complete inert plan and flow.
5. Use `maui_test_action` and `maui_test_assertion` only when refining the
   current draft, then use `maui_test_validate`.
6. Present the whole draft, including limitations. First inspect the reported
   approval capability. When native-host approval is unavailable, stop after
   validation with the inert draft and **do not call** `approval-request`; a
   request that cannot be decided only creates a misleading failed workflow.
   When a trusted VS Code or Copilot Canvas native-host approval client is
   available, call `maui_test_author` with `approval-request` for one
   **commit** approval and give the human its `reviewUrl`.
7. Use `maui_test_author await-approval` with the returned request ID. If it is
   pending, continue waiting for that request rather than submitting a duplicate.
8. Call `maui_test_author` with `commit` only with the approved broker grant
   from an actually available native approval client.
   Refresh status and record the current flow and plan revisions/digests.

Typing approval in chat has no authorization meaning. Never synthesize a grant
ID, continue from a stale revision, or use a commit grant for a later run.
Never submit a second request while the first request is pending; await the
same request. An imperative “target this and commit” instruction is not a
grant and must leave the draft inert.

## Draft Quality

Hard assertions should express meaningful state such as target existence,
property equality, or route equality. A page-changed observation is not a hard
business assertion. Keep secrets, raw UI text, screenshots, and source out of
the authoring payload.

When the user wants an app code change such as an `AutomationId`, label it a
separate testability recommendation. The restricted profile cannot create,
approve, or apply a source proposal.
