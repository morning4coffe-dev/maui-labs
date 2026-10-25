# Broader application qualification

Choose acceptance scenarios from the product brief. A counter smoke test does
not qualify persistence, native dialogs, navigation or an actual Release build.

## Lessons adopted from other tools

| Primary source | Relevant practice | MAUI implementation |
| --- | --- | --- |
| [Playwright best practices](https://playwright.dev/docs/best-practices) | Isolated tests, condition-based assertions and user-visible contracts. | Native actions wait for visible/enabled targets; assertions poll expected state. Failures retain screenshots, trees and a step trace. |
| [Maestro launchApp](https://docs.maestro.dev/reference/commands-available/launchapp) and [clearState](https://docs.maestro.dev/reference/commands-available/clearstate) | Restart and data reset are different operations. | A clean test reset is explicit and emulator-scoped. Persistence tests kill/relaunch the process without clearing data, record changed PIDs and assert restored state. |
| [Flutter native testing boundary](https://docs.flutter.dev/testing/testing-plugins) | Framework integration tests cannot cover all native dialogs/platform UI. | Pair Debug DevFlow inspection with independent Appium/UIAutomator2 testing of the Release APK. |
| [Appium UIAutomator2](https://github.com/appium/appium-uiautomator2-driver) | Black-box Android automation through native UI. | The helper verifies the foreground package and the installed APK hash, then tests native controls/dialogs without a DevFlow runtime. |
| [Android emulator runner source](https://raw.githubusercontent.com/ReactiveCircus/android-emulator-runner/main/src/script-parser.ts) | The action splits multi-line script input into separate shell invocations. | Keep loops/background process lifetimes in one Bash file invoked by the action. |

These are design lessons, not a claim that Playwright or Flutter drives native
MAUI controls. Appium is optional tooling with a separate server/driver install;
the Python adapter itself uses only the standard library.

## Native Android helper

Install Appium and a compatible UiAutomator2 driver using official distribution
channels. The qualification used Appium 3.6.0 and UiAutomator2 8.7.0. Run its
server on loopback without relaxed security, then invoke:

```bash
activity=$(adb -s emulator-5554 shell cmd package resolve-activity --brief com.example.myapp | tail -n 1 | cut -d/ -f2)
python3 eng/maui-agent/android_check.py --device emulator-5554 \
  --app MyApp --package com.example.myapp --activity "$activity" \
  --apk MyApp/bin/Release/net10.0-android/android-arm64/com.example.myapp-Signed.apk \
  --suite maui-tests/native.json --clear-test-data --output .maui-agent/native/report.json
```

The explicit `--clear-test-data` resets only the selected package and is allowed
only on an emulator. Never point this reset at production user data. Binary
installation and package selection must identify the intended test app.
The helper pulls the installed base APK and compares SHA-256 with the selected
artifact. Release checks reject APKs containing DevFlow entries. It records
native XML/screenshots, action/assertion traces, device configuration, process
restarts and app logs. Startup failures can fall back to an ADB screenshot.
Cold instrumentation gets a separate bounded startup timeout; subsequent UI
commands keep shorter timeouts. Every failure remains visible in the report.

Suite schema:

```json
{
  "scenarios": [{
    "name": "Saved task survives restart", "tags": ["persistence"],
    "steps": [
      {"action": "restart"},
      {"action": "assert", "selector": {"using": "id", "value": "TaskTitle"}, "equals": "My saved task"}
    ]
  }]
}
```

Actions: `tap`, `fill` (`text`), `assert` (`equals`, optional `attribute`),
`assertAbsent`, `back`, `restart`, `hideKeyboard`. Selectors explicitly specify
`using` and `value`; prefer native IDs, accessibility IDs and reviewed visible
text contracts. The original expectation remains the oracle when a selector is
repaired using captured native evidence. Persistence tags require an assertion
after a real process restart. Each scenario must contain assertions.

## Quality contract in the plan

For a native Android app with storage/navigation, a useful policy is:

```json
{
  "quality": {
    "independentUi": true,
    "releaseUi": true,
    "requireClassifiedFailures": true,
    "scenarios": ["invalid-input", "persistence", "navigation", "native-dialog"]
  }
}
```

Add a `kind: ui`, `format: appium` check with `configuration: Release`,
`appPackage`, `artifact` matching the APK path, `dependsOn: ["release-android"]`,
and `runs: 2`. Required runs mean consecutive intact passes at current source,
not unlimited retries until one pass appears. Artifact replacement invalidates
earlier evidence. Unexplained failures at the same source remain blockers;
verified infrastructure failures need an explicit evidence-backed classification.

Choose scenarios appropriate to the app: permissions for camera/location apps,
offline/error paths for network apps, persistence for stored data. Do not invent
coverage for features the app does not have. For applicable native UI, also test
large font scale, orientation and appearance changes. The helper supports
`--orientation LANDSCAPE --font-scale 1.3 --night-mode yes`, records the variant
and restores settings. This variant is not a full accessibility certification.

Debug layout diagnostics now fail on unsuppressed serious-or-critical violations
by default. `--fail-layout-severity` selects a stricter threshold. Unsupported
rules stay in `layoutCoverage`; no findings is not proof of unsupported checks.
Investigate failures; do not silently weaken assertions or add suppressions.
`status` and review packets distinguish `localQaReady` from `validationComplete`.
When CI is configured, completion also requires a clean committed worktree and
passing required workflows for exactly HEAD; old green CI cannot cover new edits.
