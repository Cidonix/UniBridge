#!/usr/bin/env python3
"""Compile and exercise the actual UniBridge native Windows pipe implementation."""
import argparse
import base64
import hashlib
import importlib.util
import json
import os
import re
import shutil
import subprocess
import tempfile
from pathlib import Path


def source_text(repo, path, source_ref):
    if source_ref:
        return subprocess.check_output(["git", "show", f"{source_ref}:{path.as_posix()}"], cwd=repo).decode("utf-8-sig")
    return (repo / path).read_text(encoding="utf-8-sig")


def disable_account_lookup(source):
    """Simulate an unmappable account name while keeping real token/SID APIs."""
    declaration = re.compile(r"\[DllImport\([\s\S]*?\)\]\s*(?P<signature>static\s+extern\s+\w+\s+(?P<name>\w+)\([\s\S]*?\));")
    replaced = []

    def replace(match):
        if match.group("name") != "LookupAccountName":
            return match.group(0)
        replaced.append("LookupAccountName")
        signature = re.sub(r"\bextern\s+", "", match.group("signature"))
        return ('[DllImport("kernel32.dll", EntryPoint = "SetLastError", ExactSpelling = true, SetLastError = true)]\n'
                '        static extern void SetLookupFailureLastError(uint error);\n        '
                + signature + '\n        {\n            cbSid = 0; cchReferencedDomainName = 0; peUse = 0;\n'
                '            SetLookupFailureLastError(1332); // ERROR_NONE_MAPPED\n            return false;\n        }')

    return declaration.sub(replace, source), len(replaced)


def run(command, checks, runtime, logs, cwd=None, env=None):
    completed = subprocess.run(command, cwd=cwd, env=env, capture_output=True, text=True, encoding="utf-8", errors="replace", check=False, timeout=180)
    logs.append({"runtime": runtime, "command": command, "exitCode": completed.returncode,
                 "stdout": completed.stdout, "stderr": completed.stderr})
    found = 0
    for line in completed.stdout.splitlines():
        parts = line.lstrip("\ufeff").split("\t")
        if len(parts) != 4 or parts[0] != "CHECK":
            continue
        found += 1
        checks.append({"runtime": runtime, "name": parts[2], "passed": parts[1] == "PASS",
                       "detail": base64.b64decode(parts[3]).decode("utf-8")})
    if not found or (completed.returncode != 0 and all(check["passed"] for check in checks if check["runtime"] == runtime)):
        checks.append({"runtime": runtime, "name": "harness-execution", "passed": False,
                       "detail": f"Process exit {completed.returncode}; inspect retained stdout/stderr."})
    return completed


