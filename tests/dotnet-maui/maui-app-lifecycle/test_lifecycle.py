"""Behavioral acceptance tests: real temporary Git repos and child processes."""
import argparse
import base64
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
from devflow_check import McpClient, assertion_count, validate_layout
from android_check import Driver
from mcp_server import serve
from live_setup import prepare
from xaml_watch import literal_changes, equivalent, refresh
from android_launch import component_for, agent_port, verify_status
from android_ready import check as android_ready


class AndroidReadyTests(unittest.TestCase):
    def fixture(self, root, replacements=None):
        png = base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aK1sAAAAASUVORK5CYII=")
        replies = {("get-state",): b"device\n",
                   ("shell", "getprop", "sys.boot_completed"): b"1\n",
                   ("shell", "am", "get-started-user-state", "0"): b"RUNNING_UNLOCKED\n",
                   ("shell", "pidof", "com.android.systemui"): b"1208\n",
                   ("shell", "cmd", "package", "path", "android"): b"package:/system/framework/framework-res.apk\n",
                   ("shell", "dumpsys", "window", "displays"): b"mCurrentFocus=Window{u0 com.android.launcher3/.Launcher}\n",
                   ("exec-out", "screencap", "-p"): png}
        replies.update(replacements or {})
        seen = []
        def runner(command, **kwargs):
            self.assertEqual(["adb", "-s", "emulator-5558"], command[:3])
            key = tuple(command[3:]);seen.append(key)
            reply = replies[key]  # Any unexpected mutation/installation command fails the test.
            if isinstance(reply, Exception): raise reply
            return subprocess.CompletedProcess(command, 0, stdout=reply, stderr=b"")
        return android_ready(root, "emulator-5558", runner=runner), seen

    def test_readiness_is_scoped_read_only_and_cannot_satisfy_delivery_qa(self):
        with tempfile.TemporaryDirectory() as folder:
            result, _ = self.fixture(folder)
            self.assertTrue(result["ok"])
            self.assertEqual("android-infrastructure", result["mode"])
            self.assertNotIn("assertions", result)
            self.assertTrue((Path(folder) / result["artifacts"][0]).is_file())

    def test_boot_property_alone_cannot_hide_locked_user_fallback_or_anr(self):
        variants = [(("shell", "am", "get-started-user-state", "0"), b"RUNNING_LOCKED\n"),
                    (("shell", "dumpsys", "window", "displays"), b"mCurrentFocus=Window{com.android.settings/.FallbackHome}\n"),
                    (("shell", "dumpsys", "window", "displays"), b"mCurrentFocus=Window{Application Not Responding: com.android.systemui}\n")]
        for key, value in variants:
            with self.subTest(value=value), tempfile.TemporaryDirectory() as folder:
                result, seen = self.fixture(folder, {key: value})
                self.assertFalse(result["ok"])
                self.assertNotIn(("exec-out", "screencap", "-p"), seen)

    def test_package_manager_timeout_stops_before_screen_or_app_operations(self):
        with tempfile.TemporaryDirectory() as folder:
            key = ("shell", "cmd", "package", "path", "android")
            result, seen = self.fixture(folder, {key: subprocess.TimeoutExpired("adb", 15)})
            self.assertFalse(result["ok"])
            self.assertEqual("package-manager", result["stage"])
            self.assertNotIn(("exec-out", "screencap", "-p"), seen)

    def test_truncated_screenshot_does_not_mark_device_ready(self):
        with tempfile.TemporaryDirectory() as folder:
            result, _ = self.fixture(folder, {("exec-out", "screencap", "-p"): b"\x89PNG\r\n\x1a\n"})
            self.assertFalse(result["ok"])
            self.assertEqual([], result["artifacts"])

    def test_screenshot_link_cannot_overwrite_app_source(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder).resolve()
            source = root / "App.csproj";source.write_text("<Project />")
            evidence = root / ".maui-agent/android-ready";evidence.mkdir(parents=True)
            try:
                (evidence / "screen.png").symlink_to(source)
            except OSError as exc:
                if os.name == "nt" and getattr(exc, "winerror", None) == 1314:
                    self.skipTest("Windows host does not permit creation of test symbolic links")
                raise
            with self.assertRaisesRegex(ValueError, "screenshots must stay"):
                self.fixture(root)
            self.assertEqual("<Project />", source.read_text())


