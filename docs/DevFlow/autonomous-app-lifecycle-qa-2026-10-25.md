# Broader MAUI lifecycle qualification

Qualification date: 2026-10-25. Source baseline remains upstream `main`
`391101d3f39c747cf6c23253e669382309083618`. This extends the
[initial counter qualification](autonomous-app-lifecycle-qa.md).

## App and results

PocketTasks is an Android MAUI app with offline JSON storage, input validation,
task details, completion, filters and a native deletion confirmation. Its shared
core uses source-generated JSON, serialized writes and atomic replacement.
Invalid saved data is preserved; failed saves keep the user's draft.

| Check | Observed result |
| --- | --- |
| Lifecycle behavioral suite | 44 tests pass on local macOS. |
| App domain tests | 16 pass, including concurrent writes, corrupt/null/duplicate data, filters and save errors. |
| Debug and Release builds | Both pass using SDK 10.0.302 and Android ARM64. |
| Debug DevFlow replay | 2 hard assertions pass with layout diagnostics enabled. |
| Independent Release replay | Two consecutive clean portrait runs, 12 native assertions each. |
| Appearance variant | 12 native assertions pass in dark landscape at font scale 1.3; screenshots reviewed. |
| Persistence | Force-stop/relaunch changes PIDs and preserves asserted task state without clearing storage. |
| Native dialog | Asserted Android confirmation title, Cancel preserving data and Delete removing it. |
| Binary identity | Installed base APK SHA-256 matches the selected Release artifact; no DevFlow APK entries. |

All six planned checks pass at source fingerprint
`87fc0e954821a92c3e5fbfa6ccddc1765201c157ea3deaf7a8d7d8b7f76dc85a`.
All eight acceptance criteria link to sealed, current-source evidence. Release
APK SHA-256 is `9a9cd24959579faa368a41f05e89abb48e26598d205f957c09637b45842f2697`.
Qualification used an isolated API 35 ARM64 emulator, Appium 3.6.0 and
UiAutomator2 8.7.0. Evidence stays ignored locally under `.maui-agent/`.

## Defects found and improvements

- A dependent check could be recorded under its prerequisite name. Fixed the
  loop variable and added a regression checking recorded identities. The gate
  correctly refused readiness while UI records were missing.
- A short ordinary HTTP timeout could expire during cold native instrumentation
  startup. Startup now has its own bounded timeout; ordinary commands retain
  shorter limits. A transport regression verifies restoration.
- A guessed Android dialog resource ID did not match live native XML. Repaired
  the selector using captured evidence; the original visible-title assertion
  remains unchanged.
- Android-emulator-runner splits multi-line input into independent shell
  invocations. CI now invokes one committed Bash file, preserving loops,
  background server lifetime and cleanup.
- Globally overriding TargetFrameworks leaked into the portable core reference.
  CI now selects the app's framework without overriding referenced projects.
- App tests exposed invalid/null saved records. Storage validation now rejects
  these without overwriting the original file.
- Debug inspection alone missed native system UI during an emulator System UI
  failure. Independent screenshots exposed it; the owned emulator was recovered.

The lifecycle now enforces dependency ordering, repeated passes, artifact hashes,
independent Release/native scenarios and explicit classification of same-source
failures when the plan requests them. Serious layout violations fail Debug QA;
unsupported diagnostics remain visible. `localQaReady` and
`validationComplete` distinguish local results from exact-commit CI completion.

Research drew from official Playwright, Maestro, Flutter, Appium and emulator
runner sources. The [testing strategy](../../plugins/dotnet-maui/skills/maui-app-lifecycle/references/testing-strategy.md)
links primary sources and explains the adopted practices.

## Coverage limits

This qualifies the exercised Android emulator workflow. It does not certify
arbitrary generated apps, physical devices, screen readers, permission-heavy
features, network services, store signing, iOS, macOS or Windows. Android layout
diagnostics still report unsupported interaction-occlusion and accessibility
visibility rules; those are not counted as passing checks.

Hosted GitHub execution remains subject to an external runner-startup blocker.
Local passing results do not make required cloud CI green. Draft publication and
exact-HEAD CI following preserve that distinction; no merge or production
deployment is automated. Scheduled maintenance requires installation on the
default branch, and autonomous repairs require an authorized agent host.
