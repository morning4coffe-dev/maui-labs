#!/usr/bin/env python3
"""Durable, evidence-gated MAUI agent workflow. Python 3.10+, standard library only."""
from __future__ import annotations

import argparse
import contextlib
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import tempfile
import uuid
import xml.etree.ElementTree as ET

PLAN = "maui-agent.json"
STATE = ".maui-agent"
KINDS = {"build", "unit", "ui", "release"}
CLASSIFICATIONS = {"app-regression", "test-drift", "infrastructure", "inconclusive"}


def now():
    return dt.datetime.now(dt.timezone.utc).isoformat()


def digest(data):
    return hashlib.sha256(data).hexdigest()


def redact(text):
    text = re.sub(r"(?i)(authorization\s*[:=]\s*(?:bearer\s+)?)[^\s\"']+", r"\1[REDACTED]", text)
    text = re.sub(r"(?i)((?:password|token|secret|api[_-]?key)\s*[\"']?\s*[:=]\s*[\"']?)[^\s\"',}]+", r"\1[REDACTED]", text)
    text = re.sub(r"\b(?:gh[pousr]_[A-Za-z0-9_]+|github_pat_[A-Za-z0-9_]+)\b", "[REDACTED]", text)
    for key, value in os.environ.items():
        if len(value) >= 8 and re.search(r"TOKEN|SECRET|PASSWORD|API_KEY", key, re.I):
            text = text.replace(value, "[REDACTED]")
    return text


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    temporary.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")
    os.replace(temporary, path)


def execute(argv, root, timeout=120):
    """No shell, bounded lifetime, kill the process tree on timeout."""
    if not isinstance(argv, list) or not argv or not all(isinstance(a, str) for a in argv):
        raise ValueError("Commands must be nonempty arrays of strings; shell strings are not supported")
    with tempfile.TemporaryFile() as output:
        try:
            child = subprocess.Popen(argv, cwd=root, stdout=output, stderr=subprocess.STDOUT,
                                     start_new_session=os.name != "nt")
        except OSError as exc:
            return {"exitCode": 127, "output": redact(str(exc)), "timedOut": False}
        timed_out = False
        try:
            child.wait(timeout=timeout)
        except (subprocess.TimeoutExpired, KeyboardInterrupt):
            timed_out = True
            if os.name == "nt":
                subprocess.run(["taskkill", "/PID", str(child.pid), "/T", "/F"], capture_output=True)
            else:
                with contextlib.suppress(ProcessLookupError):
                    os.killpg(child.pid, signal.SIGKILL)
            child.wait()
        output.seek(0)
        data = output.read(2 * 1024 * 1024 + 1)
        truncated = len(data) > 2 * 1024 * 1024
        return {"exitCode": 124 if timed_out else child.returncode,
                "output": redact(data[:2 * 1024 * 1024].decode("utf-8", errors="replace")),
                "timedOut": timed_out, "truncated": truncated}


def git(root, *args):
    result = execute(["git", *args], root)
    if result["exitCode"]:
        raise ValueError(result["output"])
    return result["output"].strip()


def inside(root, relative):
    path = (root / relative).resolve()
    if not path.is_relative_to(root):
        raise ValueError(f"Path leaves the application repository: {relative}")
    return path


def snapshot(root):
    result = subprocess.run(["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"],
                            cwd=root, capture_output=True, check=True)
    files = {}
    for name in sorted(set(result.stdout.decode().split("\0")) - {""}):
        if name.startswith(STATE + "/"):
            continue
        path = root / name
        if path.is_symlink():
            data = ("symlink:" + os.readlink(path)).encode()
        elif path.is_file():
            data = path.read_bytes()
        elif not path.exists():
            data = b"<deleted>"
        else:
            raise ValueError(f"Unsupported source entry (including submodules): {name}")
        files[name] = digest(data)
    return digest(json.dumps(files, sort_keys=True).encode())


