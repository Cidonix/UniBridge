#!/usr/bin/env python3
"""Compile exact relay and opt-in live Unity fault proxy qualification."""
import argparse
import json
import shutil
import subprocess
import tempfile
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--real-discovery", type=Path)
    parser.add_argument("--project-root", type=Path)
    parser.add_argument("--report", type=Path)
    parser.add_argument("--run-id")
    parser.add_argument("--build-only", action="store_true", help="Compile without making any live Editor calls.")
    args = parser.parse_args()
    if not args.build_only and not all((args.real_discovery, args.project_root, args.report, args.run_id)):
        parser.error("Live run requires --real-discovery, --project-root, --report and --run-id.")
    suite = Path(__file__).resolve().parent
    repo = suite.parents[2]
    relay = repo / "UniBridge.Relay"
    anchor = 'dirs.Add(Path.Combine(home, ".unibridge", "mcp", "connections"));'
    with tempfile.TemporaryDirectory(prefix="unibridge-live-delivery-build-") as temp:
        work = Path(temp).resolve()
        for source in relay.glob("*.cs"):
            content = source.read_text(encoding="utf-8-sig")
            if source.name == "Program.cs":
                if content.count(anchor) != 1:
                    raise RuntimeError("Relay discovery isolation anchor changed; inspect live harness before use.")
                content = content.replace(anchor, "// Qualification: only task-owned proxy discovery is enumerated.")
            (work / source.name).write_text(content, encoding="utf-8")
        shutil.copyfile(suite / "LiveRegression.cs", work / "LiveRegression.cs")
        project = work / "LiveRegression.csproj"
        project.write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
            '<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>'
            '<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>'
            '<StartupObject>Cidonix.UniBridge.Relay.LiveCommandReplayRegression</StartupObject>'
            '</PropertyGroup></Project>', encoding="utf-8")
        build = subprocess.run(["dotnet", "build", str(project), "--configuration", "Release", "--nologo"], check=False, capture_output=True, text=True)
        print(json.dumps({"buildExitCode": build.returncode, "buildOutput": (build.stdout + build.stderr)[-6000:]}, ensure_ascii=False), flush=True)
        if build.returncode or args.build_only:
            return build.returncode
        report = args.report.resolve()
        report.parent.mkdir(parents=True, exist_ok=True)
        return subprocess.run([
            "dotnet", str(work / "bin" / "Release" / "net10.0" / "LiveRegression.dll"),
            str(args.real_discovery.resolve()), str(args.project_root.resolve()), str(report), args.run_id
        ], check=False, timeout=360).returncode


if __name__ == "__main__":
    raise SystemExit(main())
