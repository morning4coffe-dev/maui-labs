# Reviewable UI flows

DevFlow stores authoritative steps in a Markdown `json maui-test` fenced block.
Validate with the current `maui_flow_validate` MCP tool before replay. The
Inspector recorder and the agent can share these files. Successful drive steps
without a verified assertion are insufficient for a passing lifecycle check.

```markdown
# Counter increments

Precondition: fresh counter page at zero. Run in an isolated test app.

\`\`\`json maui-test
{
  "schema": 1,
  "name": "Counter increments",
  "steps": [
    {
      "seq": 1,
      "action": "tap",
      "target": {"automationId": "IncrementButton"},
      "asserts": [
        {
          "kind": "propEquals",
          "selector": {"automationId": "CounterValue"},
          "name": "Text",
          "expected": "1",
          "verify": true
        }
      ]
    }
  ]
}
\`\`\`
```

Remove the backslashes before the fence backticks when writing the actual file.
The expected value comes from the product brief and known initial state, not
from copying whatever the broken app currently displays. Keep setup/reset
explicit; test state must be repeatable. A Debug-only reset action is appropriate
for fixture data but must not bypass the behavior being tested.

Use `exists` for existence or `propEquals` for exact observable outcomes; set
`verify: true`. Other annotations remain report-only. Add stable AutomationIds
before resorting to text, native IDs, type indices, or coordinates. Include error
paths, persistence, back navigation and platform permissions where the app needs
them. Screen video is separate from workflow recording.

When a selector fails, inspect the actual tree. If functionality is unchanged
but the ID moved, preserve the assertion and submit a focused selector repair.
If functionality changed or state is uncertain, fix the app or classify as
inconclusive. Do not turn a hard assertion into a report-only observation.