def plan_for(root):
    plan = json.loads((root / PLAN).read_text(encoding="utf-8"))
    if plan.get("schema") != 1:
        raise ValueError("Unsupported plan schema")
    if not plan.get("acceptance") or not plan.get("platforms"):
        raise ValueError("Plan needs acceptance criteria and target platforms")
    if not isinstance(plan.get("maxAttempts"), int) or not 1 <= plan["maxAttempts"] <= 10:
        raise ValueError("maxAttempts must be between 1 and 10")
    names = set()
    for check in plan.get("checks", []):
        name = check.get("name", "")
        if not re.fullmatch(r"[a-z0-9][a-z0-9-]{0,79}", name) or name in names:
            raise ValueError("Check names must be unique lowercase identifiers")
        names.add(name)
        if check.get("kind") not in KINDS or check.get("platform") not in ["host", *plan["platforms"]]:
            raise ValueError(f"Invalid kind/platform for {name}")
        command = check.get("command")
        if not isinstance(command, list) or not command or not all(isinstance(a, str) for a in command):
            raise ValueError(f"Check {name} requires a command argument array")
        timeout = check.get("timeout", 600)
        if not isinstance(timeout, (int, float)) or not 0 < timeout <= 3600:
            raise ValueError(f"Invalid timeout for {name}")
        if check["kind"] == "unit" and check.get("format") != "trx":
            raise ValueError("Unit checks must consume a nonempty TRX test report")
        if check["kind"] == "ui" and check.get("format") != "devflow":
            raise ValueError("UI checks must consume assertion-bearing DevFlow replay evidence")
        if check.get("format") in {"trx", "devflow"}:
            inside(root, check["result"])
    return plan


