#!/usr/bin/env python3
"""Launch an explicit Android component and verify the selected live DevFlow endpoint."""
import argparse
import json
from pathlib import Path
import re
import subprocess
import time
import urllib.request

from lifecycle import inside, now, redact, write_json


def component_for(package, output):
    matches = [line.strip() for line in output.splitlines() if line.strip().startswith(package + "/")]
    if len(matches) != 1:
        raise ValueError("Expected one launcher component for the selected package")
    return matches[0]


def agent_port(log):
    matches = re.findall(r"(?:Agent|HTTP server) started on port (\d+)\b", log)
    if matches and 1 <= int(matches[-1]) <= 65535:
        return int(matches[-1])
    return None


def verify_status(status, app, package, pid):
    if (status.get("running") is not True or status.get("app", {}).get("name") != app
            or status.get("app", {}).get("packageId") != package
            or str(status.get("app", {}).get("processId")) != pid
            or status.get("device", {}).get("platform", "").lower() != "android"):
        raise ValueError("Forwarded endpoint does not match the selected app/package/process/platform")


def run(args):
    root = Path(args.root).resolve()
    output = inside(root, args.output)
    if not output.is_relative_to(root / ".maui-agent"):
        raise ValueError("Launch report must be under .maui-agent/")
    report = {"ok": False, "at": now(), "device": args.device, "package": args.package, "app": args.app}
    def adb(*argv, timeout=30):
        r = subprocess.run([args.adb, "-s", args.device, *argv], capture_output=True, text=True, timeout=timeout)
        if r.returncode:
            if argv[:2] == ("shell", "pidof") and r.returncode == 1 and not (r.stdout.strip() or r.stderr.strip()):
                return ""
            raise ValueError(redact(r.stdout + r.stderr))
        return r.stdout.strip()
    try:
        # A host forward is shared by all devices; never replace another test's tunnel.
        for line in adb("forward", "--list").splitlines():
            fields = line.split()
            if len(fields) == 3 and fields[1] == f"tcp:{args.host_port}" and fields[0] != args.device:
                raise ValueError("Selected host port is forwarded to another device; choose another host port")
        component = component_for(args.package, adb("shell", "cmd", "package", "resolve-activity", "--brief", args.package))
        report["activity"] = component.split("/", 1)[1]
        adb("reverse", f"tcp:{args.broker_port}", f"tcp:{args.broker_port}")
        report["launch"] = adb("shell", "am", "start", "-W", "-n", component, timeout=90)
        end = time.monotonic() + args.timeout
        while time.monotonic() < end:
            pid = adb("shell", "pidof", args.package)
            if not pid.isdigit():
                time.sleep(.25)
                continue
            # This log is only a port-discovery hint; live identity below is authoritative.
            port = agent_port(adb("logcat", "-d", "--pid", pid, "-t", "1000"))
            if port:
                adb("forward", f"tcp:{args.host_port}", f"tcp:{port}")
                try:
                    with urllib.request.urlopen(f"http://127.0.0.1:{args.host_port}/api/v1/agent/status", timeout=3) as r:
                        status = json.load(r)
                except (OSError, ValueError):
                    time.sleep(.25)
                    continue
                verify_status(status, args.app, args.package, pid)
                report.update(ok=True, agentPort=port, hostPort=args.host_port, processId=int(pid), status=status)
                break
            time.sleep(.25)
        if not report["ok"]:
            raise ValueError("No verified DevFlow endpoint became ready before the launch deadline")
    except Exception as exc:
        report["error"] = redact(str(exc))
    write_json(output, report)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default=".")
    parser.add_argument("--adb", default="adb")
    parser.add_argument("--device", required=True)
    parser.add_argument("--package", required=True)
    parser.add_argument("--app", required=True)
    parser.add_argument("--host-port", type=int, default=9223)
    parser.add_argument("--broker-port", type=int, default=19223)
    parser.add_argument("--timeout", type=int, default=120)
    parser.add_argument("--output", default=".maui-agent/debug-launch/report.json")
    args = parser.parse_args()
    if not 1 <= args.host_port <= 65535 or not 1 <= args.broker_port <= 65535 or not 1 <= args.timeout <= 600:
        parser.error("Select valid ports and a 1–600 second launch deadline")
    try:
        report = run(args)
        print(json.dumps(report, indent=2))
        return 0 if report["ok"] else 1
    except (ValueError, OSError) as exc:
        print(json.dumps({"ok": False, "error": redact(str(exc))}))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
