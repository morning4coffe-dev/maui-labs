#!/usr/bin/env python3
"""Replay MAUI flows through the real DevFlow MCP server and capture UI evidence."""
from __future__ import annotations

import argparse
import base64
import contextlib
import json
from pathlib import Path
import queue
import subprocess
import sys
import threading
import time

from lifecycle import inside, now, redact, write_json


class McpClient:
    """Newline-delimited stdio MCP; a single session preserves the mutation lease."""
    def __init__(self, command, root, timeout=60):
        self.timeout, self.sequence = timeout, 0
        self.messages = queue.Queue()
        self.process = subprocess.Popen(command, cwd=root, stdin=subprocess.PIPE,
                                        stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                        text=True, encoding="utf-8", bufsize=1)
        self.reader = threading.Thread(target=self._read, daemon=True)
        self.reader.start()

    def _read(self):
        try:
            for line in self.process.stdout:
                try:
                    self.messages.put(json.loads(line))
                except json.JSONDecodeError:
                    # Startup banners do not count as protocol responses.
                    continue
        finally:
            self.messages.put(None)

    def send(self, value):
        self.process.stdin.write(json.dumps(value) + "\n")
        self.process.stdin.flush()

    def request(self, method, params=None):
        self.sequence += 1
        request_id = self.sequence
        self.send({"jsonrpc": "2.0", "id": request_id, "method": method, "params": params or {}})
        deadline = time.monotonic() + self.timeout
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise ValueError(f"MCP timeout: {method}")
            try:
                message = self.messages.get(timeout=remaining)
            except queue.Empty:
                raise ValueError(f"MCP timeout: {method}")
            if message is None:
                raise ValueError(f"MCP server exited during {method}")
            if message.get("id") != request_id:
                continue
            if "error" in message:
                raise ValueError(f"MCP {method}: {message['error']}")
            return message["result"]

    def initialize(self):
        self.request("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                                    "clientInfo": {"name": "maui-app-lifecycle", "version": "1.0.0"}})
        self.send({"jsonrpc": "2.0", "method": "notifications/initialized"})
        return self.request("tools/list")

    def call(self, name, **arguments):
        result = self.request("tools/call", {"name": name, "arguments": arguments})
        if result.get("isError"):
            raise ValueError(f"{name}: {result.get('content')}")
        return result

    def data(self, name, allow_failure=False, **arguments):
        result = self.call(name, **arguments)
        texts = [c["text"] for c in result.get("content", []) if c.get("type") == "text"]
        if len(texts) != 1:
            raise ValueError(f"{name} did not return a single JSON payload")
        try:
            data = json.loads(texts[0])
        except json.JSONDecodeError:
            raise ValueError(f"{name}: {texts[0]}")
        if not allow_failure and isinstance(data, dict) and (data.get("ok") is False or data.get("error")):
            raise ValueError(f"{name}: {data}")
        return data

    def close(self):
        with contextlib.suppress(OSError):
            self.process.stdin.close()
        try:
            self.process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait()
        self.process.stdout.close()
        self.reader.join(timeout=1)


def assertion_count(report):
    results = report.get("results", [])
    if report.get("ok") is not True or report.get("failed") != 0 or not results:
        raise ValueError("Replay did not pass")
    if report.get("total") != len(results) or report.get("passed") != len(results):
        raise ValueError("Replay result counts are inconsistent")
    count = 0
    for step in results:
        if step.get("ok") is not True:
            raise ValueError("A replay step failed")
        for assertion in step.get("asserts", []):
            if not assertion.get("skipped") and assertion.get("ok") is True:
                count += 1
            elif not assertion.get("skipped"):
                raise ValueError("An evaluated assertion did not pass")
    if not count:
        raise ValueError("Replay has no evaluated hard assertions; screenshots and annotations are not assertions")
    return count


def run(args):
    root = Path(args.root).resolve()
    output = inside(root, args.output)
    if not output.is_relative_to(root / ".maui-agent"):
        raise ValueError("Evidence output must be under .maui-agent/")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.unlink(missing_ok=True)
    report = {"ok": False, "at": now(), "app": args.app, "platform": args.platform,
              "assertions": 0, "artifacts": [], "limitations": [], "flows": []}
    client = McpClient([args.maui, "devflow", "mcp"], root, args.timeout)
    port = None

    def save(name, data):
        path = output.parent / name
        write_json(path, json.loads(redact(json.dumps(data))))
        report["artifacts"].append(str(path.relative_to(root)))

    def screenshot(name):
        response = client.call("maui_screenshot", agentPort=port)
        images = [c for c in response.get("content", []) if c.get("type") == "image"]
        if not images:
            raise ValueError("Screenshot returned no image")
        path = output.parent / name
        image = base64.b64decode(images[0]["data"], validate=True)
        if not image.startswith(b"\x89PNG\r\n\x1a\n"):
            raise ValueError("Screenshot is not a PNG")
        path.write_bytes(image)
        report["artifacts"].append(str(path.relative_to(root)))

    try:
        tools = client.initialize()
        save("tools.json", tools)
        available = {t["name"] for t in tools.get("tools", [])}
        required = {"maui_list_agents", "maui_status", "maui_capabilities", "maui_tree",
                    "maui_screenshot", "maui_flow_validate", "maui_flow_replay",
                    "maui_take_control", "maui_release_control"}
        if required - available:
            raise ValueError(f"CLI lacks required tools: {sorted(required - available)}; update maui CLI")
        if args.agent_port is not None:
            # An explicit forwarded endpoint is valid even when broker discovery is unavailable.
            port = args.agent_port
        else:
            agents = client.data("maui_list_agents")
            matches = [a for a in agents if a.get("appName") == args.app
                       and a.get("platform", "").lower() == args.platform.lower()]
            if len(matches) != 1:
                raise ValueError(f"Expected one matching app/platform, found {len(matches)}; select the target explicitly")
            port = matches[0]["port"]
            save("agent.json", matches[0])
        status = client.data("maui_status", agentPort=port)
        actual_app = status.get("appName") or (status.get("app") or {}).get("name")
        actual_platform = status.get("platform") or (status.get("device") or {}).get("platform")
        if actual_app != args.app or str(actual_platform).lower() != args.platform.lower():
            raise ValueError("Live app identity differs from the broker registration")
        save("status.json", status)
        save("capabilities.json", client.data("maui_capabilities", agentPort=port))
        # Validate ALL flows before driving any live app state.
        for flow in args.flow:
            path = inside(root, flow)
            validation = client.data("maui_flow_validate", file=str(path))
            if validation.get("ok") is not True or not validation.get("steps"):
                raise ValueError(f"Flow is invalid or empty: {flow}")
        lease = client.data("maui_take_control", agentPort=port, force=False)
        if lease.get("allowed") is not True or lease.get("heldByOther"):
            raise ValueError("Another developer or agent holds control; no forced takeover performed")
        tree = client.data("maui_tree", agentPort=port)
        if not tree:
            raise ValueError("Visual tree is empty")
        save("before-tree.json", tree)
        screenshot("before.png")
        for index, flow in enumerate(args.flow):
            if args.reset_action:
                reset = client.call("maui_invoke_action", agentPort=port, actionName=args.reset_action)
                text = "\n".join(c.get("text", "") for c in reset.get("content", []))
                if not text.startswith(f"Action '{args.reset_action}' completed."):
                    raise ValueError(f"Reset action failed: {text}")
            replay = client.data("maui_flow_replay", allow_failure=True, agentPort=port, file=str(inside(root, flow)))
            save(f"flow-{index}.json", replay)
            save(f"after-{index}-tree.json", client.data("maui_tree", agentPort=port))
            screenshot(f"after-{index}.png")
            count = assertion_count(replay)
            report["assertions"] += count
            report["flows"].append({"file": flow, "assertions": count})
            try:
                save(f"layout-{index}.json", client.data("maui_layout_diagnostics", agentPort=port,
                                                       profile="ci", includeEvidence=True))
            except ValueError as exc:
                report["limitations"].append(str(exc))
                if args.require_layout:
                    raise
        if "maui_logs" in available:
            try:
                save("logs.json", client.data("maui_logs", agentPort=port))
            except ValueError as exc:
                report["limitations"].append(str(exc))
        report["ok"] = True
    except (ValueError, OSError, KeyError, TypeError) as exc:
        report["error"] = redact(str(exc))
    finally:
        if port is not None:
            with contextlib.suppress(Exception):
                client.call("maui_release_control", agentPort=port)
        client.close()
        write_json(output, report)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default=".")
    parser.add_argument("--maui", default="maui", help="Path to the maui executable")
    parser.add_argument("--app", required=True, help="Exact running app name")
    parser.add_argument("--platform", required=True)
    parser.add_argument("--agent-port", type=int)
    parser.add_argument("--flow", action="append", required=True, help="Trusted Markdown flow file")
    parser.add_argument("--reset-action", help="App-provided Debug action to reset state before each flow")
    parser.add_argument("--output", default=".maui-agent/ui/report.json")
    parser.add_argument("--timeout", type=float, default=90)
    parser.add_argument("--require-layout", action="store_true")
    args = parser.parse_args()
    try:
        report = run(args)
        print(json.dumps(report, indent=2))
        return 0 if report["ok"] else 1
    except (ValueError, OSError) as exc:
        print(json.dumps({"ok": False, "error": redact(str(exc))}))
        return 2


if __name__ == "__main__":
    sys.exit(main())
