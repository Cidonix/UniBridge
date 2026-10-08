#!/usr/bin/env python3
"""Run command delivery regressions against current relay source in isolation."""
import argparse
import json
import shutil
import subprocess
import tempfile
from pathlib import Path
from xml.sax.saxutils import escape


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--filter", help="Run only case names containing this text.")
    parser.add_argument("--newtonsoft-dll", type=Path, help="Unity's Newtonsoft.Json.dll; enables production command journal tests.")
    parser.add_argument("--include-timeout", action="store_true", help="Include the real production 120-second response timeout gate.")
    args = parser.parse_args()
    suite = Path(__file__).resolve().parent
    repo = suite.parents[2]
    relay = repo / "UniBridge.Relay"
    default_discovery = 'dirs.Add(Path.Combine(home, ".unibridge", "mcp", "connections"));'
    args.report = args.report.resolve()
    args.report.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="unibridge-command-replay-") as temp:
        work = Path(temp).resolve()
        # Keep real relay code, except that discovery cannot enumerate or clean
        # the user's real live Editor connection directory during a fault test.
        sources = list(relay.glob("*.cs"))
        if not sources:
            raise RuntimeError(f"No relay C# sources found under {relay}")
        for source in sources:
            content = source.read_text(encoding="utf-8-sig")
            if source.name == "Program.cs":
                if content.count(default_discovery) != 1:
                    raise RuntimeError("Relay discovery isolation anchor changed; inspect the harness before running.")
                content = content.replace(default_discovery, "// Regression: only the isolated discovery directory is enumerated.")
            (work / source.name).write_text(content, encoding="utf-8")
        shutil.copyfile(suite / "Regression.cs", work / "Regression.cs")
        journal_settings = ""
        journal_reference = ""
        if args.newtonsoft_dll:
            dependency = args.newtonsoft_dll.resolve(strict=True)
            journal = repo / "com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor/Helpers/CommandRecoveryJournal.cs"
            shutil.copyfile(journal, work / "CommandRecoveryJournal.cs")
            shutil.copyfile(suite / "JournalRegression.cs", work / "JournalRegression.cs")
            journal_settings = '<DefineConstants>JOURNAL_TESTS</DefineConstants>'
            journal_reference = ('<ItemGroup><Reference Include="Newtonsoft.Json"><HintPath>' + escape(str(dependency)) +
                                 '</HintPath></Reference></ItemGroup>')
        (work / "Regression.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
            '<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>'
            '<ImplicitUsings>enable</ImplicitUsings><Nullable>annotations</Nullable>'
            '<StartupObject>Cidonix.UniBridge.Relay.CommandReplayRegression</StartupObject>'
            + journal_settings + '</PropertyGroup>' + journal_reference + '</Project>', encoding="utf-8")
        command = ["dotnet", "run", "--project", str(work / "Regression.csproj"),
                   "--configuration", "Release", "--", str(args.report)]
        if args.filter:
            command.append(args.filter)
        if args.include_timeout:
            command.append("--include-timeout")
        result = subprocess.run(command, check=False)
        return result.returncode


if __name__ == "__main__":
    raise SystemExit(main())