class AndroidLaunchTests(unittest.TestCase):
    def test_component_resolution_does_not_select_another_package(self):
        self.assertEqual("com.example/.MainActivity", component_for("com.example", "priority=0\ncom.other/.Other\ncom.example/.MainActivity"))
        with self.assertRaises(ValueError): component_for("com.example", "com.other/.Other")
        with self.assertRaises(ValueError): component_for("com.example", "com.example/.One\ncom.example/.Two")

    def test_port_discovery_supports_broker_assigned_ports_and_rejects_invalid_numbers(self):
        self.assertEqual(10223, agent_port("[Microsoft.Maui.DevFlow] Agent started on port 10223"))
        self.assertEqual(9231, agent_port("HTTP server started on port 9231"))
        self.assertIsNone(agent_port("HTTP server started on port 99999"))
        self.assertIsNone(agent_port("Unrelated server port 9223"))

    def test_live_identity_is_required_in_addition_to_a_log_port(self):
        status = {"running": True, "app": {"name": "TaskApp", "packageId": "com.example", "processId": 42}, "device": {"platform": "Android"}}
        verify_status(status, "TaskApp", "com.example", "42")
        for app, package, pid in [("Other", "com.example", "42"), ("TaskApp", "com.other", "42"), ("TaskApp", "com.example", "43")]:
            with self.subTest(app=app, package=package, pid=pid), self.assertRaises(ValueError):
                verify_status(status, app, package, pid)


