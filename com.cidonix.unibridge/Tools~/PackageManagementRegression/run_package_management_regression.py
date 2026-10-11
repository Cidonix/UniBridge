"""Production-linked package request/result and real-filesystem sample ownership tests."""
import argparse
import ctypes
import hashlib
import json
import os
import subprocess
import sys
import uuid
from pathlib import Path
from xml.sax.saxutils import escape


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--repo', type=Path, required=True)
    parser.add_argument('--candidate', type=Path)
    parser.add_argument('--newtonsoft', type=Path, required=True)
    parser.add_argument('--unity-core', type=Path, default=Path('C:/Program Files/Unity/Hub/Editor/6000.6.5f1/Editor/Data/Managed/UnityEngine/UnityEngine.CoreModule.dll'))
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding='utf-8')
    if os.name != 'nt' or not ctypes.windll.shell32.IsUserAnAdmin():
        raise SystemExit('Administrator token is required for every launch')
    report = args.report.resolve()
    if report.exists(): raise SystemExit('Report exists; choose a fresh suffix to retain evidence')
    report.parent.mkdir(parents=True, exist_ok=True)
    work = report.parent / ('package-regression-build-' + uuid.uuid4().hex)
    work.mkdir()
    provenance = {'administrator': True, 'nativeEditorExecution': False, 'wholeProductionFiles': {}, 'fixtureFiles': {}}
    for relative in (
        'Modules/Cidonix.UniBridge.MCP.Editor/Helpers/PackageManagementModels.cs',
        'Modules/Cidonix.UniBridge.MCP.Editor/Helpers/PackageManagementService.cs',
        'Modules/Cidonix.UniBridge.MCP.Editor/Helpers/PackageSampleImportGuard.cs',
        'Modules/Cidonix.UniBridge.MCP.Editor/Tools/Parameters/ManagePackagesParams.cs',
        'Modules/Cidonix.UniBridge.MCP.Editor/Helpers/Response.cs',
        'Modules/Cidonix.UniBridge.MCP.Editor/Helpers/McpJson.cs',
        'Modules/Cidonix.UniBridge.MCP.Editor/ToolRegistry/ActionOutputSchema.cs',
        'Modules/Cidonix.UniBridge.MCP.Editor/ToolRegistry/CoreActionOutputContracts.cs',
        'Modules/Cidonix.UniBridge.MCP.Editor/ToolRegistry/OutputContractProvider.cs',
        'Modules/Cidonix.UniBridge.MCP.Editor/ToolRegistry/SerializerOutputSchema.cs',
        'Modules/Cidonix.UniBridge.MCP.Editor/ToolRegistry/ActionSelectorRules.cs',
        'Modules/Cidonix.UniBridge.MCP.Editor/ToolRegistry/ActionOutputValidation.cs',
        'Modules/Cidonix.UniBridge.MCP.Editor/ToolRegistry/PackageActionOutputRules.cs',
        'Modules/Cidonix.UniBridge.MCP.Editor/ToolRegistry/ToolExecutionScheduler.cs',
        'Modules/Cidonix.UniBridge.MCP.Editor/Tools/ManagePackages.cs',
        'Modules/Cidonix.UniBridge.MCP.Runtime/Serialization/UnityValueSerialization.cs',
    ):
        path = args.repo / 'com.cidonix.unibridge' / relative
        if args.candidate and (args.candidate / 'com.cidonix.unibridge' / relative).is_file(): path = args.candidate / 'com.cidonix.unibridge' / relative
        raw = path.read_bytes(); (work / path.name).write_bytes(raw)
        provenance['wholeProductionFiles'][str(path.resolve())] = hashlib.sha256(raw).hexdigest()
    here = Path(__file__).resolve().parent
    for name in ('Regression.cs', 'MetadataFixtures.cs', 'ProviderDependencyFixtures.cs'):
        raw = (here / name).read_bytes(); (work / name).write_bytes(raw)
        provenance['fixtureFiles'][name] = hashlib.sha256(raw).hexdigest()
    provenance['newtonsoft'] = {'path': str(args.newtonsoft.resolve()), 'sha256': hashlib.sha256(args.newtonsoft.read_bytes()).hexdigest()}
    (work / 'provenance.json').write_text(json.dumps(provenance, indent=2), encoding='utf-8')
    project = work / 'PackageRegression.csproj'
    project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><StartupObject>PackageManagementRegression.Regression</StartupObject></PropertyGroup><ItemGroup><Reference Include="Newtonsoft.Json"><HintPath>' + escape(str(args.newtonsoft.resolve())) + '</HintPath></Reference><Reference Include="UnityEngine.CoreModule"><HintPath>' + escape(str(args.unity_core.resolve())) + '</HintPath></Reference></ItemGroup></Project>', encoding='utf-8')
    (work / 'NuGet.Config').write_text('<configuration><packageSources><clear /></packageSources></configuration>', encoding='utf-8')
    process = subprocess.run(['dotnet', 'run', '--project', str(project), '--configuration', 'Release', '--', str(report), str(work / 'owned-storage')], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding='utf-8', errors='replace', timeout=90)
    report.with_suffix('.build.log').write_text(process.stdout, encoding='utf-8')
    print(process.stdout)
    print('Provenance:', work / 'provenance.json')
    return process.returncode


if __name__ == '__main__':
    raise SystemExit(main())
