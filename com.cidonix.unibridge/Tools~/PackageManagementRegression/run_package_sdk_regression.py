"""Real2020/2022/6.6 SDKcompile; native Client/Sample calls are never executed here."""
import argparse
import ctypes
import hashlib
import json
import os
import subprocess
import sys
import uuid
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--repo', type=Path, required=True)
    parser.add_argument('--candidate', type=Path)
    parser.add_argument('--newtonsoft', type=Path, required=True)
    parser.add_argument('--editors', type=Path, default=Path('C:/Program Files/Unity/Hub/Editor'))
    parser.add_argument('--versions', nargs='+', default=['2020.3.24f1', '2022.3.62f3', '6000.6.5f1'])
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args(); sys.stdout.reconfigure(encoding='utf-8')
    if os.name != 'nt' or not ctypes.windll.shell32.IsUserAnAdmin(): raise SystemExit('Administrator token required')
    report = args.report.resolve()
    if report.exists(): raise SystemExit('Report exists; choose a new evidence suffix')
    report.parent.mkdir(parents=True, exist_ok=True)
    work = report.parent / ('package-sdk-build-' + uuid.uuid4().hex); work.mkdir()
    sources = []; provenance = {}
    paths = [
        'Helpers/PackageManagementModels.cs', 'Helpers/PackageManagementService.cs', 'Helpers/PackageSampleImportGuard.cs',
        'Helpers/UnityPackageManagerAdapter.cs', 'Helpers/Response.cs', 'Tools/Parameters/ManagePackagesParams.cs', 'Tools/ManagePackages.cs']
    for name in paths:
        relative = Path('com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor') / name
        path = args.repo / relative
        if args.candidate and (args.candidate / relative).is_file(): path = args.candidate / relative
        raw = path.read_bytes(); target = work / path.name; target.write_bytes(raw); sources.append(target)
        provenance[str(path.resolve())] = hashlib.sha256(raw).hexdigest()
    fixture = Path(__file__).resolve().parent / 'SdkFixtures.cs'; target = work / fixture.name; target.write_bytes(fixture.read_bytes()); sources.append(target)
    mono_root = args.editors / '2022.3.62f3/Editor/Data/MonoBleedingEdge'
    mono = mono_root / 'bin/mono.exe'; compiler = mono_root / 'lib/mono/msbuild/Current/bin/Roslyn/csc.exe'
    framework = mono_root / 'lib/mono/4.5'
    references_base = [framework / name for name in ['mscorlib.dll', 'System.dll', 'System.Core.dll']]
    references_base.append(framework / 'Facades/netstandard.dll')
    results = []
    for version in args.versions:
        managed = args.editors / version / 'Editor/Data/Managed/UnityEngine'
        references = references_base + [managed / 'UnityEngine.CoreModule.dll', managed / 'UnityEditor.CoreModule.dll', args.newtonsoft.resolve()]
        optional = managed / 'UnityEditor.PackageManagerUIModule.dll'
        if optional.is_file(): references.append(optional)
        output = work / (version + '.dll')
        command = [str(mono), str(compiler), '/nologo', '/noconfig', '/target:library', '/nostdlib+', '/langversion:latest',
                   '/define:UNITY_EDITOR,UNITY_2020_1_OR_NEWER', '/out:' + str(output)]
        command += ['/reference:' + str(path) for path in references] + [str(path) for path in sources]
        process = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding='utf-8-sig', errors='replace', timeout=45)
        passed = process.returncode == 0 and output.is_file()
        results.append({'version': version, 'passed': passed, 'exitCode': process.returncode, 'command': command, 'output': process.stdout,
                        'references': {str(path): hashlib.sha256(path.read_bytes()).hexdigest() for path in references},
                        'assemblySha256': hashlib.sha256(output.read_bytes()).hexdigest() if output.is_file() else None})
        print(version + ': ' + ('PASS' if passed else 'FAIL'))
        if not passed: print('\n'.join(process.stdout.splitlines()[:8]))
    passed = all(value['passed'] for value in results)
    report.write_text(json.dumps({'passed': passed, 'administrator': True, 'nativeEditorExecution': False, 'sources': provenance,
                                 'fixtureSha256': hashlib.sha256(fixture.read_bytes()).hexdigest(),
                                 'limitations': ['Real SDKClient/Sample types, compile only. EditorTask, attributes and scheduler are declared compile adapters.',
                                                 'Unity5.x/2017/2018 standalone legacy adapter does not expose ManagePackages; no legacy UPM support claim.'],
                                 'results': results}, indent=2), encoding='utf-8')
    return 0 if passed else 1


if __name__ == '__main__':
    raise SystemExit(main())
