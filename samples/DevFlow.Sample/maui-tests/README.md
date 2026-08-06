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
