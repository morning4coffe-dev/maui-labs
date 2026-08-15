# Conversational Intake

Use this route when the user describes a journey in words — "check that a promo
code lowers the total" — instead of pointing at a recording or a committed
flow. The goal of intake is a structure the user can correct in one reply, not
a draft.

## Step 1 — Restate as numbered steps

Write the journey back as numbered steps, in the user's own vocabulary. Do not
rename their screens, buttons, or business terms, and do not add steps they did
not describe. One user intention per step; if a sentence contains two
intentions, split it and say that you split it.

## Step 2 — Mark every step

Append exactly one marker to each step:

| Marker | Meaning |
| --- | --- |
| `[known]` | The AutomationId, postcondition, or reset contract is already in evidence — a tree dump, a committed flow, or something the user stated. |
| `[unknown: selector]` | No stable element identity for the target. |
| `[unknown: oracle]` | No observable postcondition that proves the step worked. |
| `[unknown: reset]` | No stated way to return the app or backend to the starting state. |

`[known]` means *observed*, not *plausible*. A control that "is probably called
`SubmitButton`" is `[unknown: selector]`.

## Step 3 — Ask one combined question

Collect every marker into a single message, following
[clarification-policy.md](clarification-policy.md). Name the step number, the
missing fact, and the smallest answer that unblocks it.

> Steps 1 and 4 are clear. Three things are still unknown:
>
> - Step 2 `[unknown: selector]` — which control applies the code? If it has an
>   AutomationId, that name is enough.
> - Step 3 `[unknown: oracle]` — what proves the discount applied: the total on
>   screen, or an order record you can query?
> - Step 5 `[unknown: reset]` — how does the cart get back to empty between
>   runs, or should the flow assume a fresh test tenant?
>
> Answer whichever you know; I will propose a bounded look for the rest.

Do not send one question per step. Do not open a questionnaire, and do not
guess a default in order to keep the conversation moving.

## Step 4 — Propose a bounded exploration budget

When a remaining `[unknown]` is UI-discoverable, propose a budget and state the
numbers in chat before submitting anything:

```json
{ "maxActions": 12, "maxDurationSeconds": 120, "allowedScopes": ["/cart", "/checkout"] }
```

`allowedScopes` lists named routes or screens. "The whole app", `"*"`, and an
omitted scope list are all invalid proposals. Size `maxActions` to the number
of unknowns, not to the size of the app.

Submit it with the `exploration-request` operation of `maui_test_author`, and
wait. An exploration request is a request; chat approval is not authorization,
and `awaiting-approval` is not `approved`.

## Step 5 — Draft only when nothing is unknown

Every step must be `[known]` before the author route starts. An unanswered
selector, oracle, or reset is a stop, not a default. If the user declines to
answer, record the gap as an explicit limitation and stop with an inert
partial restatement instead of a draft.

## Tool gap — say this out loud

The exploration budget is **declarative**. In the current build:

- Nothing enforces `maxActions` or `maxDurationSeconds`. They are recorded in
  the plan and reviewed by a human; no runtime component stops the agent when
  the count is exceeded.
- There is **no `maui_test_explore` tool**. Exploration is performed with the
  ordinary read-only tools (`maui_test_capabilities`, tree and query reads)
  under self-imposed limits.
- `maui_test_status` reports **no budget counter**. Nothing external tells the
  user how much of the budget has been consumed.

Therefore the agent counts its own actions and reports "used N of the proposed
M actions" when exploration ends. Do not describe the budget as enforced,
sandboxed, or capped by the tooling — describe it as a declared limit the agent
holds itself to and reports against.

## Worked example

User: *"Make sure a promo code lowers the total."*

1. Open the cart with one item in it. `[unknown: reset]`
2. Enter the promo code. `[unknown: selector]`
3. Apply it. `[unknown: selector]`
4. The order total drops by ten percent. `[unknown: oracle]`

One combined question covers the reset contract, both selectors, and whether
the total on screen is sufficient proof or an order-service check is required.
If the user answers only the selectors, propose a 12-action, 120-second,
`/cart`-scoped exploration for the reset contract, submit
`exploration-request`, and hold. Nothing is drafted until step 4 has an oracle
— or until the user accepts, in writing, that the result will be reported as
**none — not independently verified**.