def find_unity_editor(explicit):
    if explicit:
        root = explicit.resolve(strict=True)
        return root / "Editor" / "Data" if (root / "Editor" / "Data").is_dir() else root
    candidates = list((Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Unity" / "Hub" / "Editor").glob("*/Editor/Data"))
    candidates = [path for path in candidates if (path / "MonoBleedingEdge/bin/mono.exe").is_file()]
    return max(candidates, key=lambda path: tuple(map(int, re.findall(r"\d+", path.parents[1].name)))) if candidates else None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--source-ref", help="Compile previous committed sources for a native failing baseline.")
    parser.add_argument("--unity-editor", type=Path, help="Unity installation root or its Editor/Data directory.")
    parser.add_argument("--skip-unity-mono", action="store_true", help="Run standalone .NET checks without the optional installed Unity Mono checks.")
    parser.add_argument("--inspect-pipe", help="Inspect one explicitly supplied live Editor pipe through a transient client, without sending protocol commands.")
    parser.add_argument("--iterations", type=int, default=2000)
    args = parser.parse_args()
    if os.name != "nt":
        parser.error("Native Windows SID/ACL/pipe tests require Windows.")
    if not 100 <= args.iterations <= 100000:
        parser.error("--iterations must be between 100 and 100000.")
    suite = Path(__file__).resolve().parent
    repo = suite.parents[2]
    args.report = args.report.resolve()
    args.report.parent.mkdir(parents=True, exist_ok=True)
    connection = Path("com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor/Connection")
    source_names = ("NamedPipeListener.cs", "NamedPipeTransport.cs", "IConnectionListener.cs", "IConnectionTransport.cs")
    sources = {name: source_text(repo, connection / name, args.source_ref) for name in source_names}
    checks, logs = [], []
    oracle = None
    provenance = {"sourceRef": args.source_ref or "working-tree", "sha256": {
        name: hashlib.sha256(content.encode()).hexdigest() for name, content in sources.items()}}
    unmappable_source, lookup_replacements = disable_account_lookup(sources["NamedPipeListener.cs"])
    provenance["unmappableAccountLookupDeclarations"] = lookup_replacements
    unity = find_unity_editor(args.unity_editor) if not args.skip_unity_mono and not args.inspect_pipe else None
    try:
        with tempfile.TemporaryDirectory(prefix="unibridge-windows-sid-") as temp:
            work = Path(temp).resolve()
            variants = (("native-windows", "UNITY_EDITOR_WIN"),) if args.inspect_pipe else (
                ("native-windows", "UNITY_EDITOR_WIN"), ("unmappable-account-name", "UNITY_EDITOR_WIN"), ("non-windows", ""))
            for variant, constants in variants:
                project = work / variant
                project.mkdir()
                for name, content in sources.items():
                    if name == "NamedPipeListener.cs" and variant == "unmappable-account-name":
                        content = unmappable_source
                    (project / name).write_text(content, encoding="utf-8")
                for name in ("Regression.cs", "Stubs.cs"):
                    shutil.copyfile(suite / name, project / name)
                (project / "Regression.csproj").write_text(
                    '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
                    '<TargetFramework>net10.0</TargetFramework><ImplicitUsings>disable</ImplicitUsings>'
                    '<Nullable>disable</Nullable><StartupObject>WindowsSidRegression</StartupObject>'
                    '<NoWarn>CA1416;CS0414;CS0649</NoWarn><DefineConstants>' + constants + '</DefineConstants>'
                    '</PropertyGroup></Project>', encoding="utf-8")
                invocation = ["dotnet", "run", "--project", str(project / "Regression.csproj"), "--configuration", "Release", "--", str(args.iterations)]
                if args.inspect_pipe:
                    invocation.append(args.inspect_pipe)
                completed = run(invocation,
                                checks, "net10-" + variant, logs)
                if variant == "native-windows":
                    oracle_lines = [line.split("\t", 1)[1] for line in completed.stdout.splitlines() if line.startswith("ORACLE\t")]
                    if len(oracle_lines) != 1:
                        raise RuntimeError("Independent .NET process-user SID oracle was not emitted exactly once.")
                    oracle = base64.b64decode(oracle_lines[0]).decode("utf-8")
            generator_path = suite / "native_failure_generator.py"
            if not args.source_ref and not args.inspect_pipe and generator_path.is_file():
                spec = importlib.util.spec_from_file_location("unibridge_sid_failure_generator", generator_path)
                generator = importlib.util.module_from_spec(spec)
                spec.loader.exec_module(generator)
                project = work / "native-failures"
                project.mkdir()
                for name, content in sources.items():
                    if name == "NamedPipeListener.cs":
                        content = generator.generate(content)
                    (project / name).write_text(content, encoding="utf-8")
                for name in ("FailureRegression.cs", "Stubs.cs"):
                    shutil.copyfile(suite / name, project / name)
                (project / "Regression.csproj").write_text(
                    '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
                    '<TargetFramework>net10.0</TargetFramework><ImplicitUsings>disable</ImplicitUsings>'
                    '<Nullable>disable</Nullable><StartupObject>FailureRegression</StartupObject>'
                    '<NoWarn>CA1416;CS0649</NoWarn><DefineConstants>UNITY_EDITOR_WIN;NATIVE_FAILURES</DefineConstants>'
                    '</PropertyGroup></Project>', encoding="utf-8")
                run(["dotnet", "run", "--project", str(project / "Regression.csproj"), "--configuration", "Release"],
                    checks, "net10-native-failure-seams", logs)
            if unity:
                sdk_lines = subprocess.check_output(["dotnet", "--list-sdks"], text=True).splitlines()
                version, directory = re.match(r"([^ ]+) \[([^]]+)\]", sdk_lines[-1]).groups()
                compiler = Path(directory) / version / "Roslyn/bincore/csc.dll"
                references = sorted((unity / "UnityReferenceAssemblies/unity-4.8-api").glob("*.dll"))
                references += sorted((unity / "UnityReferenceAssemblies/unity-4.8-api/Facades").glob("*.dll"))
                if not references:
                    raise RuntimeError("Unity Mono .NET 4.8 reference assemblies were not found.")
                for variant, constants in variants:
                    project = work / variant
                    executable = project / "UnityMonoRegression.exe"
                    response = project / "mono-compile.rsp"
                    compile_args = ["-nologo", "-nostdlib+", "-target:exe", "-langversion:latest", "-main:WindowsSidRegression",
                                    f'-out:"{executable}"']
                    if constants:
                        compile_args.append("-define:" + constants)
                    compile_args += [f'-reference:"{path}"' for path in references]
                    compile_args += [f'"{path}"' for path in project.glob("*.cs")]
                    response.write_text("\n".join(compile_args), encoding="utf-8")
                    completed = subprocess.run(["dotnet", str(compiler), "-noconfig", "@" + str(response)], capture_output=True, text=True, check=False, timeout=120)
                    logs.append({"runtime": "unity-mono-compile-" + variant, "exitCode": completed.returncode,
                                 "stdout": completed.stdout, "stderr": completed.stderr})
                    checks.append({"runtime": "unity-mono-" + variant, "name": "compile-unity-mono-reference-surface",
                                   "passed": completed.returncode == 0, "detail": "Complete production listener/transport/interfaces compiled against Unity Mono .NET 4.8 reference assemblies."})
                    if completed.returncode == 0:
                        mono_env = dict(os.environ)
                        mono_env["UNIBRIDGE_SID_TEST_ORACLE"] = oracle
                        mono_env["MONO_PATH"] = str(unity / "MonoBleedingEdge/lib/mono/unityjit-win32")
                        run([str(unity / "MonoBleedingEdge/bin/mono.exe"), str(executable), str(args.iterations)],
                            checks, "unity-mono-" + variant, logs, env=mono_env)
    except Exception as error:
        checks.append({"runtime": "runner", "name": "harness-preparation", "passed": False, "detail": repr(error)})
    result = {"provenance": provenance, "unityEditorData": str(unity) if unity else None, "inspectPipe": args.inspect_pipe,
              "iterations": args.iterations, "passed": sum(check["passed"] for check in checks),
              "failed": sum(not check["passed"] for check in checks), "checks": checks, "logs": logs}
    args.report.write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding="utf-8")
    print(json.dumps({"report": str(args.report), "passed": result["passed"], "failed": result["failed"]}, ensure_ascii=False))
    return 0 if checks and result["failed"] == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
