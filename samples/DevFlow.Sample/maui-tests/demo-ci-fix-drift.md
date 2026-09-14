# Demo-only scenario: a repaired trailing action selector

**This flow is demo-only and must not be used for production qualification.** It exists to drive the
`demo-ci-fix` lane of `.github/workflows/devflow-integration.yml` end to end, so a
manager-facing walkthrough can show a real red CI run, a real bounded evidence upload, a real
nonqualified demo issue, and the local `maui-devflow-ci-fix` route. This draft repair demonstrates
the final local-fix and review gate; merging it would disable the intentionally failing showcase.
Nothing about this flow qualifies the sample application.

The `demo-` filename prefix is load-bearing. `AndroidFlowPilotTests.LoadTierOneFlowsAsync` never
loads a `demo-`-prefixed flow into the ordinary Android Tier-1 pilot, so this file cannot turn the
required `devflow-flow-gate` red. Only the explicit opt-in environment filter
`DEVFLOW_FLOW_PILOT_DEMO_FLOW=demo-ci-fix-drift.md` selects it, and when it is set that is the
only flow the pilot loads.

The shape is copied from `drifted-assert-after-commit.md`, the repository's worked example of a
**repair-eligible** failing run, because that is the only shape that makes the demo honest:

- The flow taps the real `AddButton`, so the business outcome really happens.
- The flow asserts the real `CountLabel`, so the assertion is intact and unchanged.
- The independent `android-app-storage` oracle reads the app's private todo ledger over adb, so
  the outcome is verified outside the UI the flow drove.
- The trailing **action** now addresses the app-owned `ShowModalButton` AutomationId identified by
  the bounded local repair workflow.

The business outcome, assertion, action order, and independent oracle remain unchanged. The
demo-only filename and explicit opt-in filter remain load-bearing safeguards.

```json maui-test
{
  "schema": 2,
  "name": "demo-ci-fix-drift",
  "app": "com.companyname.mauitodo",
  "platform": "android",
  "preconditions": "The app is freshly installed, so its todo ledger holds only the three seeded records.",
  "steps": [
    {
      "seq": 1,
      "action": "fill",
      "args": {
        "selector": { "automationId": "NewTodoEntry" },
        "text": "Ledger verified item"
      }
    },
    {
      "seq": 2,
      "action": "tap",
      "acceptanceCriterionIds": ["todo-committed"],
      "args": {
        "selector": { "automationId": "AddButton" }
      },
      "asserts": [
        {
          "kind": "propEquals",
          "selector": { "automationId": "CountLabel" },
          "name": "Text",
          "expected": "4 items, 0 completed",
          "verify": true
        }
      ]
    },
    {
      "seq": 3,
      "action": "tap",
      "args": {
        "selector": { "automationId": "ShowModalButton" }
      }
    }
  ]
}
```
