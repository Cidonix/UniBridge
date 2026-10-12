"""Exact production numeric helper and JSON contracts; native Editor is a separate gate."""
import argparse, ctypes, hashlib, json, os, subprocess, uuid
from pathlib import Path
from xml.sax.saxutils import escape

def main():
    p = argparse.ArgumentParser()
    p.add_argument('--repo', type=Path, required=True)
    p.add_argument('--candidate', type=Path)
    p.add_argument('--newtonsoft', type=Path, required=True)
    p.add_argument('--unity-core', type=Path, required=True)
    p.add_argument('--report', type=Path, required=True)
    a = p.parse_args()
    if os.name != 'nt' or not ctypes.windll.shell32.IsUserAnAdmin(): raise SystemExit('Administrator token required')
    root, overlay, report = a.repo.resolve(), (a.candidate or a.repo).resolve(), a.report.resolve()
    if report.exists(): raise SystemExit('Fresh report required')
    report.parent.mkdir(parents=True, exist_ok=True)
    work = report.parent / ('serialized-numeric-build-' + uuid.uuid4().hex)
    work.mkdir()
    files = [
        'com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor/Helpers/SerializedNumericValue.cs',
        'com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor/Helpers/McpJson.cs',
        'com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Runtime/Serialization/UnityValueSerialization.cs',
        'com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Runtime/Serialization/UnityTypeConverters.cs',
        'com.cidonix.unibridge/Tools~/SerializedNumericRegression/Regression.cs',
    ]
    hashes = {}
    for relative in files:
        path = overlay / relative
        if not path.is_file(): path = root / relative
        raw = path.read_bytes()
        (work / path.name).write_bytes(raw)
        hashes[relative] = hashlib.sha256(raw).hexdigest()
    refs = ''.join('<Reference Include="' + name + '"><HintPath>' + escape(str(path.resolve())) + '</HintPath></Reference>' for name, path in [('UnityEngine.CoreModule', a.unity_core), ('Newtonsoft.Json', a.newtonsoft)])
    (work / 'Regression.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><DefineConstants>UNITY_6000_0_OR_NEWER</DefineConstants><StartupObject>SerializedNumericRegression.Regression</StartupObject><NuGetAudit>false</NuGetAudit><NoWarn>CS0618</NoWarn></PropertyGroup><ItemGroup>' + refs + '</ItemGroup></Project>', encoding='utf-8')
    (work / 'NuGet.Config').write_text('<configuration><packageSources><clear /></packageSources></configuration>', encoding='utf-8')
    run = subprocess.run(['C:/Program Files/dotnet/dotnet.exe', 'run', '--project', str(work / 'Regression.csproj'), '-c', 'Release', '--', str(report)], capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=120)
    report.with_suffix('.build.log').write_text(run.stdout + run.stderr, encoding='utf-8')
    provenance = {'administrator': True, 'candidateRoot': str(overlay), 'sourceHashes': hashes, 'wholeProductionFiles': files[:4], 'nativeExecuted': False,
        'adapter': 'Named SerializedProperty accessor adapter stores typed values; production helper is unchanged. Native Editor and full patcher integration are separate gates.',
        'sdk': {str(path.resolve()): hashlib.sha256(path.read_bytes()).hexdigest() for path in (a.unity_core, a.newtonsoft)}, 'exitCode': run.returncode}
    report.with_suffix('.provenance.json').write_text(json.dumps(provenance, indent=2), encoding='utf-8')
    print(run.stdout + run.stderr)
    return run.returncode

if __name__ == '__main__': raise SystemExit(main())