class LiteralRefreshTests(unittest.TestCase):
    def test_source_changes_include_only_verified_existing_literals(self):
        old = b'<ContentPage><Button AutomationId="Add" Text="Add task" FontSize="14" /></ContentPage>'
        new = old.replace(b'Add task', b'Add a task').replace(b'FontSize="14"', b'FontSize="16"')
        changes = literal_changes(old, new)
        self.assertEqual(["Text", "FontSize"], [c["property"] for c in changes])
        self.assertEqual("Add task", changes[0]["before"])
        self.assertEqual("Add a task", changes[0]["after"])

    def test_unsafe_or_ambiguous_source_edits_require_rebuild(self):
        old = b'<ContentPage><Button AutomationId="Add" Text="Add task" /></ContentPage>'
        cases = [
            (old, old.replace(b'<Button ', b'<Label ')),
            (old, old.replace(b'Text="Add task"', b'Text="{Binding Title}"')),
            (old, old.replace(b'Text="Add task"', b'Text="Add task" FontSize="16"')),
            (b'<Button AutomationId="Add" Clicked="OnAdd" />', b'<Button AutomationId="Add" Clicked="OnOther" />'),
            (b'<DataTemplate><Button AutomationId="Add" Text="Old" /></DataTemplate>', b'<DataTemplate><Button AutomationId="Add" Text="New" /></DataTemplate>'),
            (b'<Grid><Button AutomationId="Add" Text="Old"/><Button AutomationId="Add" Text="Old"/></Grid>', b'<Grid><Button AutomationId="Add" Text="New"/><Button AutomationId="Add" Text="Old"/></Grid>')]
        for before, after in cases:
            with self.subTest(after=after), self.assertRaises(ValueError):
                literal_changes(before, after)

    def test_normalization_does_not_accept_invalid_numbers(self):
        self.assertTrue(equivalent("FontSize", 14.0, "14"))
        self.assertTrue(equivalent("IsVisible", True, "True"))
        with self.assertRaises(ValueError): equivalent("Opacity", "NaN", "1")

    def test_partial_transport_failure_rolls_back_the_entire_attempted_batch(self):
        class Client:
            values = {"Text": "Old", "FontSize": "14"}
            failed = False
            released = False
            def data(self, tool, **args):
                if tool == "maui_take_control":
                    self.force = args["force"]
                    return {"allowed": True}
                return [{"id": "Add", "automationId": "Add", "type": "Button"}]
            def call(self, tool, **args):
                if tool == "maui_release_control": self.released = True; return {}
                if tool == "maui_get_property": return {"content": [{"type": "text", "text": json.dumps(self.values[args["property"]])}]}
                self.values[args["property"]] = args["value"]
                if args["property"] == "FontSize" and not self.failed:
                    self.failed = True
                    raise ValueError("Transport lost the reply after applying the value")
                return {}
        client = Client()
        changes = [{"automationId": "Add", "type": "Button", "property": name, "before": before, "after": after}
                   for name, before, after in [("Text", "Old", "New"), ("FontSize", "14", "16")]]
        with self.assertRaisesRegex(ValueError, "Transport lost"):
            refresh(client, 9223, changes, 42, lambda: {"app": {"processId": 42}}, lambda: None)
        self.assertEqual({"Text": "Old", "FontSize": "14"}, client.values)
        self.assertFalse(client.force)
        self.assertTrue(client.released)

    def test_another_holders_lease_is_not_released_or_taken_over(self):
        class Client:
            def data(self, tool, **args): return {"allowed": False, "heldByOther": True}
            def call(self, *args, **kwargs): raise AssertionError("Do not mutate another holder's session")
        with self.assertRaisesRegex(ValueError, "no forced takeover"):
            refresh(Client(), 9223, [], 42, lambda: {}, lambda: None)

    def test_stale_source_metadata_is_rejected_before_property_mutation(self):
        class Client:
            def data(self, tool, **args):
                return {"allowed": True} if tool == "maui_take_control" else [{"id": "Add", "automationId": "Add", "type": "Button", "sourceFile": "Other.xaml", "sourceHash": "stale"}]
            def call(self, tool, **args):
                if tool != "maui_release_control": raise AssertionError("Stale source must not be read as a valid mutation target")
        change = {"automationId": "Add", "type": "Button", "property": "Text", "before": "Old", "after": "New"}
        with self.assertRaisesRegex(ValueError, "source metadata"):
            refresh(Client(), 9223, [change], 42, lambda: {"app": {"processId": 42}}, lambda: None, "MainPage.xaml", "valid-hash")


