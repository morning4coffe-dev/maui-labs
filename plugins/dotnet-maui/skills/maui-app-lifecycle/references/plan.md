# Plan and evidence contract

`maui-agent.json` schema 1 records the app name, acceptance criteria, target
platforms, retry budget, executable checks and GitHub destination. Commands are
argument arrays, executed directly without a shell from the app Git root.
Use the actual project TFM and installed SDK; the following example uses .NET 10.
Replace paths with the real local interpreter/tool paths or repository-relative
script paths. Copy the skill's scripts into `eng/maui-agent/` in the generated app
when a portable checkout and CI are needed; keep the imported modules together.

```json
{
  "schema": 1,
  "name": "TaskApp",
  "platforms": ["android"],
  "acceptance": ["Tasks survive a restart", "Blank tasks show an error"],
  "maxAttempts": 3,
  "checks": [
    {
      "name": "build-android", "kind": "build", "platform": "android",
      "command": ["dotnet", "build", "TaskApp/TaskApp.csproj", "-f", "net10.0-android", "-c", "Debug"],
      "timeout": 900
    },
    {
      "name": "unit", "kind": "unit", "platform": "host", "format": "trx",
      "command": ["dotnet", "test", "TaskApp.Tests/TaskApp.Tests.csproj", "--logger", "trx;LogFileName=unit.trx", "--results-directory", ".maui-agent/test-results"],
      "result": ".maui-agent/test-results/unit.trx", "timeout": 300
    },
    {
      "name": "ui-android", "kind": "ui", "platform": "android", "format": "devflow",
      "command": ["python3", "eng/maui-agent/devflow_check.py", "--app", "TaskApp", "--platform", "android", "--flow", "maui-tests/add-task.md", "--flow", "maui-tests/invalid-task.md", "--output", ".maui-agent/ui/report.json"],
      "result": ".maui-agent/ui/report.json", "timeout": 300
    },
    {
      "name": "release-android", "kind": "release", "platform": "android",
      "command": ["dotnet", "build", "TaskApp/TaskApp.csproj", "-f", "net10.0-android", "-c", "Release"],
      "timeout": 900
    }
  ],
  "github": {"repository": "OWNER/REPO", "base": "main", "requiredWorkflows": ["MAUI app CI"]}
}
```

Launch/deploy before UI checks. The UI command intentionally doesn't assume a
device, reset user data, or guess how an app launches. Use explicit device
selection and a Debug reset action (`--reset-action`) for test data when needed.
Capture restart/persistence tests as separate reproducible launch and flow steps.

An initialized plan has zero checks and is blocked until configured. All configured
checks are required. Every target platform needs build, UI and Release checks,
and the app needs a host or device unit check. Platform names should match the
DevFlow runtime (e.g. `android`, `ios`, `maccatalyst`, `windows`).

Unit evidence must be a fresh TRX with executed tests and every executed test
passing. UI evidence must contain live app identity, matching platform, hard
assertions and captured artifacts. Result paths live under `.maui-agent/`; the
runner deletes an old result before running a command. It copies evidence into
unique attempt directories and verifies SHA-256 digests before reporting success.

The source fingerprint covers tracked and nonignored untracked files, including
the plan, test files, CI configuration and tool scripts. Committing unchanged
files preserves it. An edit invalidates earlier checks even when Git HEAD has not
changed. Build outputs must be correctly ignored. Submodules are explicitly
unsupported by fingerprinting rather than silently excluded.

Logs redact common credential formats and secret environment values. This is
best-effort sanitization, not a privacy guarantee: screenshots and app data can
contain personal information. Evidence stays ignored locally. Upload only the
artifacts appropriate for the repository's visibility, with a bounded retention.

This is a local audit trail, not a security boundary against an agent/user able
to edit source and state. CI/merge protection and trusted comparison must enforce
organizational policy outside untrusted app execution.
