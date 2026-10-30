#!/usr/bin/env python3
"""Watch existing XAML literals and refresh verified DevFlow controls without a rebuild."""
import argparse
import base64
import contextlib
import json
import math
from pathlib import Path
import time
import xml.etree.ElementTree as ET

from devflow_check import McpClient
from lifecycle import digest, inside, now, redact, write_json

PROPERTIES = {"Text", "FontSize", "Opacity", "IsVisible", "IsEnabled", "CornerRadius"}


def parse_xaml(data):
    if len(data) > 1_048_576 or b"<!DOCTYPE" in data.upper():
        raise ValueError("Use a local XAML file under 1 MiB without a DTD")
    return ET.fromstring(data)


def literal_changes(before, after):
    old, new = parse_xaml(before), parse_xaml(after)
    changes = []
    identifiers = [n.get("AutomationId") for n in old.iter() if n.get("AutomationId")]

    def compare(a, b, template=False):
        kind = a.tag.split("}")[-1]
        template = template or kind in {"DataTemplate", "ControlTemplate", "Style", "ResourceDictionary"}
        if (a.tag != b.tag or len(a) != len(b) or set(a.attrib) != set(b.attrib)
                or (a.text or "").strip() != (b.text or "").strip()
                or (a.tail or "").strip() != (b.tail or "").strip()):
            raise ValueError("Structural, resource or added-attribute changes require a rebuild")
        for name, value in a.attrib.items():
            replacement = b.attrib[name]
            if replacement == value:
                continue
            identity = a.get("AutomationId")
            if (template or kind not in {"Button", "Label"} or name not in PROPERTIES
                    or (name == "CornerRadius" and kind != "Button")
                    or not identity or identifiers.count(identity) != 1
                    or identity.lstrip().startswith("{")
                    or value.lstrip().startswith("{") or replacement.lstrip().startswith("{")):
                raise ValueError("Only existing supported literals on a unique AutomationId Label/Button can refresh; rebuild this edit")
            equivalent(name, value, value)
            equivalent(name, replacement, replacement)
            changes.append({"automationId": identity, "type": kind, "property": name,
                            "before": value, "after": replacement})
        for left, right in zip(a, b):
            compare(left, right, template)
    compare(old, new)
    if len(changes) > 8:
        raise ValueError("Refresh at most eight literal properties in one revision; rebuild larger edits")
    return changes


def equivalent(name, actual, expected):
    if name in {"FontSize", "Opacity", "CornerRadius"}:
        values = [float(v) for v in [actual, expected]]
        if not all(math.isfinite(v) for v in values):
            raise ValueError("Non-finite XAML numbers are unsupported")
        return math.isclose(*values, rel_tol=1e-7, abs_tol=1e-7)
    if name in {"IsVisible", "IsEnabled"}:
        values = [str(v).lower() for v in [actual, expected]]
        if any(v not in {"true", "false"} for v in values):
            raise ValueError("Boolean XAML literals must be true or false")
        return values[0] == values[1]
    return str(actual) == expected


def property_value(client, port, target, name):
    response = client.call("maui_get_property", elementId=target["id"], property=name, agentPort=port)
    texts = [c["text"] for c in response.get("content", []) if c.get("type") == "text"]
    if len(texts) != 1:
        raise ValueError("Property response must contain one value")
    try:
        value = json.loads(texts[0])
        return value.get("value", value) if isinstance(value, dict) else value
    except json.JSONDecodeError:
        return texts[0]


def refresh(client, port, changes, expected_pid, status, screenshot, source_name=None, source_hash=None):
    def target(change):
        # The query endpoint omits Debug source metadata; use the full tree so
        # source stamps remain mandatory instead of silently waiving that guard.
        tree = client.data("maui_tree", agentPort=port)
        matches = []
        def visit(nodes):
            for node in nodes:
                if node.get("automationId") == change["automationId"]:
                    matches.append(node)
                visit(node.get("children") or [])
        visit(tree)
        if len(matches) != 1 or matches[0].get("type") != change["type"]:
            raise ValueError("Expected exactly one live control matching the XAML AutomationId and type")
        if source_name and (Path(matches[0].get("sourceFile", "").replace("\\", "/")).name != source_name
                            or matches[0].get("sourceHash") != source_hash):
            raise ValueError("Runtime source metadata does not match the starting XAML; rebuild the app baseline")
        return matches[0]

    def apply(change, value):
        item = target(change)
        client.call("maui_set_property", agentPort=port, elementId=item["id"],
                    property=change["property"], value=value, captureEpoch=item.get("captureEpoch"),
                    registryGeneration=item.get("registryGeneration"))

    lease = client.data("maui_take_control", agentPort=port, force=False)
    if lease.get("allowed") is not True or lease.get("heldByOther"):
        raise ValueError("Another session holds control; no forced takeover performed")
    attempted = []
    try:
        if status()["app"]["processId"] != expected_pid:
            raise ValueError("App restarted; rebuild and start a new watch baseline")
        # Validate the entire batch before mutating any control.
        for change in changes:
            if not equivalent(change["property"], property_value(client, port, target(change), change["property"]), change["before"]):
                raise ValueError("Runtime differs from the source baseline; rebuild rather than silently rebaseline")
        for change in changes:
            attempted.append(change)
            apply(change, change["after"])
            actual = property_value(client, port, target(change), change["property"])
            if not equivalent(change["property"], actual, change["after"]):
                raise ValueError("DevFlow property refresh did not reach the expected runtime value")
        if status()["app"]["processId"] != expected_pid:
            raise ValueError("App process changed during refresh")
        screenshot()
    except Exception:
        rollback_errors = []
        for change in reversed(attempted):
            try:
                apply(change, change["before"])
            except Exception as exc:
                rollback_errors.append(str(exc))
        if rollback_errors:
            raise ValueError("Refresh failed and runtime rollback was incomplete; rebuild: " + "; ".join(rollback_errors))
        raise
    finally:
        client.call("maui_release_control", agentPort=port)


