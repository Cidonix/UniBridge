#!/usr/bin/env python3
"""Production-linked resolver and ManageUI consumer checks; never launches Unity."""
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


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--unity-editor-data', type=Path, action='append', default=[],
                        help='Compile the whole resolver against each installed Unity SDK (without launching an Editor).')
    args = parser.parse_args()
    if os.name != 'nt' or not ctypes.windll.shell32.IsUserAnAdmin():
        parser.error('Run from an Administrator terminal.')
    suite = Path(__file__).resolve().parent
    repo = suite.parents[2]
    helpers = repo / 'com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor/Helpers'
    tool = repo / 'com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor/Tools/ManageUI.cs'
    text = tool.read_text(encoding='utf-8-sig')
    action = text[text.index('        static object EnsureEventSystem('):text.index('        static object CreateElement(')]
    consumers = text[text.index('        static GameObject EnsureEventSystemObject('):text.index('        static Type FindLoadedType(')]
    wrapper = '''using System; using UnityEngine; using UnityEngine.EventSystems; using UnityEditor;
using Cidonix.UniBridge.MCP.Editor.Helpers; using Cidonix.UniBridge.MCP.Editor.Tools.Parameters;
namespace Cidonix.UniBridge.MCP.Editor.Tools { public static class ManageUI {
public static GameObject Existing; public static bool NewModuleAvailable; public static int Dirty;
public static object Run(ManageUIParams p) { try { return EnsureEventSystem(p); } catch(Exception e) { return Response.Error(e.Message); } }
static GameObject FindEventSystem() { return Existing; }
static void MarkSceneDirty(GameObject target) { Dirty++; }
static object BuildGameObjectInfo(GameObject target) { return new { target.name }; }
static Type FindLoadedType(string name) { return NewModuleAvailable ? typeof(UnityEngine.InputSystem.UI.InputSystemUIInputModule) : null; }
''' + action + consumers + '\n} }'
    profiles = [('modern-no-flags', 'UNITY_2019_1_OR_NEWER', 'Unknown'),
                ('modern-old', 'UNITY_2019_1_OR_NEWER;ENABLE_LEGACY_INPUT_MANAGER', 'Old'),
                ('modern-new', 'UNITY_2019_1_OR_NEWER;ENABLE_INPUT_SYSTEM', 'New'),
                ('modern-both', 'UNITY_2019_1_OR_NEWER;ENABLE_INPUT_SYSTEM;ENABLE_LEGACY_INPUT_MANAGER', 'Both'),
                ('legacy-5-3-and-2017', '', 'Old')]
    results = []
    provenance = {str(path.relative_to(repo)): hashlib.sha256(path.read_bytes()).hexdigest()
                  for path in [tool, helpers / 'InputBackendResolver.cs', helpers / 'Response.cs', suite / 'Regression.cs', suite / 'Stubs.cs']}
    with tempfile.TemporaryDirectory(prefix='unibridge-input-backend-') as temporary:
        work = Path(temporary)
        shutil.copyfile(helpers / 'InputBackendResolver.cs', work / 'InputBackendResolver.cs')
        shutil.copyfile(helpers / 'Response.cs', work / 'Response.cs')
        for filename in ['Regression.cs', 'Stubs.cs']: shutil.copyfile(suite / filename, work / filename)
        (work / 'ManageUI.Production.cs').write_text(wrapper, encoding='utf-8')
        (work / 'NuGet.config').write_text('<configuration><packageSources><clear /></packageSources></configuration>')
        for profile, defines, fallback in profiles:
            (work / 'Regression.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
                '<TargetFramework>net10.0</TargetFramework><ImplicitUsings>disable</ImplicitUsings><Nullable>disable</Nullable>'
                '<DefineConstants>' + defines + '</DefineConstants></PropertyGroup></Project>', encoding='utf-8')
            built = subprocess.run(['dotnet', 'build', str(work / 'Regression.csproj'), '-c', 'Release', '--nologo', '-v', 'quiet'],
                                   capture_output=True, text=True, timeout=180)
            if built.returncode:
                results.append({'profile': profile, 'passed': 0, 'failed': 1, 'compile': built.stdout + built.stderr})
                continue
            report = work / (profile + '.json')
            run = subprocess.run(['dotnet', str(work / 'bin/Release/net10.0/Regression.dll'), str(report), fallback],
                                 capture_output=True, text=True, timeout=120)
            result = json.loads(report.read_text(encoding='utf-8')) if report.exists() else {'passed': 0, 'failed': 1}
            result.update(profile=profile, stdout=run.stdout, stderr=run.stderr)
            results.append(result)
        sdk = subprocess.check_output(['dotnet', '--list-sdks'], text=True).splitlines()[-1]
        sdk_version, sdk_directory = re.match(r'([^ ]+) \[([^]]+)\]', sdk).groups()
        compiler = Path(sdk_directory) / sdk_version / 'Roslyn/bincore/csc.dll'
        for unity in args.unity_editor_data:
            unity = unity.resolve(strict=True)
            reference_root = next((root for root in [unity / 'UnityReferenceAssemblies/unity-4.8-api',
                unity / 'MonoBleedingEdge/lib/mono/4.7.1-api', unity / 'Mono/lib/mono/2.0'] if (root / 'mscorlib.dll').is_file()), None)
            if reference_root is None: raise FileNotFoundError('No framework references: ' + str(unity))
            references = [reference_root / name for name in ['mscorlib.dll', 'System.dll', 'System.Core.dll'] if (reference_root / name).is_file()]
            references += sorted((reference_root / 'Facades').glob('*.dll'))
            managed = unity / 'Managed'
            if (managed / 'UnityEngine/UnityEditor.CoreModule.dll').is_file():
                references += [managed / 'UnityEngine' / name for name in ['UnityEditor.CoreModule.dll', 'UnityEngine.CoreModule.dll']]
            else: references += [managed / 'UnityEditor.dll', managed / 'UnityEngine.dll']
            rsp = work / 'unity-compile.rsp'
            rsp.write_text('\n'.join(['-nologo', '-nostdlib+', '-target:library', '-langversion:3',
                '-out:"' + str(work / 'InputBackend.Unity.dll') + '"'] + ['-r:"' + str(path) + '"' for path in references] +
                ['"' + str(work / 'InputBackendResolver.cs') + '"']), encoding='utf-8')
            built = subprocess.run(['dotnet', str(compiler), '-noconfig', '@' + str(rsp)], capture_output=True, text=True, timeout=120)
            results.append({'profile': 'SDK-' + unity.parents[1].name, 'passed': int(built.returncode == 0),
                'failed': int(built.returncode != 0), 'compile': built.stdout + built.stderr,
                'detail': 'Whole production resolver compiled as C# 3 against physical Unity SDK references. No Editor was launched.'})
    final = {'provenance': provenance, 'passed': sum(r['passed'] for r in results), 'failed': sum(r['failed'] for r in results), 'profiles': results}
    args.report.resolve().parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(final, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({'report': str(args.report.resolve()), 'passed': final['passed'], 'failed': final['failed']}))
    return 0 if final['failed'] == 0 else 1


if __name__ == '__main__': raise SystemExit(main())
