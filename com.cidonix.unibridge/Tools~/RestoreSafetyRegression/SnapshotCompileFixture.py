"""Compile the entire live snapshot fixture against the actual project API references.

This launches only the C# compiler, with the elevated parent token. No Unity
Editor or Unity API code is executed, and the target project is read-only.
"""
import argparse
import ctypes
import hashlib
import json
import os
import re
import subprocess
import tempfile
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    if os.name != "nt" or not ctypes.windll.shell32.IsUserAnAdmin():
        parser.error("Run from an Administrator terminal.")
    project = args.project.resolve(strict=True)
    suite = Path(__file__).resolve().parent
    repo = suite.parents[2]
    candidates = list((project / "Library/Bee/artifacts").glob("*/Cidonix.UniBridge.MCP.Editor.rsp"))
    if not candidates:
        raise FileNotFoundError("The project's generated UniBridge Editor compiler response file was not found.")
    original_rsp = max(candidates, key=lambda path: path.stat().st_mtime)
    lines = original_rsp.read_text(encoding="utf-8-sig").splitlines()
    arguments = [line for line in lines if line.startswith(("-r:", "-define:", "-nostdlib", "-langversion:"))]
    editor_dll = project / "Library/ScriptAssemblies/Cidonix.UniBridge.MCP.Editor.dll"
    if not editor_dll.is_file():
        raise FileNotFoundError(editor_dll)
    fixture = suite / "SnapshotLiveFixture.cs"
    adapter = repo / "com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor/Helpers/UnityApiAdapter.cs"
    sdk_lines = subprocess.check_output(["dotnet", "--list-sdks"], text=True).splitlines()
    version, directory = re.match(r"([^ ]+) \[([^]]+)\]", sdk_lines[-1]).groups()
    compiler = Path(directory) / version / "Roslyn/bincore/csc.dll"
    args.report = args.report.resolve()
    args.report.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="unibridge-snapshot-fixture-api-") as temporary:
        work = Path(temporary)
        arguments += ["-nologo", "-target:library", f'-out:"{work / "SnapshotLiveFixtureApi.dll"}"',
                      f'-r:"{editor_dll}"', f'"{fixture}"', f'"{adapter}"']
        response = work / "compile.rsp"
        response.write_text("\n".join(arguments), encoding="utf-8")
        completed = subprocess.run(["dotnet", str(compiler), "-noconfig", "@" + str(response)], cwd=project,
                                   stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                                   encoding="utf-8", errors="replace", check=False, timeout=180)
        result = {"passed": completed.returncode == 0, "exitCode": completed.returncode,
                  "scope": "entire unchanged live fixture and actual UnityApiAdapter; compilation only",
                  "projectReadOnly": True, "administrator": True,
                  "unityProjectVersion": (project / "ProjectSettings/ProjectVersion.txt").read_text(),
                  "sourceSha256": {str(path.relative_to(repo)): hashlib.sha256(path.read_bytes()).hexdigest()
                                   for path in (fixture, adapter)},
                  "projectCompilerRsp": str(original_rsp),
                  "projectCompilerRspSha256": hashlib.sha256(original_rsp.read_bytes()).hexdigest(),
                  "existingUniBridgeAssembly": str(editor_dll),
                  "existingUniBridgeAssemblySha256": hashlib.sha256(editor_dll.read_bytes()).hexdigest(),
                  "compiler": str(compiler), "log": completed.stdout}
        args.report.write_text(json.dumps(result, indent=2), encoding="utf-8")
        print(json.dumps({"passed": result["passed"], "exitCode": completed.returncode, "report": str(args.report)}))
        if completed.returncode:
            print(completed.stdout)
        return completed.returncode


if __name__ == "__main__":
    raise SystemExit(main())
