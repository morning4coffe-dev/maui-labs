# MAUI lifecycle qualification

Qualification date: 2026-10-24. Tooling baseline: upstream `main` commit
`391101d3f39c747cf6c23253e669382309083618`.

## Verified results

| Check | Result |
| --- | --- |
| Latest-main MAUI CLI / Go server build | Pass, .NET SDK 10.0.402, zero warnings/errors. |
| Lifecycle behavioral acceptance | 34 tests pass on the local macOS host. |
| Skill structure validation | Pass. |
| Workflow/evaluation YAML and maintenance JavaScript syntax | Pass. |
| Generated MAUI Android Debug build | Pass. |
| Generated MAUI Android Release build | Pass. |
| Generated app xUnit tests | 3 passed, 0 failed, 0 skipped. |
| Real Android DevFlow flow replay | 3 hard assertions passed after fixing the app. |
| Rendered evidence | Screenshots and visual trees inspected; controls and results visible. |
| Opt-in layout diagnostics | Zero findings; 33 passes, 2 unsupported checks, no suppressions. |
| Release automation exclusion | No DevFlow entries in the generated Release APK. |
| GitHub publication | Real lifecycle tool pushed a clean reviewed feature branch and created a private draft PR. |
| Exact-HEAD CI following | Real tool detected a failed run for the published commit and captured runner startup annotations. |

The app ran on an isolated Android API 35 ARM64 emulator. Its builds used the
existing SDK 10.0.302/workloads. The CLI came from the pinned source baseline;
the app used DevFlow package `0.1.0-preview.12.26421.1`. The client verified live
app identity at an explicitly forwarded endpoint when broker discovery was
unavailable. The tested portable plan contains executable names rather than
developer-machine absolute paths; local toolchain selection was supplied by PATH.

## Reproduction and repair

The initial app's Counter incremented by two. An unchanged xUnit assertion
expected one and observed two. Independently, real DevFlow replay resolved the
stable AutomationIds, executed the Increment action, and reported expected
`Text=1`, actual `Text=2`; both Reset assertions passed.

The app implementation was corrected to increment by one. Expectations and
selectors were preserved. The complete final-source plan passed, and each
acceptance criterion was linked to intact UI evidence and an agent assessment.
The final review packet was ready before commit/publication. Local private
evidence, TRX, logs and screenshots remain outside Git in `.maui-agent/`.

The run also reproduced a fast-deployment installation failure and a missing
diagnostics opt-in. These were treated separately from the counter regression.
The resulting skill documents embedded APK versus MSBuild deployment, live
endpoint identity checks and capability-aware diagnostics. CI startup failures
without logs are now diagnosed through GitHub check annotations.

## Limits and outstanding cloud qualification

The GitHub-hosted qualification run failed before any build/test step executed.
The tool correctly returned failed, retained the exact source SHA/run URL and
captured the external runner-startup blocker. Cloud execution is not counted as
passing QA. The toolkit fork also rejected explicit workflow dispatch with HTTP
422, reporting Actions disabled despite its permission/workflow API state.
Repository/account owners must resolve these external execution controls before
cross-platform hosted qualification can finish.

The behavioral suite has Linux/macOS/Windows CI jobs, but only local macOS
execution is claimed here. iOS, Mac Catalyst, Windows and the Go companion
runtime were not device-qualified. Go's companion requires its separate .NET 11
toolchain; CLI/server compilation and command discovery do not prove companion
runtime behavior. Android diagnostics explicitly lack non-destructive interaction
occlusion and accessibility-visibility coverage. Zero findings does not imply
those unsupported rules passed.

The tools do not merge, configure paid agent execution or deploy store releases.
Scheduled validation and trusted failure intake become active after installation
on the app's default branch; unattended repairs require a configured coding-agent
host. Human review remains responsible for acceptance and merging.
