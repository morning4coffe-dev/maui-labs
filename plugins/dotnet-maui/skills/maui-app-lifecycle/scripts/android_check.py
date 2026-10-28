#!/usr/bin/env python3
"""Independent Android Release checks via Appium/UIAutomator2. No in-app agent."""
from __future__ import annotations

import argparse
import base64
import contextlib
import json
from pathlib import Path
import subprocess
import sys
import time
import urllib.error
import urllib.request
import zipfile

from lifecycle import digest, inside, now, redact, snapshot, write_json


class Driver:
    def __init__(self, server, timeout=30):
        self.server, self.timeout, self.session = server.rstrip("/"), timeout, None

    def request(self, method, path, data=None):
        body = json.dumps(data).encode() if data is not None else None
        prefix = f"/session/{self.session}/" if self.session else "/"
        request = urllib.request.Request(self.server + prefix + path, data=body, method=method,
                                         headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(request, timeout=self.timeout) as response:
                value = json.load(response)["value"]
        except urllib.error.HTTPError as exc:
            raise ValueError(redact(exc.read().decode(errors="replace")))
        if isinstance(value, dict) and value.get("error"):
            raise ValueError(str(value))
        return value

    def start(self, udid, package, activity):
        previous = self.timeout
        self.timeout = max(120, previous)  # Cold instrumentation/configuration startup can exceed ordinary command timeouts.
        try:
            value = self.request("POST", "session", {"capabilities": {"alwaysMatch": {
                "platformName": "Android", "appium:automationName": "UiAutomator2",
                "appium:udid": udid, "appium:appPackage": package, "appium:appActivity": activity,
                "appium:noReset": True, "appium:newCommandTimeout": 120}}})
        finally:
            self.timeout = previous
        self.session = value["sessionId"]
        return value["capabilities"]

    def find(self, selector):
        if set(selector) != {"using", "value"} or selector["using"] not in {"id", "accessibility id", "xpath", "-android uiautomator"}:
            raise ValueError("Use an explicit supported native selector")
        element = self.request("POST", "element", selector)
        return element["element-6066-11e4-a52e-4f735466cecf"]

    def close(self):
        if self.session:
            self.request("DELETE", "")


def wait_for(predicate, timeout=15):
    deadline = time.monotonic() + timeout
    last = None
    while True:
        try:
            value = predicate()
            if value:
                return value
        except (ValueError, OSError) as exc:
            last = str(exc)
        if time.monotonic() >= deadline:
            raise ValueError(f"Condition did not become true within {timeout}s. Last evidence: {last}")
        time.sleep(0.2)


def run(args):
    root = Path(args.root).resolve()
    output, apk = inside(root, args.output), inside(root, args.apk)
    if not output.is_relative_to(root / ".maui-agent"):
        raise ValueError("Output must be under .maui-agent/")
    suite = json.loads(inside(root, args.suite).read_text())
    if not suite.get("scenarios") or any(not s.get("steps") for s in suite["scenarios"]):
        raise ValueError("Native suite needs nonempty scenarios")
    output.parent.mkdir(parents=True, exist_ok=True)
    report = {"schema": 1, "ok": False, "at": now(), "driver": "appium-uiautomator2",
              "app": args.app, "package": args.package, "platform": "android", "configuration": args.configuration,
              "source": snapshot(root), "binary": {"path": args.apk, "sha256": digest(apk.read_bytes())},
              "artifacts": [], "trace": [], "scenarios": [], "assertions": 0, "restarts": []}
    driver = Driver(args.server)
    initial_orientation = initial_font = initial_night = None

    def adb(*arguments):
        result = subprocess.run([args.adb, "-s", args.device, *arguments], capture_output=True, timeout=90)
        if result.returncode:
            raise ValueError(redact((result.stdout + result.stderr).decode(errors="replace")))
        return result.stdout.decode(errors="replace").strip()

    def save(name, data, binary=False):
        path = output.parent / name
        if binary:
            path.write_bytes(data)
        else:
            path.write_text(redact(data), encoding="utf-8")
        report["artifacts"].append(str(path.relative_to(root)))

    def capture(label):
        failures = []
        try:
            save(label + ".xml", driver.request("GET", "source"))
        except Exception as exc:
            failures.append(str(exc))
        try:
            image = base64.b64decode(driver.request("GET", "screenshot"), validate=True)
            if not image.startswith(b"\x89PNG\r\n\x1a\n"):
                raise ValueError("Native screenshot is not a PNG")
            save(label + ".png", image, binary=True)
        except Exception as exc:
            failures.append(str(exc))
        if failures:
            report.setdefault("captureErrors", []).extend(failures)

    try:
        wait_for(lambda: adb("shell", "getprop", "sys.boot_completed") == "1", 60)
        if not driver.request("GET", "status").get("ready"):
            raise ValueError("Appium server is not ready")
        report["environment"] = {"device": args.device, "apiLevel": adb("shell", "getprop", "ro.build.version.sdk"),
                                 "model": adb("shell", "getprop", "ro.product.model"),
                                 "orientation": args.orientation, "fontScale": args.font_scale}
        if args.clear_test_data or args.font_scale or args.night_mode:
            if adb("shell", "getprop", "ro.kernel.qemu") != "1":
                raise ValueError("Data reset and system-setting variants require an isolated emulator")
        if args.configuration == "Release":
            with zipfile.ZipFile(apk) as archive:
                if any("devflow" in name.lower() for name in archive.namelist()):
                    raise ValueError("Release APK unexpectedly contains DevFlow automation")
        adb("install", "--no-incremental", "-r", str(apk))
        package_path = adb("shell", "pm", "path", args.package)
        paths = [line.removeprefix("package:") for line in package_path.splitlines() if line.startswith("package:")]
        if len(paths) != 1:
            raise ValueError("Expected one installed base APK for the selected app")
        installed = output.parent / "installed.apk"
        try:
            adb("pull", paths[0], str(installed))
            if digest(installed.read_bytes()) != report["binary"]["sha256"]:
                raise ValueError("Installed package does not match the selected APK")
        finally:
            installed.unlink(missing_ok=True)
        if args.clear_test_data:
            if adb("shell", "pm", "clear", args.package) != "Success":
                raise ValueError("Test-data reset failed")
        if args.font_scale:
            initial_font = adb("shell", "settings", "get", "system", "font_scale")
            adb("shell", "settings", "put", "system", "font_scale", str(args.font_scale))
        if args.night_mode:
            initial_night = adb("shell", "cmd", "uimode", "night").split(":")[-1].strip()
            if initial_night not in {"yes", "no", "auto", "custom"}:
                raise ValueError("Cannot safely restore the original night mode")
            adb("shell", "cmd", "uimode", "night", args.night_mode)
            report["environment"]["nightMode"] = args.night_mode
        caps = driver.start(args.device, args.package, args.activity)
        save("session.json", json.dumps(caps, indent=2))
        if driver.request("GET", "appium/device/current_package") != args.package:
            raise ValueError("Foreground app is not the selected package")
        initial_orientation = driver.request("GET", "orientation")
        if args.orientation:
            driver.request("POST", "orientation", {"orientation": args.orientation})
        capture("initial")
        for scenario_index, scenario in enumerate(suite["scenarios"]):
            count = 0
            for step_index, step in enumerate(scenario["steps"]):
                action = step["action"]
                entry = {"scenario": scenario["name"], "action": action, "ok": False, "at": now()}
                report["trace"].append(entry)
                label = f"{scenario_index:02}-{step_index:02}-{action}"
                if action in {"tap", "fill"}:
                    def actionable():
                        element = driver.find(step["selector"])
                        if (driver.request("GET", f"element/{element}/displayed")
                                and driver.request("GET", f"element/{element}/enabled")):
                            return element
                    element = wait_for(actionable, step.get("timeout", 15))
                    if action == "tap":
                        driver.request("POST", f"element/{element}/click", {})
                    else:
                        driver.request("POST", f"element/{element}/clear", {})
                        driver.request("POST", f"element/{element}/value", {"text": step["text"]})
                elif action == "assert":
                    def matches():
                        element = driver.find(step["selector"])
                        actual = driver.request("GET", f"element/{element}/attribute/{step.get('attribute', 'text')}")
                        entry["actual"], entry["expected"] = actual, step["equals"]
                        return str(actual) == str(step["equals"])
                    wait_for(matches, step.get("timeout", 15))
                    count += 1
                elif action == "assertAbsent":
                    def absent():
                        return not driver.request("POST", "elements", step["selector"])
                    wait_for(absent, step.get("timeout", 15))
                    entry["expected"], entry["actual"] = "absent", "absent"
                    count += 1
                elif action == "back":
                    driver.request("POST", "back", {})
                elif action == "hideKeyboard":
                    if driver.request("GET", "appium/device/is_keyboard_shown"):
                        driver.request("POST", "appium/device/hide_keyboard", {})
                elif action == "restart":
                    before = adb("shell", "pidof", args.package)
                    adb("shell", "am", "force-stop", args.package)
                    adb("shell", "am", "start", "-W", "-n", f"{args.package}/{args.activity}")
                    after = wait_for(lambda: adb("shell", "pidof", args.package), 20)
                    if not before or before == after:
                        raise ValueError("Restart did not replace the app process")
                    report["restarts"].append({"beforePid": before, "afterPid": after})
                    wait_for(lambda: driver.request("GET", "appium/device/current_package") == args.package, 20)
                else:
                    raise ValueError(f"Unknown native action: {action}")
                entry["ok"] = True
                if action in {"assert", "assertAbsent", "restart"}:
                    capture(label)
            if not count:
                raise ValueError(f"Scenario has no assertions: {scenario['name']}")
            tags = scenario.get("tags", [])
            if "persistence" in tags:
                actions = [t["action"] for t in report["trace"] if t["scenario"] == scenario["name"]]
                if "restart" not in actions or not any(a == "assert" for a in actions[actions.index("restart") + 1:]):
                    raise ValueError("Persistence scenarios need a real restart followed by a state assertion")
            report["scenarios"].append({"name": scenario["name"], "tags": tags, "assertions": count})
            report["assertions"] += count
        if report.get("captureErrors") or not report["assertions"]:
            raise ValueError("Native evidence is incomplete")
        if snapshot(root) != report["source"]:
            raise ValueError("Source changed during native checks")
        report["ok"] = True
    except Exception as exc:
        report["error"] = redact(str(exc))
        if driver.session:
            capture("failure")
    finally:
        if not report["ok"] and not any(p.endswith(".png") for p in report["artifacts"]):
            with contextlib.suppress(Exception):
                fallback = subprocess.run([args.adb, "-s", args.device, "exec-out", "screencap", "-p"],
                                          capture_output=True, timeout=30)
                if fallback.returncode == 0 and fallback.stdout.startswith(b"\x89PNG\r\n\x1a\n"):
                    save("startup-failure.png", fallback.stdout, binary=True)
        if driver.session:
            with contextlib.suppress(Exception):
                if initial_orientation:
                    driver.request("POST", "orientation", {"orientation": initial_orientation})
            with contextlib.suppress(Exception):
                driver.close()
        if initial_font is not None:
            with contextlib.suppress(Exception):
                if initial_font == "null":
                    adb("shell", "settings", "delete", "system", "font_scale")
                else:
                    adb("shell", "settings", "put", "system", "font_scale", initial_font)
        if initial_night in {"yes", "no", "auto", "custom"}:
            with contextlib.suppress(Exception):
                adb("shell", "cmd", "uimode", "night", initial_night)
        with contextlib.suppress(Exception):
            pid = adb("shell", "pidof", args.package)
            if pid:
                save("app.log", adb("logcat", "-d", "--pid", pid))
        write_json(output, report)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default=".")
    parser.add_argument("--server", default="http://127.0.0.1:4725")
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--device", required=True)
    parser.add_argument("--app", required=True)
    parser.add_argument("--package", required=True)
    parser.add_argument("--activity", required=True)
    parser.add_argument("--apk", required=True)
    parser.add_argument("--suite", required=True)
    parser.add_argument("--output", default=".maui-agent/native/report.json")
    parser.add_argument("--configuration", choices=["Debug", "Release"], default="Release")
    parser.add_argument("--clear-test-data", action="store_true")
    parser.add_argument("--orientation", choices=["PORTRAIT", "LANDSCAPE"])
    parser.add_argument("--font-scale", type=float)
    parser.add_argument("--night-mode", choices=["yes", "no"])
    args = parser.parse_args()
    try:
        report = run(args)
        print(json.dumps(report, indent=2))
        return 0 if report["ok"] else 1
    except Exception as exc:
        print(json.dumps({"ok": False, "error": redact(str(exc))}))
        return 2


if __name__ == "__main__":
    sys.exit(main())
