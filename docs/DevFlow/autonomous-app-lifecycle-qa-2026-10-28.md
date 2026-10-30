# Android lifecycle and live-development qualification, October 28–30

Tooling checks pass locally. **The updated app is not fully validated:** its
current-source device runs are blocked by an unhealthy Android emulator, and
hosted toolkit CI has rejected workflow startup. No merge or production
deployment was performed.

The toolkit merged upstream `main` at
`ab32d8bd40006088b8a2378162fa7a7a98e71ba7` on October 28. This extends the
[October 25 qualification](autonomous-app-lifecycle-qa-2026-10-25.md).

## Tooling results

| Check | Observed local result |
| --- | --- |
| Lifecycle/CLI-MCP behavioral suite | 63 pass on macOS, including actual child processes, fixed-root MCP routing, device readiness, source guards, rollback and screenshot-link protection. |
| Focused .NET CLI regressions | 3 pass: wrong live app, missing project, bounded discovery with a directory-link cycle. |
| Inspector JavaScript | 105 pass, 1 skipped; clean Node 22 install, build and VSIX packaging pass. |
| Canvas | 50 pass plus 4 recorder self-tests; zero skipped/failing in the final run. |
| Whole-repository npm audit | All three projects verified October 30; no high/critical advisories. Moderate findings remain outside the blocking threshold. |
| Azure dependency compatibility | ClientSecretCredential traverses Azure Identity/MSAL 7 with mocked HTTP only. Interactive Azure signing remains unqualified. |
| Actual readiness CLI and MCP | Both report the selected device's blocked state. The real stdio server advertises `maui_lifecycle_android_ready`; its report cannot satisfy delivery UI assertions. |

An isolated, SHA-256-verified official Node 22.23.4 installation completed the
JavaScript checks after Node 26 installation stalled. The first Canvas run had
three module-load failures because its linked client had not been built; building
the client and repeating a clean Canvas installation resolved them. These were
failures, not skips. The user's global Node selection and toolkit NuGet feeds
were preserved. Dependency remediation updates Zod and Azure MSAL.

## Visible Inspector and source refresh

The shared Inspector now has `maui devflow inspect`: a loopback host for an
explicitly verified live app endpoint and local project. This permits inspection
when broker registration is unavailable without claiming discovery is healthy.
Empty-agent project hints now have time, directory-count and depth bounds and
skip links and generated caches. An actual home-directory invocation returned
in 6.42 seconds instead of traversing indefinitely.

The Inspector displayed the actual app, visual tree and property editor. A live
button edit changed the rendered control. The older agent correctly refused
source persistence with incompatible source metadata; the Debug-only dependency
was upgraded to preview 13 and rebuilt with compatible source stamps.

The source watcher changed `Add task` to `Add task • live` and back. Both saved
source revisions reached the live control with screenshots and unchanged PID
**18855**. The original source SHA-256 was restored:
`c5c39dd9ff63d9e26d99249193479eabd59bceebac7d88ab40aea967956d898a`.
Private evidence is `.maui-agent/literal-refresh-tree-20261028/report.json` and
its three PNGs in the qualification workspace. This is the October 28 demo,
not fresh runtime evidence for the later app changes.

This proves **DevFlow literal refresh**, not the Microsoft debugger engine.
The IDE launched the app, but a saved subtitle edit did not reach the live tree;
IDE XAML Hot Reload, C# Edit and Continue and structural/template/resource reload
remain unqualified. The first watcher attempt failed because the query endpoint
omitted source metadata; using the full tree fixed that while retaining the
source-stamp guard. Watcher reports deliberately contain no delivery assertions.

