---
name: maui-test-authoring
description: >-
  Author and validate reproducible UI regression tests for a running .NET MAUI app with DevFlow, by
  recording a workflow, adding assertions, saving it as a Markdown test, and replaying it to verify.
  USE FOR: "record a test", "write a UI test for this screen", building a replayable `.md` regression
  suite, adding assertions so a recording validates (not just reproduces), turning a manual repro into
  an automated check, replaying/validating saved flows. DO NOT USE FOR: xUnit/NUnit unit tests, build
  or deploy issues, DevFlow first-time setup (use maui-devflow-onboard), or app-state shortcuts (use
  devflow-automation). INVOKES: `maui_flow_*`, `maui_assert`, `maui_tap/fill/navigate`, `maui_tree`,
  `maui_screenshot`, and the DevFlow inspector.
---

# DevFlow Test Authoring — record → assert → save → replay

Turn a live MAUI app into a **reproducible, self-validating regression suite**. Every human or agent
interaction is captured as a durable step; assertions make each recording *validate* app state, not
just replay it; and `maui_flow_replay` re-runs the saved `.md` to catch regressions. Many `.md` files
= an AI-runnable regression suite.

A recording that only replays taps proves nothing changed structurally. **A recording WITH assertions
is a test.** Always add at least one assertion per scenario.

## The loop

```
connect → explore → record → ASSERT → stop (writes .md) → replay (validate) → fix selectors → repeat
```

### 1. Connect and explore

Confirm an app is connected, then learn the screen so you record durable steps:

```
maui_status
maui_tree            # element IDs, types, AutomationIds, bounds
maui_screenshot      # visual context
```

Note which elements have an **AutomationId** — only an AutomationId is a durable selector. If a target
you need to tap or assert has none, that is a testability gap: ask the app developer to add one (or add
it yourself in the app), because a text/type selector is flagged `fragile` and may break on replay.

### 2. Start recording

```
maui_flow_record_start name="add-todo"
```

This returns a `recordingId` and the current route (the start checkpoint). Keep the `recordingId`.

### 3. Drive the app — each action becomes a step

Drive the app with the normal tools **and** mirror each into the recording with `maui_flow_record_step`
using the same durable selector. (When a human drives the DevFlow *inspector* in Record mode, steps are
captured automatically — see "Interactive recording" below.)

```
maui_fill elementId="<id of #NewTodoEntry>" text="Buy milk"
maui_flow_record_step recordingId="<id>" action="fill" automationId="NewTodoEntry" value="Buy milk"

maui_tap elementId="<id of #AddButton>"
maui_flow_record_step recordingId="<id>" action="tap" automationId="AddButton"
```

Selector precedence for every step: **AutomationId > exact text > raw id**. Never rely on a bare type.

### 4. Add assertions — this is what makes it a test

Record an `assert` step (a validation-only step that drives nothing and runs its checks at that point in
the sequence — so you can also assert the *initial* state before any action). Pass assertions as JSON:

```
maui_flow_record_step recordingId="<id>" action="assert" \
  assertsJson='[{"kind":"propEquals","selector":{"automationId":"TodoCount"},"name":"Text","expected":"1","verify":true}]'
```

Assertion kinds:

| kind | needs | checks |
|------|-------|--------|
| `propEquals` | `selector` + `name` + `expected` | a property equals a value (e.g. Text == "1") |
| `exists` | `selector` | the element is present |
| `routeIs` | `expected` | current Shell route |
| `pageChanged` | — | screen changed (report-only) |

Set `"verify":true` for hard assertions (fail the replay on mismatch). Prefer `propEquals` on a visible
value the workflow is supposed to change (a count, a label, a total). Use `maui_assert` for a quick
one-off check outside a recording.

### 5. Stop — writes the Markdown test

```
maui_flow_record_stop recordingId="<id>"
```

This writes a `.md` file into the project's `maui-tests/` folder. The file has a human-readable step
list **and** a `json maui-test` block (the source of truth). Run `maui_flow_validate` if you want to
review selector/assertion health before replaying.

### 6. Replay to validate

```
maui_flow_replay name="add-todo"
```

You get a **per-step pass/fail report** with each assertion's expected vs actual. This is the real
signal that the app still behaves. If a step fails to resolve, the selector was fragile — add an
AutomationId in the app and re-record that step.

### 7. Build a suite

Repeat for each critical flow (login, add item, delete, navigate, error state). `maui_flow_list` shows
saved tests. Replaying all of them is a regression gate you can run after any change:

```
maui_flow_list
maui_flow_replay name="login"
maui_flow_replay name="add-todo"
maui_flow_replay name="delete-todo"
```

## Interactive recording (the DevFlow inspector)

The shared DevFlow inspector (browser, VS Code webview, or Copilot Canvas) records **human** clicks the
same way, using the same engine — ideal when a user wants to demonstrate a scenario:

1. Open the inspector for the running app.
2. Click **● Record**. Drive the app normally — taps, fills, and scrolls are captured as durable steps.
3. To add a check: select an element (Inspect mode, or Alt/Shift-click), then click **⊨ Assert** — it
   records `Text == <current value>` (or `exists`) on that element.
4. Click **● Record** again to stop; the recording is saved / downloaded as the same `.md`.
5. Click **▶ Replay** to run it against the live app, or **⟲ Start route** to navigate back to where
   recording began before trying again.

Human demonstrates → the engine records durable steps → you add assertions → save → replay. The result
is identical to an agent-authored `.md`, so the two workflows are interchangeable.

## Rules of thumb

- **Every scenario needs an assertion.** A recording without one only reproduces; it does not validate.
- **Assert what the workflow changes** (a count, a total, a label, a route), not incidental UI.
- **AutomationId or it's fragile.** Fragile steps are flagged; fix them in the app for stable replay.
- **Revert before re-recording.** Return to the start route (or reset app state via a
  `devflow-automation` action) so each recording starts from a known state.
- **Validate, then replay.** `maui_flow_validate` catches structural problems before a replay wastes
  time polling for an unresolvable target.
- **Keep tests small and named for intent** (`login`, `add-todo`, `empty-cart-error`) so the suite reads
  as a specification.
