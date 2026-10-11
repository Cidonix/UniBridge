"""Qualify exact production Unity-value contracts and real-SDK managed reflection paths."""
import argparse
import ctypes
import hashlib
import importlib.util
import json
import os
import subprocess
import uuid
from pathlib import Path
from xml.sax.saxutils import escape


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--repo', type=Path, required=True)
    parser.add_argument('--candidate', type=Path)
    parser.add_argument('--newtonsoft', type=Path, required=True)
    parser.add_argument('--unity-core', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--suite', choices=['math', 'reflection'], default='math')
    args = parser.parse_args()
    if os.name != 'nt' or not ctypes.windll.shell32.IsUserAnAdmin():
        raise SystemExit('Every launch requires an Administrator Windows token')
    repo = args.repo.resolve()
    source_root = (args.candidate or args.repo).resolve()
    report = args.report.resolve()
    if report.exists():
        raise SystemExit('Report exists; choose a fresh suffix to retain earlier evidence')
    report.parent.mkdir(parents=True, exist_ok=True)
    work = report.parent / ('unity-value-build-' + uuid.uuid4().hex)
    work.mkdir()
    spec = importlib.util.spec_from_file_location('value_production_source', repo / 'com.cidonix.unibridge/Tools~/BridgeIsolationRegression/ProductionSource.py')
    source = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(source)
    relative_files = (
        'com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Runtime/Serialization/UnityValueSerialization.cs',
        'com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor/Helpers/McpJson.cs',
        'com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor/Tools/ManageGameObject.cs',
        'com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor/ToolRegistry/ActionOutputSchema.cs',
    )
    provenance = {'administrator': True, 'candidateRoot': str(source_root), 'sourceHashes': {},
                  'wholeProductionFiles': [], 'extractedMethods': [], 'nativeEditorExecution': False,
                  'limitations': ['Real Unity6.6 SDK value types in .NET10, not an Editor runtime.',
                                  'Exact reflection bodies and whole resolver; object reference resolver is an unsupported adapter; no native Material calls.',
                                  'Unity Debug logging is a no-op adapter; physical Camera setter and MCP transport require the native gate.']}
    texts = {}
    for relative in relative_files:
        path = source_root / relative
        if not path.is_file(): path = repo / relative
        raw = path.read_bytes()
        provenance['sourceHashes'][relative] = hashlib.sha256(raw).hexdigest()
        texts[Path(relative).name] = raw.decode('utf-8-sig')
        if not relative.endswith('ManageGameObject.cs'):
            (work / Path(relative).name).write_bytes(raw)
            provenance['wholeProductionFiles'].append(relative)
    converter_relative = 'com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Runtime/Serialization/UnityTypeConverters.cs'
    converter_path = (source_root / converter_relative) if (source_root / converter_relative).exists() else repo / converter_relative
    raw = converter_path.read_bytes()
    (work / converter_path.name).write_bytes(raw)
    provenance['sourceHashes'][converter_relative] = hashlib.sha256(raw).hexdigest()
    provenance['wholeProductionFiles'].append(converter_relative)
    methods = ('SetProperty', 'ConvertJTokenToType', 'TryReadComponentMember', 'PropertyValuesEqual',
               'TryCompareStableObjectReferences', 'LooksLikeSerializedObjectReference', 'IsDefaultGlobalObjectId', 'IsNumericToken')
    helper_relative = 'com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor/Helpers/ReflectionPropertyPath.cs'
    helper_path = source_root / helper_relative
    if not helper_path.is_file(): helper_path = repo / helper_relative
    if helper_path.is_file():
        helper_raw = helper_path.read_bytes()
        (work / helper_path.name).write_bytes(helper_raw)
        provenance['sourceHashes'][helper_relative] = hashlib.sha256(helper_raw).hexdigest()
        provenance['wholeProductionFiles'].append(helper_relative)
        methods += ('WriteReflectionProperty', 'ReflectionPropertyValuesEqual')
    text = texts['ManageGameObject.cs']
    field = text[text.index('        static readonly JsonSerializer InputSerializer'):text.index('        static JObject NormalizeInput')]
    body = '\n'.join(source.method(text, name) for name in methods)
    provenance['extractedMethods'] = list(methods)
    (work / 'ManageGameObjectProduction.cs').write_text('''using System; using System.Reflection;
using Newtonsoft.Json; using Newtonsoft.Json.Linq; using UnityEngine;
using Cidonix.UniBridge.MCP.Editor.Helpers; using Cidonix.UniBridge.MCP.Runtime.Serialization;
namespace MathRegression { static class ManageGameObject {
''' + field + body + '''
 public static ReflectionPropertyPath.WriteResult Write(object target, string path, JToken token) { return WriteReflectionProperty(target, path, token); }
 public static bool Set(object target, string name, JToken value) { return SetProperty(target, name, value); }
 public static bool Read(object target, string name, out object value) { return TryReadComponentMember(target, name, out value); }
 public static bool Equal(object expected, object actual) { return PropertyValuesEqual(expected, actual); }
 static bool SetNestedProperty(object target, string path, JToken token, JsonSerializer serializer) { throw new NotSupportedException("Nested route adapter is not qualified"); }
 static object ResolveObjectArray(JToken token, Type type) { throw new NotSupportedException("Object route adapter is not qualified"); }
}
 static class Debug { public static void LogError(object message) {} public static void LogWarning(object message) {} }
 static class SerializedPropertyPatcher { public static object ResolveObjectReferenceValue(Type type, JToken token) { throw new NotSupportedException("Object reference adapter is not qualified"); } }
}
''', encoding='utf-8')
    provenance['fixtureFiles'] = {}
    names = ('Regression.cs', 'SchemaRegression.cs') if args.suite == 'math' else ('Regression.cs',)
    for name in names:
        fixture_relative = Path('com.cidonix.unibridge/Tools~') / ('UnityValueRegression' if args.suite == 'math' else 'ReflectionPropertyRegression') / name
        fixture = source_root / fixture_relative
        if not fixture.is_file(): fixture = repo / fixture_relative
        (work / fixture.name).write_bytes(fixture.read_bytes())
        provenance['fixtureFiles'][name] = hashlib.sha256(fixture.read_bytes()).hexdigest()
    for label, path in (('newtonsoft', args.newtonsoft), ('unityCore', args.unity_core), ('unityEditor', args.unity_core.parent / 'UnityEditor.CoreModule.dll')):
        provenance[label] = {'path': str(path.resolve()), 'sha256': hashlib.sha256(path.read_bytes()).hexdigest()}
    (work / 'provenance.json').write_text(json.dumps(provenance, indent=2), encoding='utf-8')
    project = work / 'UnityValueRegression.csproj'
    project.write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><DefineConstants>UNITY_6000_0_OR_NEWER</DefineConstants><StartupObject>MathRegression.Regression</StartupObject><NoWarn>CS0618</NoWarn></PropertyGroup><ItemGroup><Reference Include="UnityEngine.CoreModule"><HintPath>''' + escape(str(args.unity_core.resolve())) + '''</HintPath></Reference><Reference Include="UnityEditor.CoreModule"><HintPath>''' + escape(str((args.unity_core.parent / 'UnityEditor.CoreModule.dll').resolve())) + '''</HintPath></Reference><Reference Include="Newtonsoft.Json"><HintPath>''' + escape(str(args.newtonsoft.resolve())) + '''</HintPath></Reference></ItemGroup></Project>''', encoding='utf-8')
    (work / 'NuGet.Config').write_text('<configuration><packageSources><clear /></packageSources></configuration>', encoding='utf-8')
    result = subprocess.run(['dotnet', 'run', '--project', str(project), '--configuration', 'Release', '--', str(report)],
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding='utf-8', errors='replace', timeout=90)
    report.with_suffix('.build.log').write_text(result.stdout, encoding='utf-8')
    print(result.stdout)
    print('Provenance:', work / 'provenance.json')
    return result.returncode


if __name__ == '__main__':
    raise SystemExit(main())