See [live development](../../plugins/dotnet-maui/skills/maui-app-lifecycle/references/live-development.md)
for supported edits, leases, rollback and local IDE preparation. Microsoft's
[debugger-backed XAML Hot Reload](https://learn.microsoft.com/dotnet/maui/xaml/hot-reload)
is a separate mechanism.

## App runs and freshness

PocketTasks exercises offline creation, validation, details, completion, filters,
process persistence and native deletion. New boundary scenarios cover whitespace,
trimmed Czech text and emoji, the 80-character limit and reopening a completed task.

The October 28 baseline at source
`7432dc50555467c64f673b55c4c10f68333423a254c5b2d5ef356c9fb117a0ee`
passed all planned runs: 16 domain tests, Debug/Release builds, 2 Debug assertions
and **56 independent Release assertions** (24 portrait, 12 dark landscape at
130% font scale, 20 boundary assertions). Installed APK identity and actual
process restart were checked. This baseline still contained visual findings.

Visual review found pale navigation text/back icon on white and weak placeholder
contrast. The updated source uses darker navigation text/icons in light mode,
stronger placeholder colors in both themes and an explicit accessible input name.
Current runtime verification of those corrections remains blocked.

| Current app source | Result |
| --- | --- |
| Local checkpoint | `29de606be85ac4ae54a6e89633736b2b6e1ff17c`; not pushed while UI gates are incomplete. |
| Source fingerprint | `0af2a1c6b42bb3fc7c9ce4951957dd93182af30690375963657fbdc7edcdee35` |
| Domain tests | 16 pass; acceptance criterion 6 links to current evidence. |
| Debug build | Pass: `3ac3f541bfc843f2a2e2402febaf7062`. |
| Release build | Pass: `d35a8319d75f47cc9dfceba3e8c575a8`. |
| DevFlow replay | Blocked during APK installation before launch/assertions; failed attempt retained and classified. |
| Native/variant/boundary runs | Earlier results are stale for this source; current runs have not passed. |
| Current review | `localQaReady=false`, `validationComplete=false`. |

The previous UI results are not reused after source changes. The app's existing
draft PR therefore remains at its previously published checkpoint. The new
local checkpoint is saved for resuming on a healthy target.

## Failures retained and repairs

- `adb monkey` exited -5 before assertions. Explicit `am start -W` launched the
  selected package. The launcher now resolves the real activity, discovers the
  selected PID's agent port and verifies live app/package/PID/platform identity.
  Native persistence restarts use the explicit activity too.
- A prematurely started rerun was cancelled after a guarded style edit rejected
  duplicate Entry/Editor matches. The cancellation remains classified; the edit
  was narrowed to Entry before renewed checks.
- A Debug build timed out across an overnight session pause. A later Release
  build reached signing but exceeded 900 seconds under sustained host load above
  100. Both failures remain recorded. A bounded 1800-second local build limit,
  limited workers and temporary process-bound idle-sleep assertions enabled the
  current builds to complete. Retry budgets were not reset.
- The original owned emulator remained in an uninterruptible exit wait. A new
  disposable AVD used separate ports and retained its data across recovery.
  The initial 2-core/2-GiB request was raised by the emulator to 2560 MiB; a later
  attempt restored the baseline's 4-core/4096-MiB profile. Both headless and
  visible launches were attempted.
- The replacement showed Google Play Services, launcher and System UI ANRs,
  Package Manager dump timeouts and stalled installation despite a complete APK
  transfer and an observed integrity check. A boot flag and System UI PID were
  insufficient: a captured frame still said "Pixel is starting".
- Recovery used only owned test instances: targeted reconnect, temporary
  scheduling adjustment, service restart, and an ADB restart after verifying
  that all registered devices/forwards belonged to this task. It did not yield a
  usable target. Power Manager and emulator-console operations also timed out.
  The SDK's unbundled emulator window could not be bound by the computer-control
  API. No unrelated application/device was stopped and no host reboot occurred.

These findings produced the reusable **`android_ready.py`** CLI and
**`maui_lifecycle_android_ready`** MCP tool. They bound every command, select an
explicit device, inspect primary-user unlock, System UI, Package Manager,
foreground window and complete PNG capture. Startup fallback, active ANR,
missing focus and stalled services block readiness. Artifact paths cannot follow
a screenshot link into app source. A failed report does not classify fault
ownership; an app-owned ANR still requires app investigation. No readiness
report can replace hard app assertions. The CI asset runs this preflight before
installation; its new positive end-to-end path remains unqualified on this host.

All failed product-check attempts and diagnostics are retained. Cycle 3 has two
failed DevFlow checks, two failed Debug builds and one failed Release build;
there is still one failed-attempt allowance for DevFlow. It was preserved instead
of being spent against an unhealthy device. Native-check budgets are unused.
Owned Appium/Inspector processes and responsive emulators were cleaned up; the
old kernel-stalled emulator requires host recovery.

## Hosted CI and remaining limits

At toolkit code commit `fa42a45`, manual CLI, DevFlow and skill-check dispatches
all returned HTTP 422, "Actions has been disabled for this repository." The
permissions API separately returned `enabled: true`; that setting did not prove
that a runner could start. No run was created. A manual CLI entry point was added,
matching the existing DevFlow/skill workflows. GitHub documents
[branch-specific manual runs](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/manually-run-a-workflow?tool=cli).
A fresh skill-check dispatch at readiness-tool code commit
`3d9617285e5bc5f13ed9f0b3159b3f1fc9b71bd8` on October 30 returned the same
HTTP 422 response. No runner started. Exact results are retained in the local
handoff. No billing, visibility,
credentials or production settings were changed.

Finishing this app requires a healthy Android target, then the unchanged current
UI gates, visual acceptance review and publication/CI for that exact commit.
Hosted CI is not replaced by local assertions. Physical devices, other MAUI app
heads, screen readers, permission-heavy features, remote APIs, store signing,
the Go companion/graduation path and arbitrary generated apps are unqualified
by this pass. Human review remains the merge/release gate; no unattended
maintenance daemon or human approval is implied by these results.
