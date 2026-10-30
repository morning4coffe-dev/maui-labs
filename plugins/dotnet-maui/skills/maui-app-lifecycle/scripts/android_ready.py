#!/usr/bin/env python3
"""Collect bounded, read-only Android readiness evidence before spending app-test attempts."""
import argparse
import json
from pathlib import Path
import re
import subprocess
import time

from lifecycle import inside, now, redact, write_json


def check(root, device, output=".maui-agent/android-ready/report.json", adb="adb", timeout=60, runner=None):
    root = Path(root).resolve()
    output = inside(root, output)
    if not output.is_relative_to(root / ".maui-agent"):
        raise ValueError("Readiness evidence must be under .maui-agent/")
    frame = inside(root, output.parent / "screen.png")
    if not frame.is_relative_to(root / ".maui-agent"):
        raise ValueError("Readiness screenshots must stay under .maui-agent/")
    if not device or any(c.isspace() for c in device) or not 1 <= timeout <= 120:
        raise ValueError("Select one explicit device and a 1–120 second deadline")
    runner = runner or subprocess.run
    report = {"ok": False, "mode": "android-infrastructure", "at": now(), "device": device,
              "checks": {}, "artifacts": [], "limitations": ["Readiness is not app behavior, accessibility or delivery QA. A failure does not classify the fault owner."]}
    end = time.monotonic() + timeout

    def call(stage, *args):
        report["stage"] = stage
        remaining = end - time.monotonic()
        if remaining <= 0:
            raise ValueError("Readiness deadline expired")
        try:
            result = runner([adb, "-s", device, *args], capture_output=True, timeout=min(15, remaining))
        except subprocess.TimeoutExpired:
            raise ValueError(f"Selected device timed out at {stage}") from None
        if result.returncode:
            raise ValueError(f"Selected device failed at {stage}: " + result.stderr.decode(errors="replace")[:1000])
        return result.stdout

    def require(stage, actual, expected):
        report["checks"][stage] = actual
        if actual != expected:
            raise ValueError(f"Selected device is not ready at {stage}: {actual}")

    try:
        require("connection", call("connection", "get-state").decode().strip(), "device")
        require("boot", call("boot", "shell", "getprop", "sys.boot_completed").decode().strip(), "1")
        require("user", call("user", "shell", "am", "get-started-user-state", "0").decode().strip(), "RUNNING_UNLOCKED")
        pids = call("system-ui", "shell", "pidof", "com.android.systemui").decode().split()
        if not pids or not all(p.isdigit() for p in pids):
            raise ValueError("System UI has no verified process")
        report["checks"]["systemUiPids"] = pids
        package = call("package-manager", "shell", "cmd", "package", "path", "android").decode().strip()
        if not package.startswith("package:"):
            raise ValueError("Package Manager did not resolve the framework package")
        report["checks"]["frameworkPackage"] = package
        windows = call("window", "shell", "dumpsys", "window", "displays").decode(errors="replace")
        focus = re.search(r"mCurrentFocus=(.+)", windows)
        report["checks"]["focus"] = focus.group(1).strip() if focus else None
        if not focus or focus.group(1).strip() == "null":
            raise ValueError("No focused window is observable; inspect device startup")
        if "Application Not Responding" in windows or "FallbackHome" in focus.group(1):
            raise ValueError("Android is showing an ANR or startup fallback; investigate the cause before app QA")
        screen = call("screen", "exec-out", "screencap", "-p")
        if (not screen.startswith(b"\x89PNG\r\n\x1a\n")
                or not screen.endswith(b"\x00\x00\x00\x00IEND\xaeB`\x82")):
            raise ValueError("Device did not return a complete PNG screenshot")
        frame.parent.mkdir(parents=True, exist_ok=True)
        frame.write_bytes(screen)
        report["artifacts"].append(str(frame.relative_to(root)))
        report.update(ok=True, stage="ready")
    except (ValueError, OSError) as exc:
        report["error"] = redact(str(exc))
    write_json(output, report)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default=".")
    parser.add_argument("--device", required=True)
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--timeout", type=int, default=60)
    parser.add_argument("--output", default=".maui-agent/android-ready/report.json")
    args = parser.parse_args()
    try:
        result = check(args.root, args.device, args.output, args.adb, args.timeout)
        print(json.dumps(result, indent=2))
        return 0 if result["ok"] else 1
    except (ValueError, OSError) as exc:
        print(json.dumps({"ok": False, "error": redact(str(exc))}))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
