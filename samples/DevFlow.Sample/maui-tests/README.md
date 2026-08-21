# DevFlow.Sample Tier-1 flows

These shared Android and official Windows flows intentionally use only stable, unique
`AutomationId` values already owned by the sample. Their `platform` tag is a comma-separated
allow-list; a Windows host rejects a tag that does not explicitly include `windows` rather than
falling back to a weaker selector. Each fixture hard-resets, seeds, launches, verifies its
test-only state fingerprint, and then invokes the canonical `MauiFlowRunner`.

`agent-authored-contract.md` is intentionally a reviewed committed artifact, not an autonomous
agent action. Apple and Windows QA replay it through the same host-side runner as the human flows.

The initial corpus excludes permission prompts, native dialogs, dynamic per-item delete buttons,
and picker selection because those need additional authoritative selector/identity coverage. They
are covered by the Windows capability contract, which reports `capability-missing` rather than
silently substituting a weaker selector when a native or WebView surface is unavailable.

`drifted-add-todo.md` is the one deliberate exception to the "stable AutomationId" rule above. It
is a copy of `verified-add-todo.md` whose add button is addressed as `AddButtonRenamed`, an
AutomationId the sample does not expose, so the run is expected to end `locator-not-found`. It
exists as a fixture for selector-drift triage and the self-repair pipeline. Do not add it to a QA
lane that expects passing flows, and note that because the tap never lands its independent business
oracle also fails: the run is correctly not repair-eligible, since repair eligibility requires the
business outcome to have verified independently.
