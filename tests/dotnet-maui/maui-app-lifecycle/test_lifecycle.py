"""Behavioral acceptance tests: real temporary Git repos and child processes."""
import argparse
import contextlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

SCRIPTS = Path(__file__).resolve().parents[3] / "plugins/dotnet-maui/skills/maui-app-lifecycle/scripts"
sys.path.insert(0, str(SCRIPTS))
from lifecycle import Workflow, digest, execute, redact, snapshot, write_json
from devflow_check import McpClient, assertion_count
from mcp_server import serve


class WorkflowTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name).resolve()
        subprocess.run(["git", "init", "-b", "main", str(self.root)], check=True, capture_output=True)
        self.git("config", "user.email", "qa@example.invalid")
        self.git("config", "user.name", "MAUI QA")
        (self.root / "app.txt").write_text("version 1")
        self.git("add", ".")
        self.git("commit", "-m", "initial")
        self.workflow = Workflow(self.root)
        self.workflow.init("App", ["android"], ["Counter increments by one"])

    def tearDown(self):
        self.temporary.cleanup()

    def git(self, *args):
        return subprocess.run(["git", *args], cwd=self.root, check=True, capture_output=True, text=True).stdout.strip()

    def plan(self, checks):
        path = self.root / "maui-agent.json"
        plan = json.loads(path.read_text())
        plan["checks"] = checks
        write_json(path, plan)

    def command(self, name="build", code="print('built')", **extra):
        return {"name": name, "kind": "build", "platform": "android",
                "command": [sys.executable, "-c", code], **extra}

    def test_empty_plan_blocks_all_platform_gates(self):
        status = self.workflow.status()
        self.assertFalse(status["ok"])
        self.assertTrue(any("unit" in b for b in status["blockers"]))
        self.assertTrue(any("Acceptance 0" in b for b in status["blockers"]))

    def test_no_shell_interpolation(self):
        command = self.command(code="import sys; print(sys.argv[1])")
        command["command"].append("$(touch SHOULD_NOT_EXIST); echo unsafe")
        self.plan([command])
        self.assertTrue(self.workflow.check("build")["ok"])
        self.assertFalse((self.root / "SHOULD_NOT_EXIST").exists())

    def test_source_changes_invalidate_success(self):
        self.plan([self.command()])
        self.workflow.check("build")
        self.assertEqual(self.workflow.status()["coverage"][0]["status"], "passed")
        (self.root / "app.txt").write_text("version 2")
        self.assertEqual(self.workflow.status()["coverage"][0]["status"], "stale")

    def test_untracked_source_and_plan_are_fingerprinted(self):
        original = snapshot(self.root)
        (self.root / "new-source.cs").write_text("new code")
        self.assertNotEqual(original, snapshot(self.root))
        original = snapshot(self.root)
        self.plan([self.command()])
        self.assertNotEqual(original, snapshot(self.root))

    def test_committing_same_source_preserves_evidence(self):
        self.plan([self.command()])
        record = self.workflow.check("build")
        self.git("add", ".")
        self.git("commit", "-m", "app")
        self.assertEqual(record["source"], snapshot(self.root))
        self.assertEqual(self.workflow.status()["coverage"][0]["status"], "passed")

    def test_evidence_tampering_invalidates_pass(self):
        self.plan([self.command()])
        record = self.workflow.check("build")
        evidence = self.root / next(iter(record["evidence"]))
        evidence.write_text("forged")
        self.assertEqual(self.workflow.status()["coverage"][0]["status"], "failed")

    def test_timeout_is_a_failed_attempt(self):
        self.plan([self.command(code="import time; time.sleep(10)", timeout=0.1)])
        result = self.workflow.check("build")
        self.assertFalse(result["ok"])
        self.assertTrue(result["timedOut"])

    def test_source_mutation_during_check_is_not_a_pass(self):
        self.plan([self.command(code="from pathlib import Path; Path('app.txt').write_text('changed')")])
        result = self.workflow.check("build")
        self.assertFalse(result["ok"])
        self.assertIn("Source changed", result["reason"])

    def test_repeated_failure_requires_classification_then_budget_stops(self):
        self.plan([self.command(code="raise SystemExit(1)")])
        self.workflow.check("build")
        second = self.workflow.check("build")
        with self.assertRaisesRegex(ValueError, "Classify"):
            self.workflow.check("build")
        self.workflow.classify(second["id"], "infrastructure", "SDK command exited 1 in sealed log")
        self.workflow.check("build")
        with self.assertRaisesRegex(ValueError, "budget exhausted"):
            self.workflow.check("build")
        self.workflow.cycle("Human authorized another investigation")
        self.assertEqual(self.workflow.status()["cycle"], 2)

    def test_empty_and_failed_trx_cannot_pass(self):
        for counters in ["executed='0' passed='0'", "executed='2' passed='1' failed='1'", "executed='2' passed='1'"]:
            with self.subTest(counters=counters):
                xml = f"<TestRun><Counters {counters}/></TestRun>"
                code = f"from pathlib import Path; Path('.maui-agent/unit.trx').write_text({xml!r})"
                self.plan([self.command(name="unit", code=code, kind="unit", platform="host", format="trx", result=".maui-agent/unit.trx")])
                self.assertFalse(self.workflow.check("unit")["ok"])

    def test_stale_report_is_deleted_before_command(self):
        path = self.root / ".maui-agent/unit.trx"
        path.write_text("<TestRun><Counters executed='1' passed='1'/></TestRun>")
        self.plan([self.command(name="unit", kind="unit", platform="host", format="trx", result=".maui-agent/unit.trx")])
        self.assertFalse(self.workflow.check("unit")["ok"])

    def test_failed_test_run_keeps_trx_evidence(self):
        code = "from pathlib import Path; Path('.maui-agent/unit.trx').write_text(\"<TestRun><Counters executed='1' passed='0' failed='1'/></TestRun>\"); raise SystemExit(1)"
        self.plan([self.command(name="unit", code=code, kind="unit", platform="host", format="trx", result=".maui-agent/unit.trx")])
        record = self.workflow.check("unit")
        self.assertFalse(record["ok"])
        self.assertTrue(any(name.endswith("unit.trx") for name in record["evidence"]))
        self.assertTrue(self.workflow.verify(record))

    def test_failed_ui_run_keeps_report_and_screenshot_evidence(self):
        code = "from pathlib import Path; import json; Path('.maui-agent/failure.png').write_bytes(b'PNG evidence'); Path('.maui-agent/ui.json').write_text(json.dumps({'ok':False,'artifacts':['.maui-agent/failure.png']})); raise SystemExit(1)"
        self.plan([self.command(name="ui", code=code, kind="ui", format="devflow", result=".maui-agent/ui.json")])
        record = self.workflow.check("ui")
        self.assertFalse(record["ok"])
        self.assertTrue(any(name.endswith("failure.png") for name in record["evidence"]))
        self.assertTrue(any(name.endswith("ui.json") for name in record["evidence"]))

    def test_result_outside_state_is_not_deleted(self):
        path = self.root / "keep.trx"
        path.write_text("do not delete")
        self.plan([self.command(name="unit", kind="unit", format="trx", result="keep.trx")])
        with self.assertRaisesRegex(ValueError, "under .maui-agent"):
            self.workflow.check("unit")
        self.assertTrue(path.exists())

    def test_traversal_is_rejected(self):
        self.plan([self.command(name="ui", kind="ui", format="devflow", result="../report.json")])
        with self.assertRaisesRegex(ValueError, "leaves"):
            self.workflow.check("ui")

    def passing_plan(self):
        trx_code = "from pathlib import Path; Path('.maui-agent/unit.trx').write_text(\"<TestRun><Counters executed='2' passed='2' failed='0'/></TestRun>\")"
        ui_code = "from pathlib import Path; import json; Path('.maui-agent/tree.json').write_text('tree'); Path('.maui-agent/ui.json').write_text(json.dumps({'ok':True,'assertions':1,'app':'App','platform':'android','artifacts':['.maui-agent/tree.json']}))"
        self.plan([self.command(), self.command(name="release", kind="release"),
                   self.command(name="unit", code=trx_code, kind="unit", platform="host", format="trx", result=".maui-agent/unit.trx"),
                   self.command(name="ui", code=ui_code, kind="ui", format="devflow", result=".maui-agent/ui.json")])

    def pass_checks(self):
        records = [self.workflow.check(name) for name in ["build", "release", "unit", "ui"]]
        self.assertTrue(all(r["ok"] for r in records))
        self.workflow.observe(0, records[-1]["id"], "Assertion and tree show count 1")

    def test_complete_coverage_and_acceptance_make_review_ready(self):
        self.passing_plan()
        self.pass_checks()
        self.assertTrue(self.workflow.status()["ok"])
        self.assertTrue(self.workflow.review()["ready"])
        self.assertEqual(self.workflow.load()["observations"][0]["reviewer"], "agent")

    def test_acceptance_cannot_use_old_or_failed_evidence(self):
        self.plan([self.command()])
        record = self.workflow.check("build")
        (self.root / "app.txt").write_text("changed")
        with self.assertRaisesRegex(ValueError, "current source"):
            self.workflow.observe(0, record["id"], "old evidence")

    def test_adding_platform_blocks_completion(self):
        self.passing_plan()
        self.pass_checks()
        plan = json.loads((self.root / "maui-agent.json").read_text())
        plan["platforms"].append("ios")
        write_json(self.root / "maui-agent.json", plan)
        status = self.workflow.status()
        self.assertFalse(status["ok"])
        self.assertTrue(any("ui check for ios" in b for b in status["blockers"]))

    def test_no_ci_runs_is_pending(self):
        self.configure_ci()
        with patch("lifecycle.execute", side_effect=self.fake_gh([])):
            result = self.workflow.ci()
        self.assertEqual(result["status"], "pending")
        self.assertFalse(result["ok"])

    def configure_ci(self):
        path = self.root / "maui-agent.json"
        plan = json.loads(path.read_text())
        plan["github"] = {"repository": "example/app", "requiredWorkflows": ["App CI"]}
        write_json(path, plan)

    def fake_gh(self, runs):
        def fake(command, root, timeout=120):
            if command[0] == "git":
                return execute(command, root, timeout)
            return {"exitCode": 0, "output": json.dumps(runs), "timedOut": False}
        return fake

    def test_ci_ignores_green_runs_for_other_commit(self):
        self.configure_ci()
        runs = [{"headSha": "old", "workflowName": "App CI", "status": "completed", "conclusion": "success", "createdAt": "2026", "databaseId": 1}]
        with patch("lifecycle.execute", side_effect=self.fake_gh(runs)):
            self.assertEqual(self.workflow.ci()["status"], "pending")

    def test_ci_uses_latest_run_and_does_not_pass_skipped(self):
        self.configure_ci()
        head = self.git("rev-parse", "HEAD")
        runs = [{"headSha": head, "workflowName": "App CI", "status": "completed", "conclusion": conclusion,
                 "createdAt": str(index), "databaseId": index} for index, conclusion in [(1, "success"), (2, "skipped")]]
        with patch("lifecycle.execute", side_effect=self.fake_gh(runs)):
            result = self.workflow.ci()
        self.assertEqual(result["status"], "failed")
        self.assertEqual(result["runs"][0]["databaseId"], 2)

    def test_publish_rejects_missing_qa_before_any_network_mutation(self):
        with self.assertRaisesRegex(ValueError, "QA gates"):
            self.workflow.github("App", ".maui-agent/body.md")

    def test_lock_prevents_overlapping_mutations(self):
        self.plan([self.command()])
        with self.workflow.locked():
            with self.assertRaisesRegex(ValueError, "Another operation"):
                self.workflow.check("build")

    def test_mcp_routes_status_and_validates_arguments(self):
        requests = [
            {"jsonrpc": "2.0", "id": 1, "method": "initialize"},
            {"jsonrpc": "2.0", "method": "notifications/initialized"},
            {"jsonrpc": "2.0", "id": 2, "method": "tools/list"},
            {"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "maui_lifecycle_status"}},
            {"jsonrpc": "2.0", "id": 4, "method": "tools/call", "params": {"name": "maui_lifecycle_check", "arguments": {"name": 42}}}]
        output = io.StringIO()
        serve(self.workflow, io.StringIO("\n".join(json.dumps(r) for r in requests)), output)
        messages = [json.loads(line) for line in output.getvalue().splitlines()]
        self.assertEqual(len(messages), 4)
        self.assertGreaterEqual(len(messages[1]["result"]["tools"]), 9)
        self.assertFalse(messages[2]["result"]["isError"])
        self.assertIn("error", messages[3])

    def test_real_lifecycle_mcp_process_resumes_repository_state(self):
        client = McpClient([sys.executable, str(SCRIPTS / "mcp_server.py"), "--root", str(self.root)], self.root, 5)
        try:
            tools = client.initialize()["tools"]
            self.assertIn("maui_lifecycle_maintenance", {tool["name"] for tool in tools})
            status = client.data("maui_lifecycle_status", allow_failure=True)
            self.assertEqual(status["cycle"], 1)
            self.assertEqual(status["source"], snapshot(self.root))
            self.assertFalse(status["ok"])
        finally:
            client.close()


class DevFlowTests(unittest.TestCase):
    def report(self, assertions):
        return {"ok": True, "total": 1, "passed": 1, "failed": 0,
                "results": [{"ok": True, "asserts": assertions}]}

    def test_report_only_assertions_do_not_pass(self):
        with self.assertRaisesRegex(ValueError, "no evaluated"):
            assertion_count(self.report([{"skipped": True, "ok": None}]))

    def test_failed_assertion_does_not_pass(self):
        with self.assertRaisesRegex(ValueError, "did not pass"):
            assertion_count(self.report([{"skipped": False, "ok": False}]))

    def test_hard_assertion_counts(self):
        self.assertEqual(assertion_count(self.report([{"skipped": False, "ok": True}])), 1)

    def test_inconsistent_counts_do_not_pass(self):
        report = self.report([{"skipped": False, "ok": True}])
        report["total"] = 2
        with self.assertRaisesRegex(ValueError, "inconsistent"):
            assertion_count(report)

    def test_mcp_transport_handles_notifications_and_process_exit(self):
        code = """import sys,json
for line in sys.stdin:
 r=json.loads(line)
 if 'id' not in r: continue
 print(json.dumps({'jsonrpc':'2.0','method':'notifications/message'}),flush=True)
 print(json.dumps({'jsonrpc':'2.0','id':r['id'],'result':{'tools':[]}}),flush=True)
"""
        with tempfile.TemporaryDirectory() as directory:
            client = McpClient([sys.executable, "-u", "-c", code], directory, 2)
            try:
                self.assertEqual(client.initialize(), {"tools": []})
            finally:
                client.close()
            self.assertIsNotNone(client.process.returncode)

    def test_common_secrets_are_redacted(self):
        text = redact('Authorization: Bearer private password=secret ghp_1234567890')
        self.assertNotIn("private", text)
        self.assertNotIn("secret", text)
        self.assertNotIn("ghp_", text)


if __name__ == "__main__":
    unittest.main()