class LiveSetupTests(unittest.TestCase):
    def test_windows_jdk_layout_and_invalid_jdk(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder).resolve()
            (root / "App.csproj").write_text("<Project />")
            jdk = root / "jdk"
            (jdk / "bin").mkdir(parents=True)
            with self.assertRaisesRegex(ValueError, "JDK must contain"):
                prepare(root, "App.csproj", "net10.0-android", "emulator-5556", str(jdk))
            self.assertFalse((root / ".maui-agent/live-config").exists())
            (jdk / "bin/java.exe").write_bytes(b"fixture")
            prepare(root, "App.csproj", "net10.0-android", "emulator-5556", str(jdk))
            settings = json.loads((root / ".maui-agent/live-config/settings.json").read_text())
            self.assertEqual(str(jdk), settings["maui.configuration.javaSdkPreferredPath"])

    def test_generates_resolved_local_paths_without_modifying_editor_configuration(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder).resolve()
            (root / "App.csproj").write_text("<Project />")
            result = prepare(root, "App.csproj", "net10.0-android", "emulator-5556")
            launch = json.loads((root / ".maui-agent/live-config/launch.json").read_text())
            self.assertEqual(str(root / "App.csproj"), launch["configurations"][0]["project"])
            self.assertEqual("emulator-5556", launch["configurations"][0]["device"])
            settings = json.loads((root / ".maui-agent/live-config/settings.json").read_text())
            self.assertTrue(settings["dotnet.debug.hotReloadXAML"])
            self.assertFalse((root / ".vscode").exists())
            self.assertFalse(result["installed"])

    def test_install_preserves_existing_jsonc_and_does_not_partially_write(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder).resolve()
            (root / "App.csproj").write_text("<Project />")
            (root / ".vscode").mkdir()
            settings = root / ".vscode/settings.json"
            settings.write_text('{ // user settings\n "custom": true\n}')
            original = settings.read_bytes()
            with self.assertRaisesRegex(ValueError, "no files were replaced"):
                prepare(root, "App.csproj", "net10.0-android", "emulator-5556", install=True)
            self.assertEqual(original, settings.read_bytes())
            self.assertFalse((root / ".vscode/launch.json").exists())

    def test_rejects_missing_project_before_writing(self):
        with tempfile.TemporaryDirectory() as folder:
            with self.assertRaisesRegex(ValueError, "existing app"):
                prepare(folder, "missing.csproj", "net10.0-android", "emulator-5556", install=True)
            self.assertFalse((Path(folder) / ".vscode").exists())


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

    def test_repeated_runs_require_consecutive_current_passes(self):
        self.plan([self.command(runs=2)])
        self.workflow.check("build")
        self.assertNotEqual(self.workflow.status()["coverage"][0]["status"], "passed")
        self.workflow.check("build")
        self.assertEqual(self.workflow.status()["coverage"][0]["status"], "passed")

    def test_dependency_must_pass_before_check_executes(self):
        self.plan([self.command(), self.command(name="dependent", dependsOn=["build"])])
        with self.assertRaisesRegex(ValueError, "dependency build"):
            self.workflow.check("dependent")
        self.assertEqual(len(self.workflow.load()["attempts"]), 0)
        self.workflow.check("build")
        result = self.workflow.check("dependent")
        self.assertTrue(result["ok"])
        self.assertEqual(result["check"], "dependent")
        self.assertEqual(self.workflow.load()["attempts"][-1]["check"], "dependent")

    def test_dependency_cycles_are_rejected(self):
        self.plan([self.command(dependsOn=["second"]), self.command(name="second", dependsOn=["build"])])
        with self.assertRaisesRegex(ValueError, "cycle"):
            self.workflow.status()

    def test_later_pass_does_not_hide_an_unclassified_same_source_failure(self):
        self.plan([self.command(code="from pathlib import Path; raise SystemExit(1 if Path('.maui-agent/fail').exists() else 0)")])
        p = self.root / "maui-agent.json"
        plan = json.loads(p.read_text()); plan["quality"] = {"requireClassifiedFailures": True}; write_json(p, plan)
        flag = self.root / ".maui-agent/fail"; flag.write_text("fail")
        failure = self.workflow.check("build")
        flag.unlink()
        self.workflow.check("build")
        self.assertTrue(any("Unresolved current-source failure" in b for b in self.workflow.status()["blockers"]))
        self.workflow.classify(failure["id"], "infrastructure", "Verified the isolated runner was not ready; recovered without changing app expectations")
        self.assertFalse(any("Unresolved current-source failure" in b for b in self.workflow.status()["blockers"]))

    def test_replacing_tested_binary_invalidates_a_pass(self):
        path = self.root / ".maui-agent/app.apk"
        path.write_bytes(b"binary one")
        self.plan([self.command(artifact=".maui-agent/app.apk")])
        self.workflow.check("build")
        self.assertEqual(self.workflow.status()["coverage"][0]["status"], "passed")
        path.write_bytes(b"binary two")
        self.assertNotEqual(self.workflow.status()["coverage"][0]["status"], "passed")

    def test_quality_contract_does_not_count_devflow_as_independent_release_qa(self):
        self.passing_plan()
        p = self.root / "maui-agent.json"
        plan = json.loads(p.read_text())
        plan["quality"] = {"independentUi": True, "releaseUi": True, "scenarios": ["persistence"]}
        write_json(p, plan)
        self.pass_checks()
        status = self.workflow.status()
        self.assertFalse(status["ok"])
        self.assertTrue(any("Release runtime" in b for b in status["blockers"]))
        self.assertTrue(any("persistence" in b for b in status["blockers"]))

    def test_independent_report_checks_counts_source_configuration_and_binary(self):
        binary = self.root / ".maui-agent/app.apk"
        binary.write_bytes(b"test binary")
        for index, change in enumerate([{}, {"source": "old-source"}, {"assertions": 2},
                                        {"configuration": "Debug"}, {"binary": {"path": ".maui-agent/app.apk", "sha256": "wrong"}}]):
            with self.subTest(change=change):
                code = f"""import sys,json
from pathlib import Path
sys.path.insert(0,{str(SCRIPTS)!r})
from lifecycle import snapshot,digest
Path('.maui-agent/tree.xml').write_text('<hierarchy/>')
r={{'ok':True,'app':'App','package':'example.app','platform':'android','configuration':'Release',
'driver':'appium-uiautomator2','source':snapshot(Path.cwd()),'assertions':1,
'trace':[{{'action':'assert','ok':True}}],
'binary':{{'path':'.maui-agent/app.apk','sha256':digest(Path('.maui-agent/app.apk').read_bytes())}},
'scenarios':[{{'tags':['navigation'],'assertions':1}}],'artifacts':['.maui-agent/tree.xml']}}
r.update({change!r})
Path('.maui-agent/native.json').write_text(json.dumps(r))
"""
                name = f"native-{index}"
                self.plan([self.command(name=name, code=code, kind="ui", format="appium", configuration="Release",
                                        appPackage="example.app", artifact=".maui-agent/app.apk", result=".maui-agent/native.json")])
                self.assertEqual(self.workflow.check(name)["ok"], not bool(change))

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

    def test_acceptance_evidence_must_remain_intact_after_a_later_pass(self):
        self.passing_plan()
        self.pass_checks()
        old = self.workflow.load()["attempts"][-1]
        self.workflow.check("ui")
        (self.root / next(iter(old["evidence"]))).write_text("tampered")
        status = self.workflow.status()
        self.assertEqual(next(c for c in status["coverage"] if c["name"] == "ui")["status"], "passed")
        self.assertTrue(any("Acceptance 0" in b for b in status["blockers"]))

    def test_incomplete_review_cannot_be_used_for_publication(self):
        self.passing_plan()
        self.assertFalse(self.workflow.review()["ready"])
        self.pass_checks()
        with self.assertRaisesRegex(ValueError, "current review"):
            self.workflow.github("App", ".maui-agent/body.md")

    def test_no_ci_runs_is_pending(self):
        self.configure_ci()
        with patch("lifecycle.execute", side_effect=self.fake_gh([])):
            result = self.workflow.ci()
        self.assertEqual(result["status"], "pending")
        self.assertFalse(result["ok"])

    def test_local_pass_does_not_claim_complete_validation_with_stale_or_missing_ci(self):
        self.passing_plan()
        self.configure_ci()
        self.pass_checks()
        state = self.workflow.load()
        state["ci"].append({"head": self.git("rev-parse", "HEAD"), "status": "passed"})
        self.workflow.save(state)
        status = self.workflow.status()
        self.assertTrue(status["localQaReady"])
        self.assertFalse(status["validationComplete"])
        self.assertEqual(status["ci"]["status"], "stale")
        self.git("add", "."); self.git("commit", "-m", "verified app")
        self.assertEqual(self.workflow.status()["ci"]["status"], "pending")
        state["ci"].append({"head": self.git("rev-parse", "HEAD"), "status": "passed"})
        self.workflow.save(state)
        self.assertTrue(self.workflow.status()["validationComplete"])

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

    def test_ci_collects_runner_startup_annotations_when_logs_are_missing(self):
        self.configure_ci()
        head = self.git("rev-parse", "HEAD")
        run = {"headSha": head, "workflowName": "App CI", "status": "completed", "conclusion": "failure",
               "createdAt": "2026", "databaseId": 123}
        def fake(command, root, timeout=120):
            if command[0] == "git":
                return execute(command, root, timeout)
            if "--log-failed" in command:
                return {"exitCode": 1, "output": "log not found", "timedOut": False}
            if command[1:3] == ["run", "list"]:
                data = [run]
            elif command[1:3] == ["run", "view"]:
                data = {"jobs": [{"databaseId": 456, "name": "android", "conclusion": "failure", "steps": []}]}
            else:
                data = [{"annotation_level": "failure", "message": "Runner allocation was denied by account policy"}]
            return {"exitCode": 0, "output": json.dumps(data), "timedOut": False}
        with patch("lifecycle.execute", side_effect=fake):
            result = self.workflow.ci()
        self.assertEqual(result["status"], "failed")
        self.assertIn("account policy", result["runs"][0]["startupAnnotations"][0])
        self.assertTrue((self.root / result["runs"][0]["startupDiagnostics"]).exists())

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
            self.assertIn("maui_lifecycle_android_ready", {tool["name"] for tool in tools})
            with self.assertRaisesRegex(ValueError, "Path leaves"):
                client.data("maui_lifecycle_android_ready", device="not-contacted", output="../escape.json", allow_failure=True)
            status = client.data("maui_lifecycle_status", allow_failure=True)
            self.assertEqual(status["cycle"], 1)
            self.assertEqual(status["source"], snapshot(self.root))
            self.assertFalse(status["ok"])
        finally:
            client.close()

    def test_android_readiness_mcp_keeps_fixed_root_and_nonpassing_health_visible(self):
        requests = [
            {"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {
                "name": "maui_lifecycle_android_ready", "arguments": {"device": "emulator-5558", "timeout": 30}}},
            {"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {
                "name": "maui_lifecycle_android_ready", "arguments": {"device": "emulator-5558", "root": "/other"}}}]
        output = io.StringIO()
        with patch("mcp_server.android_readiness", return_value={"ok": False, "mode": "android-infrastructure", "stage": "window"}) as ready:
            serve(self.workflow, io.StringIO("\n".join(json.dumps(r) for r in requests)), output)
            ready.assert_called_once_with(self.root, device="emulator-5558", timeout=30)
        messages = [json.loads(line) for line in output.getvalue().splitlines()]
        self.assertFalse(messages[0]["result"]["isError"])
        self.assertFalse(json.loads(messages[0]["result"]["content"][0]["text"])["ok"])
        self.assertIn("error", messages[1])


