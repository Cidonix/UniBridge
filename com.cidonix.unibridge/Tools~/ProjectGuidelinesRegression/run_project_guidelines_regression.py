#!/usr/bin/env python3
"""Source-linked reader and closed-contract regression; never launches Unity."""
import argparse
import ctypes
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import uuid
from xml.sax.saxutils import quoteattr

HERE = Path(__file__).resolve().parent
EDITOR = "Modules/Cidonix.UniBridge.MCP.Editor/"
COMMON = ["Helpers/ProjectGuidelinesModels.cs", "Helpers/ProjectGuidelinesReader.cs",
          "Tools/Parameters/ProjectGuidelinesParams.cs", "Helpers/Response.cs", "Helpers/McpJson.cs"]
CONTRACT = ["ToolRegistry/ProjectGuidelinesActionOutputRules.cs", "ToolRegistry/ActionOutputSchema.cs",
            "ToolRegistry/ActionOutputValidation.cs"]
SETTINGS = ["Settings/ProjectGuidelinesSettings.cs"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package-root", type=Path, default=HERE.parent.parent)
    parser.add_argument("--source-overlay", type=Path, help="Explicit candidate package root; overlaid sources are identified in the report.")
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--suite", choices=("all", "reader", "contracts", "settings"), default="all")
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    if os.name == "nt" and not ctypes.windll.shell32.IsUserAnAdmin():
        raise RuntimeError("Administrator token required before launching .NET regression processes.")
    report_path = args.report.resolve()
    if report_path.exists():
        raise FileExistsError("Reports are immutable; choose a fresh report name.")
    package = args.package_root.resolve(strict=True)
    overlay = args.source_overlay.resolve(strict=True) if args.source_overlay else None
    dotnet = shutil.which(args.dotnet)
    if not dotnet:
        raise FileNotFoundError(".NET 10 SDK required; pass --dotnet <installed dotnet executable>.")
    report_path.parent.mkdir(parents=True, exist_ok=True)
    work = report_path.parent / ("ProjectGuidelinesRegression_" + uuid.uuid4().hex)
    work.mkdir()
    report = {"passed": False, "suite": args.suite, "startedUtc": datetime.now(timezone.utc).isoformat(),
              "packageRoot": str(package), "sourceOverlay": str(overlay) if overlay else None,
              "administrator": os.name == "nt", "nativeUnityExecution": False,
              "workDirectory": str(work), "sources": [], "suites": []}
    fingerprinted = set()
    try:
        for suite in (("reader", "contracts", "settings") if args.suite == "all" else (args.suite,)):
            directory = work / suite
            directory.mkdir()
            sources = []
            for relative in COMMON + (CONTRACT if suite == "contracts" else SETTINGS if suite == "settings" else []):
                relative = EDITOR + relative
                source = overlay / relative if overlay and (overlay / relative).is_file() else package / relative
                source = source.resolve(strict=True)
                sources.append(source)
                if source not in fingerprinted:
                    report["sources"].append({"relativePath": relative, "actualPath": str(source),
                                              "sha256": hashlib.sha256(source.read_bytes()).hexdigest()})
                    fingerprinted.add(source)
            template_names = [{"reader": "ReaderRegression.cs.txt", "contracts": "ContractRegression.cs.txt", "settings": "SettingsRegression.cs.txt"}[suite], "SerializationSeam.cs.txt"]
            if suite == "contracts":
                template_names.append("ContractSeams.cs.txt")
            if suite == "settings":
                template_names.append("SettingsSeams.cs.txt")
            for name in template_names:
                destination = directory / ("Program.cs" if name.endswith("Regression.cs.txt") else name[:-4])
                destination.write_bytes((HERE / "Templates" / name).read_bytes())
                sources.append(destination)
            project = directory / "Regression.csproj"
            project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
                               '<TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems>'
                               '<Nullable>disable</Nullable></PropertyGroup><ItemGroup>'
                               '<PackageReference Include="Newtonsoft.Json" Version="13.0.4" />' +
                               "".join("<Compile Include=" + quoteattr(str(source)) + " />" for source in sources) +
                               '</ItemGroup></Project>', encoding="utf-8")
            child_report = directory / "result.json"
            completed = subprocess.run([dotnet, "run", "--project", str(project), "-c", "Release", "--", "--report", str(child_report)],
                                       text=True, encoding="utf-8", errors="replace", capture_output=True, timeout=240,
                                       creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
            (directory / "dotnet.log").write_text(completed.stdout + completed.stderr, encoding="utf-8")
            result = json.loads(child_report.read_text(encoding="utf-8-sig")) if child_report.is_file() else {"passed": False, "total": 0, "cases": []}
            result.update({"suite": suite, "exitCode": completed.returncode, "report": str(child_report), "log": str(directory / "dotnet.log")})
            report["suites"].append(result)
            if completed.returncode != 0 or not result.get("passed"):
                raise RuntimeError("Regression failed in " + suite + "; inspect retained " + str(directory / "dotnet.log"))
        # Evidence corresponds to the exact linked bytes, including concurrent edits.
        for source in report["sources"]:
            if hashlib.sha256(Path(source["actualPath"]).read_bytes()).hexdigest() != source["sha256"]:
                raise RuntimeError("Linked source changed during regression: " + source["relativePath"])
        report["total"] = sum(row["total"] for row in report["suites"])
        report["passed"] = True
    except Exception as error:
        report["error"] = type(error).__name__ + ": " + str(error)[:1600]
    with report_path.open("x", encoding="utf-8") as stream:
        json.dump(report, stream, indent=2, ensure_ascii=False)
    print(json.dumps({"passed": report["passed"], "total": report.get("total", 0), "report": str(report_path), "error": report.get("error")}))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