def run(args):
    root = Path(args.root).resolve()
    source, output = inside(root, args.source), inside(root, args.output)
    if source.suffix != ".xaml" or not output.is_relative_to(root / ".maui-agent"):
        raise ValueError("Select workspace XAML and an evidence output under .maui-agent/")
    baseline = source.read_bytes()
    compiled_hash = digest(baseline.removeprefix(b"\xef\xbb\xbf"))[:32]
    parse_xaml(baseline)
    report = {"ok": False, "mode": "devflow-literal-refresh", "at": now(), "app": args.app,
              "package": args.package, "sourceFile": args.source, "revisions": [], "artifacts": [],
              "limitations": ["Existing literal Label/Button properties only; no C# or structural reload. Development evidence is not delivery QA."]}
    client = McpClient([args.maui, "devflow", "mcp"], root, args.timeout)
    def status():
        value = client.data("maui_status", agentPort=args.agent_port)
        if value.get("app", {}).get("name") != args.app or value.get("app", {}).get("packageId") != args.package:
            raise ValueError("Live app identity changed or does not match the selected package")
        return value
    def capture(name):
        result = client.call("maui_screenshot", agentPort=args.agent_port)
        images = [c for c in result.get("content", []) if c.get("type") == "image"]
        if not images:
            raise ValueError("Screenshot evidence is missing")
        data = base64.b64decode(images[0]["data"], validate=True)
        if not data.startswith(b"\x89PNG\r\n\x1a\n"):
            raise ValueError("Screenshot evidence is not a PNG")
        output.parent.mkdir(parents=True, exist_ok=True)
        path = output.parent / name
        path.write_bytes(data)
        report["artifacts"].append(str(path.relative_to(root)))
    try:
        client.initialize()
        initial = status()
        report["beforeStatus"] = initial
        pid = initial["app"]["processId"]
        capture("before.png")
        print(json.dumps({"state": "watching", "app": args.app, "processId": pid,
                          "source": args.source, "sha256": digest(baseline)}), flush=True)
        end = time.monotonic() + args.duration
        while time.monotonic() < end and len(report["revisions"]) < args.max_updates:
            candidate = source.read_bytes()
            if candidate == baseline:
                time.sleep(0.25)
                continue
            time.sleep(0.25)
            if candidate != source.read_bytes():
                continue
            changes = literal_changes(baseline, candidate)
            if not changes:
                baseline = candidate
                continue
            revision = {"beforeSha256": digest(baseline), "afterSha256": digest(candidate), "changes": changes}
            refresh(client, args.agent_port, changes, pid, status,
                    lambda: capture(f"revision-{len(report['revisions']) + 1}.png"), source.name, compiled_hash)
            if source.read_bytes() != candidate:
                raise ValueError("Source changed during refresh; inspect the captured runtime and rebuild")
            revision["processId"] = status()["app"]["processId"]
            report["revisions"].append(revision)
            report["afterStatus"] = status()
            write_json(output, report)
            print(json.dumps({"state": "refreshed", **revision}), flush=True)
            baseline = candidate
        report["ok"] = True
    except Exception as exc:
        report["error"] = redact(str(exc))
        with contextlib.suppress(Exception):
            capture("failure.png")
    finally:
        client.close()
        write_json(output, report)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default=".")
    parser.add_argument("--maui", default="maui")
    parser.add_argument("--source", required=True)
    parser.add_argument("--app", required=True)
    parser.add_argument("--package", required=True)
    parser.add_argument("--agent-port", required=True, type=int)
    parser.add_argument("--output", default=".maui-agent/literal-refresh/report.json")
    parser.add_argument("--duration", type=int, default=300)
    parser.add_argument("--max-updates", type=int, default=20)
    parser.add_argument("--timeout", type=int, default=30)
    args = parser.parse_args()
    if not 1 <= args.duration <= 3600 or not 1 <= args.max_updates <= 100:
        parser.error("Duration must be 1–3600 seconds; max updates must be 1–100")
    try:
        report = run(args)
        print(json.dumps(report, indent=2))
        return 0 if report["ok"] else 1
    except (ValueError, OSError) as exc:
        print(json.dumps({"ok": False, "error": redact(str(exc))}))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