class DevFlowTests(unittest.TestCase):
    def test_native_session_start_can_outlive_ordinary_command_timeout(self):
        import http.server
        import threading
        import time
        class Handler(http.server.BaseHTTPRequestHandler):
            def do_POST(self):
                self.rfile.read(int(self.headers["Content-Length"]))
                time.sleep(0.05)
                body = json.dumps({"value": {"sessionId": "native-test", "capabilities": {"platformName": "Android"}}}).encode()
                self.send_response(200); self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
            def log_message(self, *args):
                pass
        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
        try:
            driver = Driver(f"http://127.0.0.1:{server.server_port}", timeout=0.01)
            self.assertEqual(driver.start("emulator", "app.test", ".MainActivity")["platformName"], "Android")
            self.assertEqual(driver.timeout, 0.01)
        finally:
            server.shutdown(); server.server_close(); thread.join()
    def test_serious_layout_violations_fail_while_suppressions_and_observations_do_not(self):
        with self.assertRaisesRegex(ValueError, "Layout diagnostics"):
            validate_layout({"findings": [{"outcome": "violation", "severity": "serious", "suppressed": False}]})
        validate_layout({"findings": [{"outcome": "violation", "severity": "critical", "suppressed": True},
                                     {"outcome": "observation", "severity": "serious"}]})
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
