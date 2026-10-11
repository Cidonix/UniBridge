#!/usr/bin/env python3
"""Compile production-linked Input API analyzer, context adapter and closed schemas with emitted metadata fixtures."""
import argparse
import ctypes
import hashlib
import json
import os
import shutil
import subprocess
import tempfile
from pathlib import Path
from xml.sax.saxutils import escape

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--source-root', type=Path, required=True)
    parser.add_argument('--base-root', type=Path, required=True)
    parser.add_argument('--newtonsoft-dll', type=Path, required=True)
    args = parser.parse_args()
    if os.name != 'nt' or not ctypes.windll.shell32.IsUserAnAdmin(): parser.error('Run from an Administrator terminal.')
    suite = Path(__file__).resolve().parent
    source_root = args.source_root.resolve()
    base_root = args.base_root.resolve()
    module = Path('com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor')
    production = [module / name for name in ['Helpers/InputApiAnalyzer.cs','Helpers/InputApiHints.cs','Helpers/InputBackendResolver.cs',
        'Helpers/McpJson.cs','Helpers/ObsoleteApiAnalyzer.cs','ToolRegistry/InputApiOutputContract.cs','ToolRegistry/ActionOutputSchema.cs',
        'ToolRegistry/ScriptActionOutputRules.cs','ToolRegistry/IntelligenceActionOutputRules.cs']]
    provenance = {}
    with tempfile.TemporaryDirectory(prefix='unibridge-input-api-') as temporary:
        work = Path(temporary)
        for relative in production:
            path = source_root / relative
            if not path.exists(): path = base_root / relative
            shutil.copyfile(path, work / path.name)
            provenance[relative.as_posix()] = hashlib.sha256(path.read_bytes()).hexdigest()
        for name in ['Regression.cs','ContextStubs.cs']: shutil.copyfile(suite / name, work / name)
        shutil.copyfile(base_root / 'com.cidonix.unibridge/Tools~/InputBackendRegression/Stubs.cs', work / 'UnityStubs.cs')
        validation = (source_root / module / 'Tools/ManageScript.Validation.cs').read_text(encoding='utf-8-sig')
        snippet = validation[validation.index('        internal static InputApiReport AnalyzeInputApiHints('):validation.index('        internal static object GetObsoleteApiHintsOutputSchema()')]
        (work / 'ManageScript.Production.cs').write_text('using Cidonix.UniBridge.MCP.Editor.Helpers; namespace Cidonix.UniBridge.MCP.Editor.Tools { public static partial class ManageScript {\n' + snippet + '\n} }', encoding='utf-8')
        original = (base_root / module / 'ToolRegistry/ActionOutputValidation.cs').read_text(encoding='utf-8-sig')
        suffix = original[original.index('        internal static List<JObject> Check('):]
        (work / 'ActionOutputValidation.cs').write_text('using System; using System.Collections.Generic; using System.Globalization; using System.IO; using System.Linq; using System.Text.RegularExpressions; using Newtonsoft.Json; using Newtonsoft.Json.Linq; using Cidonix.UniBridge.MCP.Editor.Helpers; namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry { internal static class ActionOutputValidation {\n' + suffix, encoding='utf-8')
        dependencies = [base_root / 'com.cidonix.unibridge/Plugins' / name for name in ['CodeAnalysis/Microsoft.CodeAnalysis.dll','CodeAnalysis/Microsoft.CodeAnalysis.CSharp.dll','Shared/System.Collections.Immutable.dll','Shared/System.Reflection.Metadata.dll']]
        dependencies.append(args.newtonsoft_dll.resolve())
        refs = ''.join('<Reference Include="' + escape(path.stem) + '"><HintPath>' + escape(str(path)) + '</HintPath><Private>true</Private></Reference>' for path in dependencies)
        (work / 'NuGet.config').write_text('<configuration><packageSources><clear /></packageSources></configuration>')
        (work / 'Regression.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>disable</ImplicitUsings><Nullable>disable</Nullable><StartupObject>InputApiRegression</StartupObject></PropertyGroup><ItemGroup>' + refs + '</ItemGroup></Project>', encoding='utf-8')
        built = subprocess.run(['dotnet','build',str(work / 'Regression.csproj'),'-c','Release','--nologo','-v','quiet'], capture_output=True, text=True, timeout=180)
        if built.returncode:
            final = {'passed':0,'failed':1,'compile':built.stdout+built.stderr,'provenance':provenance}
        else:
            report = work / 'result.json'
            # Report fixtures use the existing Obsolete DTO only to prove that the new independent report composes with its contract.
            run = subprocess.run(['dotnet',str(work / 'bin/Release/net10.0/Regression.dll'),str(report),str(source_root / module)], capture_output=True,text=True,timeout=180)
            final = json.loads(report.read_text(encoding='utf-8')) if report.exists() else {'passed':0,'failed':1}
            final.update(provenance=provenance,stdout=run.stdout,stderr=run.stderr)
    args.report.resolve().parent.mkdir(parents=True,exist_ok=True)
    args.report.write_text(json.dumps(final,ensure_ascii=False,indent=2),encoding='utf-8')
    print(json.dumps({'report':str(args.report.resolve()),'passed':final['passed'],'failed':final['failed']}))
    return 0 if final['failed'] == 0 else 1
if __name__ == '__main__': raise SystemExit(main())
