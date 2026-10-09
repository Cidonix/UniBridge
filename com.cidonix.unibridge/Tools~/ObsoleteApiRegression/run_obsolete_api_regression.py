#!/usr/bin/env python3
"""Compile the unchanged semantic obsolete API collector with bundled Roslyn."""
import argparse
import ctypes
import hashlib
import json
import os
import re
import shutil
import subprocess
import tempfile
from pathlib import Path
from xml.sax.saxutils import escape


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def compile_unity_api_surface(repo, work, unity, dependencies, source, logs, provenance, source_ref):
    """Compile whole production wrapper/collector/path resolver without running Unity."""
    api = work / "unity-api"
    api.mkdir()
    (api / "ObsoleteApiAnalyzer.cs").write_bytes(source)
    for filename in ("ObsoleteApiHints.cs", "ProjectPathResolver.cs"):
        path = Path("com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor/Helpers") / filename
        content = (subprocess.check_output(["git", "show", f"{source_ref}:{path.as_posix()}"], cwd=repo)
                   if source_ref else (repo / path).read_bytes())
        (api / filename).write_bytes(content)
        provenance["sha256"][path.as_posix()] = hashlib.sha256(content).hexdigest()
    sdk_lines = subprocess.check_output(["dotnet", "--list-sdks"], text=True).splitlines()
    version, directory = re.match(r"([^ ]+) \[([^]]+)\]", sdk_lines[-1]).groups()
    compiler = Path(directory) / version / "Roslyn/bincore/csc.dll"
    refs = sorted((unity / "UnityReferenceAssemblies/unity-4.8-api").glob("*.dll"))
    refs += sorted((unity / "UnityReferenceAssemblies/unity-4.8-api/Facades").glob("*.dll"))
    if not refs:
        raise FileNotFoundError("Unity .NET 4.8 reference assemblies were not found in " + str(unity))
    refs += [unity / "Managed/UnityEngine/UnityEngine.CoreModule.dll",
             unity / "Managed/UnityEngine/UnityEditor.CoreModule.dll"] + dependencies
    arguments = ["-nologo", "-nostdlib+", "-target:library", "-langversion:9.0", f'-out:"{api / "WrapperApi.dll"}"']
    arguments += [f'-reference:"{path}"' for path in refs]
    arguments += [f'"{path}"' for path in api.glob("*.cs")]
    response = api / "compile.rsp"
    response.write_text("\n".join(arguments), encoding="utf-8")
    built = subprocess.run(["dotnet", str(compiler), "-noconfig", "@" + str(response)],
                           capture_output=True, text=True, encoding="utf-8", errors="replace", check=False, timeout=180)
    logs.append({"stage":"compile-unity-api-surface", "exitCode":built.returncode, "stdout":built.stdout, "stderr":built.stderr})
    return {"name":"compile-unity-reference-api-surface", "passed":built.returncode == 0,
            "detail":"Whole production collector, Unity context wrapper and path resolver compiled against installed Unity .NET 4.8/core module references. This is compilation-only; no Editor runtime behavior was tested."}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--source-ref", help="Compile the production collector from this Git ref.")
    parser.add_argument("--unity-editor-data", type=Path, help="Optionally compile whole production wrappers against this installed Editor/Data reference surface, without launching Unity.")
    args = parser.parse_args()
    if os.name != "nt" or not ctypes.windll.shell32.IsUserAnAdmin():
        parser.error("Run this regression from an Administrator Windows terminal.")

    suite = Path(__file__).resolve().parent
    repo = suite.parents[2]
    report = args.report.resolve()
    report.parent.mkdir(parents=True, exist_ok=True)
    relative = Path("com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor/Helpers/ObsoleteApiAnalyzer.cs")
    dependencies = [repo / "com.cidonix.unibridge/Plugins" / path for path in (
        "CodeAnalysis/Microsoft.CodeAnalysis.dll",
        "CodeAnalysis/Microsoft.CodeAnalysis.CSharp.dll",
        "Shared/System.Collections.Immutable.dll",
        "Shared/System.Reflection.Metadata.dll")]
    logs = []
    provenance = {"sourceRef": args.source_ref or "working-tree", "sourcePath": relative.as_posix(), "sha256":{}}
    try:
        source = (subprocess.check_output(["git", "show", f"{args.source_ref}:{relative.as_posix()}"], cwd=repo)
                  if args.source_ref else (repo / relative).read_bytes())
        provenance["sourceSha256"] = hashlib.sha256(source).hexdigest()
        for dependency in dependencies:
            if not dependency.is_file():
                raise FileNotFoundError(dependency)
            provenance["sha256"][dependency.relative_to(repo).as_posix()] = sha256(dependency)
        for filename in ("Regression.cs", "FixtureCases.cs"):
            provenance["sha256"][filename] = sha256(suite / filename)

        with tempfile.TemporaryDirectory(prefix="unibridge-obsolete-api-") as temporary:
            work = Path(temporary)
            unity_check = None
            if args.unity_editor_data:
                unity = args.unity_editor_data.resolve(strict=True)
                provenance["unityEditorData"] = str(unity)
                unity_check = compile_unity_api_surface(repo, work, unity, dependencies, source, logs, provenance, args.source_ref)
            (work / "ObsoleteApiAnalyzer.cs").write_bytes(source)
            for filename in ("Regression.cs", "FixtureCases.cs"):
                shutil.copyfile(suite / filename, work / filename)
            (work / "NuGet.config").write_text("<configuration><packageSources><clear /></packageSources></configuration>", encoding="utf-8")
            refs = "".join('<Reference Include="' + escape(dependency.stem) + '"><HintPath>' +
                           escape(str(dependency)) + '</HintPath><Private>true</Private></Reference>' for dependency in dependencies)
            (work / "Regression.csproj").write_text(
                '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
                '<TargetFramework>net10.0</TargetFramework><ImplicitUsings>disable</ImplicitUsings>'
                '<Nullable>disable</Nullable><NoWarn>CA1416</NoWarn><StartupObject>ObsoleteApiRegression</StartupObject>'
                '<EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup>'
                '<Compile Include="ObsoleteApiAnalyzer.cs" /><Compile Include="Regression.cs" /><Compile Include="FixtureCases.cs" />'
                + refs + '</ItemGroup></Project>', encoding="utf-8")
            (work / "provenance.json").write_text(json.dumps(provenance), encoding="utf-8")
            command = ["dotnet", "build", str(work / "Regression.csproj"), "--configuration", "Release", "--nologo", "--verbosity", "quiet"]
            built = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace", check=False, timeout=180)
            logs.append({"stage":"compile", "exitCode":built.returncode, "stdout":built.stdout, "stderr":built.stderr})
            if built.returncode:
                raise RuntimeError("Production-linked regression failed to compile; inspect retained compile diagnostics.")
            fresh_report = work / "runtime-report.json"
            completed = subprocess.run(["dotnet", str(work / "bin/Release/net10.0/Regression.dll"),
                                        str(fresh_report), str(work / "provenance.json")], capture_output=True, text=True,
                                       encoding="utf-8", errors="replace", check=False, timeout=180)
            logs.append({"stage":"execute", "exitCode":completed.returncode, "stdout":completed.stdout, "stderr":completed.stderr})
            if not fresh_report.is_file():
                raise RuntimeError("Regression process produced no report.")
            result = json.loads(fresh_report.read_text(encoding="utf-8"))
            if unity_check:
                result["checks"].append(unity_check)
                result["passed"] += int(unity_check["passed"])
                result["failed"] += int(not unity_check["passed"])
            result["logs"] = logs
            report.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
            print(json.dumps({"report":str(report), "passed":result["passed"], "failed":result["failed"]}))
            return 0 if completed.returncode == 0 and result["failed"] == 0 else 1
    except Exception as error:
        result = {"provenance":provenance, "passed":0, "failed":1,
                  "checks":[{"name":"harness-preparation-or-execution", "passed":False, "detail":repr(error)}], "logs":logs}
        report.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
        print(json.dumps({"report":str(report), "passed":0, "failed":1, "error":str(error)}))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
