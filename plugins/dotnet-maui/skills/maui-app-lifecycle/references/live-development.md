# Visible development with Inspector and hot reload

Keep the shared Inspector visible for the human while the agent edits source.
Use the broker URL when registered. If registration fails but the explicitly
selected endpoint works, use the CLI's verified-endpoint mode:

```bash
python3 eng/maui-agent/android_launch.py --device emulator-5556 \
  --app PocketTasks --package com.companyname.pockettasks --host-port 9225
maui devflow inspect --app PocketTasks --project PocketTasks/PocketTasks.csproj \
  --agent-host 127.0.0.1 --agent-port 9225 --listen-port 19225
```

Read app identity before interacting. In the browser, select a control in the
tree, change a literal property and confirm the running screenshot/property.
**Apply to XAML** persists supported existing literal attributes. It requires
current writable source metadata; an older or stale source hash must be repaired
through an actual compatible agent rebuild. Do not remove hash checks.

## Separate the proof for each operation

| Operation | Required observation |
| --- | --- |
| Inspector property edit | The actual runtime property and rendered app change. Source may still be unchanged. |
| Apply to XAML | The intended source attribute changes, with no unrelated source edits. |
| XAML Hot Reload | A saved source edit updates the runtime; process ID stays unchanged. Check retained draft/data/navigation relevant to the edit. |
| Rebuild/deploy | A new binary is built and installed; do not describe a restart as hot reload. |
| Delivery QA | A stable final-source build passes host tests, Debug checks and independent Release scenarios. |

Microsoft documents [XAML Hot Reload](https://learn.microsoft.com/dotnet/maui/xaml/hot-reload)
as a debugger-backed feature in Visual Studio and VS Code. `maui devflow inspect`
does not attach a debugger or provide a C# Edit-and-Continue transport. Keep the
IDE's Debug launch alive. Do not add an unrestricted remote code execution
endpoint to mimic debugger support.

Prepare reviewable local IDE configuration with the bundled standard-library tool:

```bash
python3 eng/maui-agent/live_setup.py --project PocketTasks/PocketTasks.csproj \
  --framework net10.0-android --device emulator-5556 --jdk /actual/installed/jdk
```

It writes `.maui-agent/live-config/` using resolved local paths. `--install` can
initialize `.vscode/` only when none of the destination configuration files
exists; existing files must be merged deliberately. Keep generated machine paths
ignored. This prepares the IDE; it does not claim a debugger or hot reload is running.

## Setup findings from the Android qualification

- Verify that the MAUI extension is enabled for the selected workspace and
  supports the installed MAUI SDK. Keep the app's existing SDK pin/workloads.
- Use a focused solution containing the app, portable core and host tests.
  Avoid silently selecting another app or a test project from a broad workspace.
- The tested MAUI VS Code adapter resolves its project lookup before expanding
  `${workspaceFolder}`. A generated local launch configuration therefore needs
  the resolved absolute project path, the explicit test device and Debug TFM.
  Keep machine-specific generated configuration local; do not copy another
  developer's absolute paths into a portable template.
- If the extension's temporary launch metadata names a referenced library
  instead of the app, remove its IDE-only global properties from that reference
  (`CustomAfterMicrosoftCSharpTargets`, `MauiVSCodeBuildOutputFile`, `XamlTools`).
  Confirm the resulting metadata names the app; this is a diagnosed workaround,
  not a reason to alter all references preemptively.
- Record the active Debug XAML inflator. Source-generated XAML and the classic
  IDE engine need their own compatibility check. A failed live-tree assertion
  stays failed even when the editor says Debugging started.
- The current MAUI extension may recommend a newer JDK than an older command-line
  build used. Select an already installed compatible JDK at workspace scope;
  don't change the user's global toolchain merely for a demo.

Capture baseline status/tree/screenshot and source digest before saving an edit.
Poll a specific expected runtime value with a bounded timeout. Record the new
source digest and unchanged process identity, then exercise an interaction to
check that the edited control still works. Preserve failed attempts and logs.
If the source edit never reaches the live tree, report hot reload as blocked;
the Inspector's property-edit success cannot substitute for it.

Project discovery when no agent is registered is a bounded hint scan. It skips
generated/cache directories and directory links and stops after a depth, count
or time limit. Missing hints do not prove DevFlow integration is absent.

## Source-watching literal refresh

When the IDE engine is unavailable, use the explicitly limited DevFlow watcher:

```bash
python3 /absolute/skill/scripts/xaml_watch.py --root . \
  --source PocketTasks/MainPage.xaml --app PocketTasks \
  --package com.companyname.pockettasks --agent-port 9225 \
  --duration 600 --max-updates 20
```

Start from a freshly built source baseline. Keep Inspector visible and save an
existing literal property in the editor. The watcher supports `Text`, `FontSize`,
`Opacity`, `IsVisible`, `IsEnabled`, and Button `CornerRadius` on uniquely identified
Label/Button controls. It checks compiled source metadata from the full tree,
app/package/PID identity, old property values and the mutation lease before editing.
It verifies the result and unchanged PID, captures each revision, and attempts
rollback on a failed batch. It never forces takeover of another session's lease.

Structural, binding, resource, template, new-attribute and C# changes require a
rebuild or a separately qualified IDE engine. A stale source stamp fails closed;
do not waive it. This is `devflow-literal-refresh`, not Microsoft debugger-backed
XAML Hot Reload or C# Edit and Continue. Its report deliberately cannot satisfy
the delivery UI assertion gate. Finish with a fresh build and full final-source QA.

The Android launcher resolves the selected package's actual activity and uses
`am start -W`, rather than probabilistic `monkey` input. It discovers the agent port
from that process's logs, then verifies the live app/package/PID/platform over HTTP.
A log line alone never proves readiness. It refuses to replace another device's
host tunnel. Pass an explicit isolated device and host port when running parallel
work; do not reset or stop a device owned by another task.
