# DevFlow Apple XCTest Test Agent

This nonshipping experimental component is a **thin XCTest/XCUITest device agent**. It launches or
activates one approved target through `XCUIApplication`, keeps that target foreground,
and exposes authenticated operation-level commands to the macOS host.

It intentionally does **not** contain `MauiFlowRunner`, flow parsing, selector policy,
actionability, repair, source, plan, or replay logic. The sole semantic executor is
`Microsoft.Maui.DevFlow.Testing.MauiFlowRunner` in
`Microsoft.Maui.DevFlow.TestAgent.Host` on the macOS host.

See [the Apple QA handoff](../../../docs/DevFlow/apple-xctest-spike.md) for the required macOS
commands, clean-repeat artifacts, and evidence that must be reviewed before it is considered
runtime-proven.
