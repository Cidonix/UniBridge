"""Exact production-linked recorded frame and callstack qualification; no Unity launch or authored project writes."""
import argparse
import ctypes
import hashlib
import json
import os
import re
import shutil
import subprocess
import uuid
from pathlib import Path
from xml.sax.saxutils import escape


def unity_core_reference(explicit):
    if explicit:
        path = explicit.resolve()
    else:
        editors = Path(os.environ.get('ProgramFiles', 'C:/Program Files')) / 'Unity/Hub/Editor'
        installed = list(editors.glob('*/Editor/Data/Managed/UnityEngine/UnityEngine.CoreModule.dll'))
        if not installed:
            raise SystemExit('Provide --unity-core pointing to the installed UnityEngine.CoreModule.dll')
        path = max(installed, key=lambda item: tuple(int(value) for value in re.findall(r'\d+', item.parents[4].name)[:3])).resolve()
    if not path.is_file():
        raise SystemExit('Unity SDK CoreModule reference missing: ' + str(path))
    return path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--repo', type=Path, default=Path(__file__).resolve().parents[3])
    parser.add_argument('--overlay', type=Path)
    parser.add_argument('--newtonsoft', type=Path, required=True)
    parser.add_argument('--unity-core', type=Path, help='Real Unity CoreModule SDK reference; derives the newest installed Hub Editor if omitted')
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    if os.name != 'nt' or not ctypes.windll.shell32.IsUserAnAdmin():
        raise SystemExit('An Administrator Windows shell is required')
    unity_core = unity_core_reference(args.unity_core)
    unity_symbols = 'UNITY_6000_0_OR_NEWER' if b'GetEntityId' in unity_core.read_bytes() else ''
    report = args.report.resolve()
    if report.exists():
        raise SystemExit('Report exists; choose a fresh report to preserve evidence')
    report.parent.mkdir(parents=True, exist_ok=True)
    work = report.parent / ('frame-regression-build-' + uuid.uuid4().hex)
    work.mkdir()
    editor = Path('com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor')
    runtime = Path('com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Runtime/Serialization')
    sources = {}
    for relative in ('Helpers/RecordedProfilerFrameAnalysis.cs', 'Helpers/RecordedProfilerFrameReader.cs', 'Helpers/UnityRecordedProfilerSource.cs',
                     'Tools/RuntimeProfiler.RecordedFrames.cs', 'Tools/Parameters/RuntimeProfilerParams.cs',
                     'ToolRegistry/RuntimeProfilerActionOutputRules.cs', 'Helpers/McpJson.cs', 'Helpers/Response.cs', 'ToolRegistry/McpAttributes.cs'):
        source = (args.overlay / editor / relative) if args.overlay and (args.overlay / editor / relative).exists() else args.repo / editor / relative
        shutil.copyfile(source, work / source.name)
        sources[str(editor / relative)] = hashlib.sha256(source.read_bytes()).hexdigest()
    for name in ('UnityValueSerialization.cs', 'UnityTypeConverters.cs'):
        relative = runtime / name
        source = args.overlay / relative if args.overlay and (args.overlay / relative).exists() else args.repo / relative
        shutil.copyfile(source, work / name)
        sources[str(relative)] = hashlib.sha256(source.read_bytes()).hexdigest()
    fixture_hashes = {}
    for name in ('NativeApiFixtures.cs', 'ToolFixtures.cs', 'Regression.cs'):
        source = Path(__file__).resolve().parent / name
        shutil.copyfile(source, work / source.name)
        fixture_hashes[source.name] = hashlib.sha256(source.read_bytes()).hexdigest()
    (work / 'Counters.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><DefineConstants>' + unity_symbols + '</DefineConstants><NoWarn>CS0649;CS0436;CS0618</NoWarn></PropertyGroup><ItemGroup><Reference Include="Newtonsoft.Json"><HintPath>' + escape(str(args.newtonsoft.resolve())) + '</HintPath></Reference><Reference Include="UnityEngine.CoreModule"><HintPath>' + escape(str(unity_core)) + '</HintPath></Reference></ItemGroup></Project>', encoding='utf-8')
    (work / 'NuGet.Config').write_text('<configuration><packageSources><clear /></packageSources></configuration>', encoding='utf-8')
    run = subprocess.run(['dotnet', 'run', '--project', str(work / 'Counters.csproj'), '--configuration', 'Release', '--', str(report), str(work / 'owned-export')], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, check=False, timeout=120)
    report.with_suffix('.build.log').write_text(run.stdout, encoding='utf-8')
    result = json.loads(report.read_text()) if report.exists() else {'passed': False, 'failed': 1, 'failure': 'Qualification executable did not produce a report; inspect build log', 'exitCode': run.returncode}
    result.update({'sourceHashes': sources, 'fixtureHashes': fixture_hashes, 'administrator': True,
                   'newtonsoftSha256': hashlib.sha256(args.newtonsoft.read_bytes()).hexdigest(),
                   'unityCore': {'path': str(unity_core), 'sha256': hashlib.sha256(unity_core.read_bytes()).hexdigest(), 'compileSymbols': unity_symbols},
                   'qualificationScope': 'Exact recorded frame reader/adapter/analysis/action/output rules, McpJson and whole math helper/converters; real SDK math value types in NET10. Profiler native methods, Application/Mathf host state and scheduler/export seams are declared fixtures; Unity object resolution and native Editor execution are not qualified here.'})
    report.write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(run.stdout)
    return run.returncode


if __name__ == '__main__':
    raise SystemExit(main())
