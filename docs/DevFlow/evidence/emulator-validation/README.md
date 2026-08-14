# Emulator validation evidence

First artifacts produced by `maui devflow flow run` against a real Android
emulator rather than a test fake.

**This run did not pass.** It is committed as an honest record of what the
harness actually emits on real hardware, not as proof the flow succeeded.

| | |
|---|---|
| Command | `maui devflow flow run samples/DevFlow.Sample/maui-tests/modal-roundtrip.md --project samples/DevFlow.Sample/DevFlow.Sample.csproj --platform android --device emulator-5554 --output <out> --json` |
| Device | Android emulator, AVD `devflow-tests-api35` (API 35, x86_64) |
| Source revision | `72e7d2b1770d35620b7701408eb43310dbcedcc3` |
| Run id | `run-20260814191908180-cc9b7bd9` |
| Outcome | `infrastructure-error` / `app-build-failed`, process exit code 1 |
| Failed stage | `resolve-artifact` (stage 5 of the lifecycle) |

Stages 1-4 (`validate-request`, `source-identity`, `load-workflow`,
`validate-target`) passed. The run never reached install, launch, agent
binding, or replay, so nothing here demonstrates those stages.

## Why it failed

`resolve-artifact` builds the app through a generated MSBuild host project that
forces `TargetFramework=net10.0-android` down into the app's transitive
`ProjectReference` graph. The `net10.0`-only DevFlow projects
(`Microsoft.Maui.DevFlow.Agent.Core`, `Microsoft.Maui.DevFlow.Logging`) are
restored under that framework and their `project.assets.json` is rewritten in
the shared Arcade `artifacts/obj/<Project>/` directory with only a
`net10.0-android` target. The subsequent build asks for `net10.0` and fails
with `NETSDK1005`.

The same clobbered assets files also break a plain `dotnet build` of the app
afterwards, until `dotnet restore` is run again.

The operator-visible message does not say any of this: the diagnostic is
truncated to a window that contains only NuGet `Restored ...` lines, and no raw
build log is written to the output directory.

## What these files are good for

`execution-manifest.json` and `flow-run.json` show that the reporting contract
holds on a real device: stage-by-stage lifecycle, run/flow/incident
fingerprints, artifact digests, and redaction. Neither file contains an
absolute path, user name, or token.