class Workflow:
    def __init__(self, root):
        self.root = Path(root).resolve()
        self.directory = self.root / STATE
        self.state_path = self.directory / "state.json"

    @contextlib.contextmanager
    def locked(self):
        self.directory.mkdir(parents=True, exist_ok=True)
        lock = self.directory / "lock"
        try:
            fd = os.open(lock, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        except FileExistsError:
            raise ValueError(f"Another operation holds {lock}; inspect its PID before removing a stale lock")
        try:
            os.write(fd, str(os.getpid()).encode())
            os.close(fd)
            yield
        finally:
            lock.unlink(missing_ok=True)

    def load(self):
        return json.loads(self.state_path.read_text(encoding="utf-8"))

    def save(self, state):
        write_json(self.state_path, state)

    def init(self, name, platforms, acceptance):
        git(self.root, "rev-parse", "--show-toplevel")
        if Path(git(self.root, "rev-parse", "--show-toplevel")).resolve() != self.root:
            raise ValueError("Use the Git repository root")
        if (self.root / PLAN).exists() or self.state_path.exists():
            raise ValueError("Workflow already exists; use status or cycle to resume")
        with self.locked():
            ignore = self.root / ".gitignore"
            text = ignore.read_text() if ignore.exists() else ""
            if "/.maui-agent/" not in text.splitlines():
                ignore.write_text(text.rstrip() + "\n/.maui-agent/\n")
            plan = {"schema": 1, "name": name, "platforms": platforms,
                    "acceptance": acceptance, "maxAttempts": 3, "checks": [],
                    "github": {"repository": "", "base": "main", "requiredWorkflows": []}}
            write_json(self.root / PLAN, plan)
            self.save({"schema": 1, "cycle": 1, "reason": "initial creation", "createdAt": now(),
                       "attempts": [], "classifications": [], "reviews": [], "ci": []})
        return self.status()

    def status(self):
        plan, state, source = plan_for(self.root), self.load(), snapshot(self.root)
        blockers = []
        checks = plan["checks"]
        if not any(c["kind"] == "unit" for c in checks):
            blockers.append("No unit test check configured")
        for platform in plan["platforms"]:
            for kind in ("build", "ui", "release"):
                if not any(c["kind"] == kind and c["platform"] == platform for c in checks):
                    blockers.append(f"No {kind} check for {platform}")
        coverage = []
        for check in checks:
            records = [r for r in state["attempts"] if r["check"] == check["name"]]
            record = records[-1] if records else None
            valid = bool(record and record["ok"] and record["source"] == source and self.verify(record))
            coverage.append({"name": check["name"], "platform": check["platform"], "kind": check["kind"],
                             "status": "passed" if valid else "missing" if not record else
                             "stale" if record["source"] != source else "failed"})
            if not valid:
                blockers.append(f"{check['name']}: {coverage[-1]['status']}")
        for index, criterion in enumerate(plan["acceptance"]):
            observations = [o for o in state.get("observations", []) if o["criterion"] == index
                            and o["source"] == source]
            intact = any(any(r["id"] == o["attempt"] and r["ok"] and r["source"] == source
                             and self.verify(r) for r in state["attempts"]) for o in observations)
            if not intact:
                blockers.append(f"Acceptance {index} has no current evidence: {criterion}")
        return {"ok": not blockers, "cycle": state["cycle"], "source": source,
                "coverage": coverage, "blockers": blockers,
                "next": "review" if not blockers else "configure or repair the listed checks; run check"}

    def observe(self, criterion, attempt, note):
        """Record the agent's acceptance assessment; never impersonate human approval."""
        with self.locked():
            plan, state, source = plan_for(self.root), self.load(), snapshot(self.root)
            if type(criterion) is not int or not 0 <= criterion < len(plan["acceptance"]) or not note.strip():
                raise ValueError("Provide a valid zero-based acceptance index and evidence assessment")
            record = next((r for r in state["attempts"] if r["id"] == attempt), None)
            if not record or not record["ok"] or record["source"] != source or not self.verify(record):
                raise ValueError("Acceptance observations need intact passing evidence for current source")
            observation = {"criterion": criterion, "attempt": attempt, "source": source,
                           "note": redact(note), "reviewer": "agent", "at": now()}
            state.setdefault("observations", []).append(observation)
            self.save(state)
            return observation

    def verify(self, record):
        try:
            return bool(record.get("evidence")) and all(
                digest(inside(self.root, name).read_bytes()) == expected
                for name, expected in record["evidence"].items())
        except (OSError, ValueError):
            return False

    def check(self, name):
        with self.locked():
            plan, state = plan_for(self.root), self.load()
            check = next((c for c in plan["checks"] if c["name"] == name), None)
            if check is None:
                raise ValueError(f"Unknown check: {name}")
            failures = [r for r in state["attempts"] if r["check"] == name and not r["ok"]
                        and r["cycle"] == state["cycle"]]
            if len(failures) >= plan["maxAttempts"]:
                raise ValueError("Repair budget exhausted. Present evidence to the human before starting another cycle")
            if len(failures) >= 2 and failures[-1]["signature"] == failures[-2]["signature"]:
                latest = failures[-1]["id"]
                if not any(c["attempt"] == latest for c in state["classifications"]):
                    raise ValueError("Repeated identical failure. Classify and inspect evidence before retrying")
            source = snapshot(self.root)
            attempt_id = uuid.uuid4().hex
            directory = self.directory / "evidence" / attempt_id
            directory.mkdir(parents=True)
            result_file = inside(self.root, check["result"]) if check.get("result") else None
            if result_file and result_file.exists():
                # A successful command cannot accidentally reuse yesterday's report.
                if not result_file.is_relative_to(self.directory):
                    raise ValueError("Check result paths must be under .maui-agent/ so stale reports can be removed safely")
                result_file.unlink()
            started = now()
            result = execute(check["command"], self.root, check.get("timeout", 600))
            log = directory / "command.log"
            log.write_text(result["output"], encoding="utf-8")
            ok = result["exitCode"] == 0 and not result["timedOut"] and not result.get("truncated")
            reason, artifacts = "", [log]
            try:
                if ok and check.get("format") == "trx":
                    tree = ET.parse(result_file)
                    counters = next((e for e in tree.iter() if e.tag.split("}")[-1] == "Counters"), None)
                    if counters is None or int(counters.get("executed", "0")) <= 0:
                        raise ValueError("TRX contains no executed tests")
                    if int(counters.get("failed", "0")) or int(counters.get("error", "0")):
                        raise ValueError("TRX contains failed tests")
                    if int(counters.get("passed", "0")) != int(counters.get("executed", "0")):
                        raise ValueError("Not all executed tests passed")
                    artifacts.append(result_file)
                elif ok and check.get("format") == "devflow":
                    report = json.loads(result_file.read_text())
                    if report.get("ok") is not True or report.get("assertions", 0) <= 0:
                        raise ValueError("UI report has no successful hard assertions")
                    if report.get("platform", "").lower() != check["platform"].lower():
                        raise ValueError("UI report platform does not match the check")
                    if not report.get("app") or not report.get("artifacts"):
                        raise ValueError("UI report is missing app identity or captured evidence")
                    artifacts.extend(inside(self.root, p) for p in report["artifacts"])
                    artifacts.append(result_file)
                if snapshot(self.root) != source:
                    raise ValueError("Source changed during the check; rerun against a stable revision")
            except (ValueError, OSError, ET.ParseError, TypeError) as exc:
                ok, reason = False, str(exc)
            # Failed runs need their TRX/replay/screenshots as much as successful ones.
            if result_file and result_file.is_file():
                artifacts.append(result_file)
                if check.get("format") == "devflow":
                    try:
                        failure_report = json.loads(result_file.read_text())
                        for relative in failure_report.get("artifacts", []):
                            artifact = inside(self.root, relative)
                            if not artifact.is_relative_to(self.directory):
                                raise ValueError("UI artifacts must stay under .maui-agent/")
                            artifacts.append(artifact)
                    except (ValueError, OSError, TypeError) as exc:
                        ok, reason = False, str(exc)
            evidence = {}
            try:
                for index, artifact in enumerate(dict.fromkeys(artifacts)):
                    data = artifact.read_bytes()
                    # Seal copies so a subsequent check cannot overwrite previous evidence.
                    sealed = directory / f"{index}-{artifact.name}"
                    if artifact != log:
                        sealed.write_bytes(data)
                    else:
                        sealed = log
                    evidence[str(sealed.relative_to(self.root))] = digest(data)
            except OSError as exc:
                ok, reason = False, str(exc)
            record = {"id": attempt_id, "cycle": state["cycle"], "check": name, "source": source,
                      "startedAt": started, "finishedAt": now(), "ok": ok,
                      "exitCode": result["exitCode"], "timedOut": result["timedOut"],
                      "reason": reason, "evidence": evidence,
                      "signature": digest((str(result["exitCode"]) + reason + result["output"][-4000:]).encode())}
            state["attempts"].append(record)
            self.save(state)
            return record

    def classify(self, attempt, category, reason):
        if category not in CLASSIFICATIONS or not reason.strip():
            raise ValueError("Provide an evidence-backed classification and reason")
        with self.locked():
            state = self.load()
            record = next((r for r in state["attempts"] if r["id"] == attempt), None)
            if not record or record["ok"] or not self.verify(record):
                raise ValueError("Classification requires an intact failed attempt")
            entry = {"attempt": attempt, "category": category, "reason": redact(reason), "at": now()}
            state["classifications"].append(entry)
            self.save(state)
            return entry

    def cycle(self, reason):
        if not reason.strip():
            raise ValueError("A new cycle needs the human's new request or authorization as its reason")
        with self.locked():
            state = self.load()
            state["cycle"] += 1
            state["reason"] = redact(reason)
            self.save(state)
            return {"ok": True, "cycle": state["cycle"], "reason": state["reason"]}

    def review(self):
        with self.locked():
            status = self.status()
            plan, state = plan_for(self.root), self.load()
            review = {"at": now(), "source": status["source"], "ready": status["ok"],
                      "acceptance": plan["acceptance"], "coverage": status["coverage"],
                      "observations": state.get("observations", []),
                      "blockers": status["blockers"], "classifications": state["classifications"],
                      "diffStat": git(self.root, "diff", "--stat", "HEAD"),
                      "untracked": git(self.root, "ls-files", "--others", "--exclude-standard")}
            write_json(self.directory / "review.json", review)
            state["reviews"].append(review)
            self.save(state)
            return review

    def github(self, title, body_file):
        """User-authorized push and draft PR; never merges or enables auto-merge."""
        with self.locked():
            status, plan = self.status(), plan_for(self.root)
            if not status["ok"]:
                raise ValueError("QA gates are incomplete: " + "; ".join(status["blockers"]))
            state = self.load()
            if (not state["reviews"] or not state["reviews"][-1]["ready"]
                    or state["reviews"][-1]["source"] != status["source"]):
                raise ValueError("Generate a current review before publishing")
            if git(self.root, "status", "--porcelain"):
                raise ValueError("Commit the reviewed app changes before publishing")
            config = plan["github"]
            repository = config.get("repository", "")
            if not re.fullmatch(r"[\w.-]+/[\w.-]+", repository):
                raise ValueError("Set github.repository to an authorized owner/repository")
            branch = git(self.root, "branch", "--show-current")
            if not branch or branch in {"main", "master", config.get("base", "main")}:
                raise ValueError("Publish from a feature branch")
            body = inside(self.root, body_file)
            if not body.is_file():
                raise ValueError("PR body file does not exist")
            remote = git(self.root, "remote", "get-url", "origin").removesuffix(".git")
            if remote not in {f"https://github.com/{repository}", f"git@github.com:{repository}"}:
                raise ValueError("origin does not match github.repository")
            git(self.root, "push", "--set-upstream", "origin", branch)
            existing = execute(["gh", "pr", "list", "--repo", repository, "--head", branch,
                                "--state", "open", "--json", "url"], self.root)
            if existing["exitCode"]:
                raise ValueError(existing["output"])
            prs = json.loads(existing["output"])
            if prs:
                return {"ok": True, "url": prs[0]["url"], "head": git(self.root, "rev-parse", "HEAD")}
            result = execute(["gh", "pr", "create", "--repo", repository, "--draft", "--base",
                              config.get("base", "main"), "--head", branch, "--title", title,
                              "--body-file", str(body)], self.root)
            if result["exitCode"]:
                raise ValueError(result["output"])
            return {"ok": True, "url": result["output"].strip(), "head": git(self.root, "rev-parse", "HEAD")}

    def maintenance(self):
        config = plan_for(self.root)["github"]
        repository = config.get("repository", "")
        if not re.fullmatch(r"[\w.-]+/[\w.-]+", repository):
            raise ValueError("Configure github.repository before maintenance intake")
        commands = {
            "issues": ["gh", "issue", "list", "--repo", repository, "--state", "open", "--limit", "100",
                       "--search", '"MAUI maintenance:" in:title', "--json", "number,title,url,body,updatedAt"],
            "runs": ["gh", "run", "list", "--repo", repository, "--branch", config.get("base", "main"),
                     "--limit", "20", "--json", "databaseId,headSha,workflowName,status,conclusion,url"]}
        report = {"at": now(), "repository": repository}
        for name, command in commands.items():
            result = execute(command, self.root)
            if result["exitCode"]:
                raise ValueError(result["output"])
            report[name] = json.loads(result["output"])
        return report

    def ci(self):
        """One polling step. The agent can communicate while waiting between steps."""
        with self.locked():
            plan, state = plan_for(self.root), self.load()
            repository = plan["github"]["repository"]
            required = plan["github"].get("requiredWorkflows", [])
            if not re.fullmatch(r"[\w.-]+/[\w.-]+", repository) or not required:
                raise ValueError("Configure github.repository and explicit requiredWorkflows; zero runs cannot pass")
            head = git(self.root, "rev-parse", "HEAD")
            result = execute(["gh", "run", "list", "--repo", repository, "--commit", head,
                              "--limit", "100", "--json",
                              "databaseId,headSha,workflowName,status,conclusion,url,createdAt,attempt"], self.root)
            if result["exitCode"]:
                raise ValueError(result["output"])
            runs = json.loads(result["output"])
            selected, missing = [], []
            for workflow in required:
                candidates = [r for r in runs if r["headSha"] == head and r["workflowName"] == workflow]
                candidates.sort(key=lambda r: (r["createdAt"], r["databaseId"], r.get("attempt", 1)), reverse=True)
                if candidates:
                    selected.append(candidates[0])
                else:
                    missing.append(workflow)
            failed = [r for r in selected if r["status"] == "completed" and r["conclusion"] != "success"]
            pending = missing or any(r["status"] != "completed" for r in selected)
            report = {"ok": not pending and not failed, "status": "failed" if failed else "pending" if pending else "passed",
                      "head": head, "at": now(), "missing": missing, "runs": selected}
            for run in failed:
                log = execute(["gh", "run", "view", str(run["databaseId"]), "--repo", repository,
                               "--log-failed"], self.root)
                path = self.directory / "ci" / f"{run['databaseId']}-{run.get('attempt', 1)}.log"
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(log["output"], encoding="utf-8")
                run["failureLog"] = str(path.relative_to(self.root))
            state["ci"].append(report)
            self.save(state)
            return report


def doctor(root):
    commands = {"python": [sys.executable, "--version"], "dotnet": ["dotnet", "--info"],
                "workloads": ["dotnet", "workload", "list"], "maui": ["maui", "--version"],
                "devflow": ["maui", "devflow", "--help"], "go": ["maui", "go", "--help"],
                "github": ["gh", "auth", "status"]}
    return {"at": now(), "tools": {name: execute(command, root, 60) for name, command in commands.items()}}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default=".", help="Application Git repository root")
    sub = parser.add_subparsers(dest="action", required=True)
    sub.add_parser("doctor")
    init = sub.add_parser("init")
    init.add_argument("--name", required=True)
    init.add_argument("--platform", action="append", required=True)
    init.add_argument("--acceptance", action="append", required=True)
    sub.add_parser("status")
    observe = sub.add_parser("observe")
    observe.add_argument("criterion", type=int)
    observe.add_argument("attempt")
    observe.add_argument("--note", required=True)
    check = sub.add_parser("check")
    check.add_argument("name")
    classify = sub.add_parser("classify")
    classify.add_argument("attempt")
    classify.add_argument("category", choices=sorted(CLASSIFICATIONS))
    classify.add_argument("--reason", required=True)
    cycle = sub.add_parser("cycle")
    cycle.add_argument("--reason", required=True)
    sub.add_parser("review")
    publish = sub.add_parser("github", help="Push current reviewed branch and create/reuse a draft PR")
    publish.add_argument("--title", required=True)
    publish.add_argument("--body-file", required=True)
    sub.add_parser("ci")
    sub.add_parser("maintenance")
    args = parser.parse_args()
    workflow = Workflow(args.root)
    try:
        if args.action == "doctor":
            result = doctor(workflow.root)
        elif args.action == "init":
            result = workflow.init(args.name, args.platform, args.acceptance)
        elif args.action == "check":
            result = workflow.check(args.name)
        elif args.action == "observe":
            result = workflow.observe(args.criterion, args.attempt, args.note)
        elif args.action == "classify":
            result = workflow.classify(args.attempt, args.category, args.reason)
        elif args.action == "cycle":
            result = workflow.cycle(args.reason)
        elif args.action == "github":
            result = workflow.github(args.title, args.body_file)
        else:
            result = getattr(workflow, args.action)()
        print(json.dumps(result, indent=2))
        return 1 if result.get("ok") is False or result.get("ready") is False else 0
    except (ValueError, OSError, KeyError, subprocess.SubprocessError) as exc:
        print(json.dumps({"ok": False, "error": redact(str(exc))}, indent=2))
        return 2


if __name__ == "__main__":
    sys.exit(main())
