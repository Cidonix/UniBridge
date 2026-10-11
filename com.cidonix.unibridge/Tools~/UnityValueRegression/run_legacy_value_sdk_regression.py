"""Compile unchanged Unity math contracts against real legacy Unity SDKs; no Editor launch."""
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
    parser.add_argument('--newtonsoft', type=Path, required=True, help='Newtonsoft net20 assembly')
    parser.add_argument('--editors', type=Path, default=Path('C:/Program Files/Unity/Hub/Editor'))
    parser.add_argument('--compiler-version', default='2022.3.62f3')
    parser.add_argument('--framework-version', default='5.3.2f1')
    parser.add_argument('--versions', nargs='+', default=['5.3.2f1', '5.6.5f1', '5.6.7f1', '2017.4.6f1', '2018.4.8f1'])
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding='utf-8')
    if os.name != 'nt' or not ctypes.windll.shell32.IsUserAnAdmin():
        raise SystemExit('Every launch requires an Administrator Windows token')
    report = args.report.resolve()
    if report.exists():
        raise SystemExit('Report exists; choose a fresh suffix to preserve prior evidence')
    report.parent.mkdir(parents=True, exist_ok=True)
    work = report.parent / ('legacy-value-build-' + uuid.uuid4().hex)
    work.mkdir()
    source_root = (args.candidate or args.repo).resolve()
    sources = []
    source_hashes = {}
    for name in ('UnityValueSerialization.cs', 'UnityTypeConverters.cs'):
        relative = Path('com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Runtime/Serialization') / name
        source = source_root / relative
        if not source.is_file(): source = args.repo / relative
        raw = source.read_bytes()
        target = work / name
        target.write_bytes(raw)
        sources.append(target)
        source_hashes[str(source.resolve())] = hashlib.sha256(raw).hexdigest()
    mono_root = args.editors / args.compiler_version / 'Editor/Data/MonoBleedingEdge'
    mono = mono_root / 'bin/mono.exe'
    compiler = mono_root / 'lib/mono/msbuild/Current/bin/Roslyn/csc.exe'
    framework = args.editors / args.framework_version / 'Editor/Data/MonoBleedingEdge/lib/mono/2.0'
    results = []
    for version in args.versions:
        data = args.editors / version / 'Editor/Data'
        references = [framework / 'mscorlib.dll', framework / 'System.dll', data / 'Managed/UnityEngine.dll',
                      data / 'Managed/UnityEditor.dll', args.newtonsoft.resolve()]
        if (framework / 'System.Core.dll').is_file(): references.append(framework / 'System.Core.dll')
        for path in [mono, compiler] + references:
            if not path.is_file(): raise SystemExit('Required compile input missing: ' + str(path))
        output = work / ('UnityValue-' + version + '.dll')
        command = [str(mono), str(compiler), '/nologo', '/noconfig', '/target:library', '/nostdlib+', '/checked+',
                   '/langversion:7.3', '/define:UNITY_EDITOR,UNITY_EDITOR_WIN', '/out:' + str(output)]
        command += ['/reference:' + str(path) for path in references] + [str(path) for path in sources]
        result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                                encoding='utf-8-sig', errors='replace', timeout=45)
        passed = result.returncode == 0 and output.is_file()
        results.append({'version': version, 'passed': passed, 'exitCode': result.returncode,
                        'command': command, 'output': result.stdout,
                        'referenceHashes': {str(path): hashlib.sha256(path.read_bytes()).hexdigest() for path in references},
                        'assemblySha256': hashlib.sha256(output.read_bytes()).hexdigest() if output.is_file() else None})
        print(version + ': ' + ('PASS' if passed else 'FAIL') + '\n' + result.stdout)
    passed = all(item['passed'] for item in results)
    report.write_text(json.dumps({'passed': passed, 'administrator': True, 'nativeEditorExecution': False,
                                 'proof': 'Exact whole math helper and existing converters with C#7.3 Roslyn and NET2 references; not full legacy adapter or native runtime qualification.',
                                 'sourceHashes': source_hashes, 'results': results}, indent=2), encoding='utf-8')
    return 0 if passed else 1


if __name__ == '__main__':
    raise SystemExit(main())
