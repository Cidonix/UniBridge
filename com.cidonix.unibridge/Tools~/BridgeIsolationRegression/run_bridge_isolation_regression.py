"""Production-linked point 7 qualification; no Unity process or authored paths touched."""
import argparse
import ctypes
import hashlib
import json
import os
import shutil
import subprocess
import sys
import uuid
from pathlib import Path
from xml.sax.saxutils import escape

from ProductionSource import build


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--repo', type=Path, default=Path(__file__).resolve().parents[3])
    parser.add_argument('--newtonsoft', type=Path, required=True)
    parser.add_argument('--goldens', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--groups', default='json,routes,tracing,lifecycle,store')
    args = parser.parse_args()
    if os.name != 'nt' or not ctypes.windll.shell32.IsUserAnAdmin():
        raise SystemExit('An Administrator Windows shell is required for every launch')
    args.repo = args.repo.resolve(); args.report = args.report.resolve()
    args.newtonsoft = args.newtonsoft.resolve(); args.goldens = args.goldens.resolve()
    if args.report.exists():
        raise SystemExit('Report already exists; choose a new suffix to preserve earlier evidence')
    args.report.parent.mkdir(parents=True, exist_ok=True)
    work = args.report.parent / ('bridge-isolation-build-' + uuid.uuid4().hex)
    work.mkdir()
    provenance = build(args.repo, work)
    provenance.update({'administrator': True, 'newtonsoftSha256': hashlib.sha256(args.newtonsoft.read_bytes()).hexdigest(),
                       'goldenArtifacts': {file.name: hashlib.sha256(file.read_bytes()).hexdigest() for file in sorted(args.goldens.glob('*.json'))},
                       'fixtureRoot': str(work / 'owned-storage'), 'nativeHandleQualification': False})
    here = Path(__file__).resolve().parent
    for file in here.glob('*.cs'):
        if 'Live' not in file.name:
            shutil.copyfile(file, work / file.name)
    project = work / 'BridgeIsolation.csproj'
    project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><DefineConstants>UNITY_EDITOR;UNITY_6000_0_OR_NEWER</DefineConstants><StartupObject>Cidonix.UniBridge.BridgeIsolationRegression.Program</StartupObject><NoWarn>CS0649;CS0067;CS0414</NoWarn></PropertyGroup><ItemGroup><Reference Include="Newtonsoft.Json"><HintPath>' + escape(str(args.newtonsoft)) + '</HintPath></Reference></ItemGroup></Project>', encoding='utf-8')
    (work / 'NuGet.Config').write_text('<configuration><packageSources><clear /></packageSources></configuration>', encoding='utf-8')
    provenance_path = work / 'provenance.json'
    provenance_path.write_text(json.dumps(provenance, indent=2), encoding='utf-8')
    run = subprocess.run(['dotnet', 'run', '--project', str(project), '--configuration', 'Release', '--', str(args.report), str(provenance_path), str(args.goldens), str(work / 'owned-storage'), args.groups], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, check=False)
    args.report.with_suffix('.build.log').write_text(run.stdout, encoding='utf-8')
    print(run.stdout)
    return run.returncode


if __name__ == '__main__':
    raise SystemExit(main())
