#!/usr/bin/env python3
"""Exercise production Editor wait methods and relay recovery without an Editor."""
import argparse
import hashlib
import json
import re
import shutil
import subprocess
import tempfile
from pathlib import Path
from xml.sax.saxutils import escape


def source_text(repo, path, source_ref):
    if source_ref:
        return subprocess.check_output(
            ["git", "show", f"{source_ref}:{path.as_posix()}"], cwd=repo
        ).decode("utf-8-sig")
    return (repo / path).read_text(encoding="utf-8-sig")


def method(source, name):
    """Extract a complete unchanged C# method, ignoring string/comment braces."""
    matches = list(re.finditer(r"^\s*(?:(?:private|internal|public)\s+)?static\s+(?:async\s+)?[^\n;{}=]+\b" + re.escape(name)
                              + r"\s*\([^;{}]*\)\s*\{", source, re.MULTILINE))
    if len(matches) != 1:
        raise RuntimeError(f"Expected exactly one production method {name}, found {len(matches)}")
    start = matches[0].start()
    pos = source.index("{", matches[0].start())
    depth, mode = 0, "code"
    while pos < len(source):
        char, pair = source[pos], source[pos:pos + 2]
        if mode == "line":
            if char == "\n":
                mode = "code"
        elif mode == "block":
            if pair == "*/":
                mode = "code"
                pos += 1
        elif mode in ("string", "char"):
            if char == "\\":
                pos += 1
            elif char == ('"' if mode == "string" else "'"):
                mode = "code"
        elif mode == "verbatim":
            if pair == '""':
                pos += 1
            elif char == '"':
                mode = "code"
        elif pair in ("//", "/*"):
            mode = "line" if pair == "//" else "block"
            pos += 1
        elif pair == '@"':
            mode = "verbatim"
            pos += 1
        elif char in ('"', "'"):
            mode = "string" if char == '"' else "char"
        elif char == "{":
            depth += 1
        elif char == "}":
            depth -= 1
            if depth == 0:
                return source[start:pos + 1]
        pos += 1
    raise RuntimeError(f"Unbalanced production method {name}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--newtonsoft-dll", type=Path, required=True)
    parser.add_argument("--source-ref", help="Compile sources at a Git ref, e.g. HEAD, to prove the regression was previously failing.")
    parser.add_argument("--filter", help="Run only cases containing this text.")
    args = parser.parse_args()
    suite = Path(__file__).resolve().parent
    repo = suite.parents[2]
    dependency = args.newtonsoft_dll.resolve(strict=True)
    args.report = args.report.resolve()
    args.report.parent.mkdir(parents=True, exist_ok=True)
    manage_path = Path("com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor/Tools/ManageEditor.cs")
    editor = source_text(repo, manage_path, args.source_ref)
    extracted = [method(editor, name) for name in (
        "WaitForReady", "WaitForReadyAfterReload", "IsEditorReady", "BuildReadinessData",
        "WaitForEditorUpdateAsync", "Clamp", "ReloadCheckpoint")]
    # Any additional readiness result helper is production code too; only the
    # heavyweight diagnostics outside the wait are supplied by the fixture.
    for helper in ("WaitSucceeded", "IsWaitResultSuccess", "IsWaitResponseSuccess", "TryGetWaitFailure", "IsReadinessWaitSuccessful"):
        if re.search(r"\b" + helper + r"\s*\(", editor):
            extracted.append(method(editor, helper))
    provenance = {"sourceRef": args.source_ref or "working-tree", "manageEditorSha256": hashlib.sha256(editor.encode()).hexdigest()}
    legacy_path = Path("com.cidonix.unibridge/Legacy~/Unity2017/Assets/UniBridgeLegacy/Editor/UniBridgeLegacyTools.cs")
    legacy = source_text(repo, legacy_path, args.source_ref)
    legacy_host_path = legacy_path.with_name("UniBridgeLegacyHost.cs")
    legacy_host = source_text(repo, legacy_host_path, args.source_ref)
    provenance["legacyToolsSha256"] = hashlib.sha256(legacy.encode()).hexdigest()
    with tempfile.TemporaryDirectory(prefix="unibridge-editor-wait-build-") as temp:
        work = Path(temp).resolve()
        for source in (repo / "UniBridge.Relay").glob("*.cs"):
            relative = source.relative_to(repo)
            content = source_text(repo, relative, args.source_ref)
            if source.name == "Program.cs":
                provenance["relayProgramSha256"] = hashlib.sha256(content.encode()).hexdigest()
                anchor = 'dirs.Add(Path.Combine(home, ".unibridge", "mcp", "connections"));'
                if content.count(anchor) != 1:
                    raise RuntimeError("Relay discovery isolation anchor changed; inspect the harness.")
                content = content.replace(anchor, "// Test: discover only the owned fake Unity endpoint.")
            (work / source.name).write_text(content, encoding="utf-8")
        for name in ("Regression.cs", "Fixtures.cs"):
            shutil.copyfile(suite / name, work / name)
        (work / "EditorWaitProduction.cs").write_text(
            "using Newtonsoft.Json.Linq;\nusing UnityEditor;\nusing UnityEditorInternal;\nusing UnityEngine.SceneManagement;\nusing UnityEditor.SceneManagement;\n"
            "using Cidonix.UniBridge.MCP.Editor.Helpers;\n"
            "namespace Cidonix.UniBridge.Relay { static partial class EditorWaitProduction {\n"
            + "\n".join(extracted) + "\n} }", encoding="utf-8")
        (work / "PlayWaitProduction.cs").write_text(
            "using Cidonix.UniBridge.MCP.Editor.Helpers;\nusing UnityEditor;\n"
            "namespace Cidonix.UniBridge.Relay { static partial class PlayWaitProduction {\n"
            + method(editor, "WaitForPlayModeState") + "\n} }", encoding="utf-8")
        (work / "LegacyWaitProduction.cs").write_text(
            "using UnityEditor;\nusing UnityEditor.SceneManagement;\nusing UnityEngine;\n"
            "using UnitySceneManager = UnityEngine.SceneManagement.SceneManager;\n"
            "namespace Cidonix.UniBridge.Relay { static partial class LegacyWaitProduction {\n"
            + "\n".join(method(legacy, name) for name in ("ManageEditor", "BuildEditorState", "Action", "EqualsAction", "Result"))
            + "\n} static class UniBridgeLegacyValue {\n"
            + "\n".join(method(legacy_host, name) for name in ("Get", "GetBool", "GetString"))
            + "\n} }", encoding="utf-8")
        response = Path("com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor/Helpers/Response.cs")
        (work / "Response.cs").write_text(source_text(repo, response, args.source_ref), encoding="utf-8")
        (work / "source_provenance.json").write_text(json.dumps(provenance), encoding="utf-8")
        (work / "Regression.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
            '<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>'
            '<Nullable>annotations</Nullable><NoWarn>CS0649</NoWarn><StartupObject>Cidonix.UniBridge.Relay.EditorWaitRegression</StartupObject>'
            '</PropertyGroup><ItemGroup><Reference Include="Newtonsoft.Json"><HintPath>'
            + escape(str(dependency)) + '</HintPath></Reference></ItemGroup></Project>', encoding="utf-8")
        command = ["dotnet", "run", "--project", str(work / "Regression.csproj"), "--configuration", "Release", "--",
                   str(args.report), str(work / "source_provenance.json")]
        if args.filter:
            command.append(args.filter)
        return subprocess.run(command, check=False).returncode


if __name__ == "__main__":
    raise SystemExit(main())
