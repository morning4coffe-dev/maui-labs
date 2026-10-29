#!/usr/bin/env python3
"""Prepare local VS Code MAUI Debug configuration without replacing user files."""
import argparse
import json
from pathlib import Path
from lifecycle import inside, write_json


def prepare(root, project, framework, device, jdk=None, install=False):
    root = Path(root).resolve()
    project = inside(root, project)
    if not project.is_file() or project.suffix != ".csproj":
        raise ValueError("Select an existing app .csproj under the workspace")
    if not framework or not device:
        raise ValueError("Select the app's actual target framework and test device")
    settings = {"dotnet.debug.hotReloadXAML": True,
                "maui.configuration.useLaunchJsonConfigurations": True}
    if jdk:
        java_home = Path(jdk).expanduser().resolve()
        if not any((java_home / "bin" / name).is_file() for name in ("java", "java.exe")):
            raise ValueError("JDK must contain bin/java or bin/java.exe")
        settings["maui.configuration.javaSdkPreferredPath"] = str(java_home)
    files = {
        "launch.json": {"version": "0.2.0", "configurations": [{
            "name": project.stem + " live Inspector + XAML Hot Reload", "type": "maui", "request": "launch",
            "project": str(project), "targetFramework": framework, "configuration": "Debug",
            "device": device, "preLaunchTask": "maui: Build"}]},
        "tasks.json": {"version": "2.0.0", "tasks": [{
            "label": "maui: Build", "type": "maui", "project": str(project),
            "targetFramework": framework, "configuration": "Debug", "group": "build",
            "problemMatcher": ["$maui-problem-matcher"]}]},
        "settings.json": settings}
    destination = root / (".vscode" if install else ".maui-agent/live-config")
    if install and any((destination / name).exists() for name in files):
        raise ValueError("Existing VS Code configuration must be reviewed and merged; no files were replaced")
    for name, data in files.items():
        write_json(destination / name, data)
    return {"ok": True, "directory": str(destination), "installed": install,
            "next": "Launch the selected Debug configuration; verify runtime values and the same PID after saving XAML"}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", default=".")
    parser.add_argument("--project", required=True)
    parser.add_argument("--framework", required=True)
    parser.add_argument("--device", required=True)
    parser.add_argument("--jdk")
    parser.add_argument("--install", action="store_true", help="Install only when none of the destination files exists")
    args = parser.parse_args()
    try:
        print(json.dumps(prepare(args.root, args.project, args.framework, args.device, args.jdk, args.install)))
        return 0
    except (ValueError, OSError) as exc:
        print(json.dumps({"ok": False, "error": str(exc)}))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
