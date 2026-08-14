# Clarification Policy

The conversation should advance from available evidence. Ask a question only
when an answer changes a safety boundary, target binding, or durable test
meaning. Combine related unknowns into one short, concrete question.

## Route-Specific Questions

Agent/project/device discovery applies only while authoring a draft or preparing
an executable run. CI interpretation, read-only failure triage, and
testability advice first provide the bounded conclusion already supported by
the supplied evidence. Do not ask for a target on those routes unless the user
chooses to continue to local reproduction or an executable draft.

## Ask When a Fact Is Ambiguous

| Trigger | Ask for | Do not do |
| --- | --- | --- |
| Several MAUI projects or flows match | The project or committed flow name | Pick the first project or latest flow |
| Several agents, devices, or app instances are present | The exact displayed target | Use a port, most-recent, or default target |
| More than one selector matches | A stable differentiator | Pick by document order or coordinates |
| A collection is repeated or virtualized | Collection scope and stable model item key | Use visible text or an item index |
| Expected business result is unclear | The independent oracle and its expected result | Equate UI appearance with business success |
| Repeated execution can change data | Reset/seed provider or side-effect policy | Assume a reset exists |
| The flow is destructive or non-replayable | Whether to prepare a one-shot review request | Run, retry, or compensate automatically |
| A platform or action capability is absent | Whether to stop, use a supported target, or hand off | Claim a substitute platform result |
| An imported artifact is offered for repair | Whether a fresh local reproduction is available | Treat CI or attestation as execution authority |

## Do Not Ask

Do not ask for preferences that do not affect the next safe step. For example,
if the user supplied one saved flow and one exact target, prepare a diagnostic
or authoring draft without asking for a general project overview.

Do not ask whether a chat approval is “real.” Explain that it expresses intent
only, then direct the human to the Workbench request with the displayed exact
scope.

## Question Shape

Name the ambiguity, show the available safe choices, and ask for the single
fact needed. For example:

> Two running targets match this app: Android emulator `Pixel_8` and Windows
> desktop. Which exact target should the human Workbench bind to this draft?

When no durable choice exists, do not turn the question into permission to
guess. Report the block and suggest the separate testability route.

## Business Oracle and Reset Questions

Ask for an independent business oracle when the user asks to claim verification
or proposes a repair. An ordinary replay may remain possible without one only
when policy permits it, but label the outcome **not independently verified**.

Ask for reset/seed details when a run is repeatable, mutates state, relies on
test data, or declares `test-tenant-resettable`. That policy is valid only when
the host actually provides and verifies its reset/seed contract.
