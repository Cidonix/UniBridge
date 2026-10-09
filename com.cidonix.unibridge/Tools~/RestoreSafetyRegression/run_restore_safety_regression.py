"""Run unchanged production restore code over unique owned filesystem fixtures."""
import argparse
import ctypes
import hashlib
import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path
from xml.sax.saxutils import escape


def source_text(repo, path, source_ref):
    if source_ref:
        return subprocess.check_output(
            ['git', 'show', f'{source_ref}:{path.as_posix()}'], cwd=repo
        ).decode('utf-8-sig')
    return (repo / path).read_text(encoding='utf-8-sig')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--newtonsoft-dll', type=Path, required=True)
    parser.add_argument('--source-ref')
    parser.add_argument('--filter')
    args = parser.parse_args()
    if os.name != 'nt' or not ctypes.windll.shell32.IsUserAnAdmin():
        parser.error('Run from an Administrator terminal.')
    suite = Path(__file__).resolve().parent
    repo = suite.parents[2]
    dependency = args.newtonsoft_dll.resolve(strict=True)
    args.report = args.report.resolve()
    args.report.parent.mkdir(parents=True, exist_ok=True)
    tools_dir = Path('com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor/Tools')
    paths = [tools_dir/'WorkSession.cs',
             Path('com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor/Helpers/Response.cs')]
    if not args.source_ref:
        paths.extend(path.relative_to(repo) for path in sorted((repo/tools_dir).glob('WorkSession*.cs'))
                     if path.name not in ('WorkSession.cs', 'WorkSession.Semantics.cs'))
    provenance = {'sourceRef': args.source_ref or 'working-tree', 'sources': {},
                  'newtonsoftSha256': hashlib.sha256(dependency.read_bytes()).hexdigest(),
                  'administrator': True, 'scope': 'isolated-filesystem-and-editor-substrate'}
    with tempfile.TemporaryDirectory(prefix='unibridge-restore-build-') as temporary:
        work = Path(temporary).resolve()
        needs_mcp_json = False
        for path in paths:
            content = source_text(repo, path, args.source_ref)
            provenance['sources'][path.as_posix()] = hashlib.sha256(content.encode()).hexdigest()
            (work/path.name).write_text(content, encoding='utf-8')
            needs_mcp_json |= 'McpJson' in content or 'ToObjectIndependent' in content
        if needs_mcp_json:
            path = Path('com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor/Helpers/McpJson.cs')
            content = source_text(repo, path, args.source_ref)
            provenance['sources'][path.as_posix()] = hashlib.sha256(content.encode()).hexdigest()
            (work/path.name).write_text(content, encoding='utf-8')
        snapshot_source = suite/'SnapshotSource.py'
        if snapshot_source.is_file():
            sys.dont_write_bytecode = True
            spec = importlib.util.spec_from_file_location('snapshot_source', snapshot_source)
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            extra = module.build_snapshot_production(repo, args.source_ref, work)
            if isinstance(extra, dict):
                provenance['sources'].update(extra)
        # Compile the actual loaded-dirty-state guard too, without importing
        # unrelated semantic scene traversal. Its Unity APIs use the same scene
        # substrate qualified by SnapshotRegression.
        semantics_path = tools_dir/'WorkSession.Semantics.cs'
        semantics = source_text(repo, semantics_path, args.source_ref)
        provenance['sources'][semantics_path.as_posix()] = hashlib.sha256(semantics.encode()).hexdigest()
        if 'static string GetLoadedRestoreBlocker(' in semantics:
            extraction_path = suite.parent/'EditorWaitRegression/run_editor_wait_regression.py'
            spec = importlib.util.spec_from_file_location('restore_method_extraction', extraction_path)
            helper = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(helper)
            loaded_guard = helper.method(semantics, 'GetLoadedRestoreBlocker')
        else:
            loaded_guard = 'static string GetLoadedRestoreBlocker(string path) => null;'
        (work/'WorkSessionLoadedGuard.cs').write_text(
            'using System; using System.Linq; using UnityEditor; using UnityEngine; using UnityEngine.SceneManagement;\n'
            'namespace Cidonix.UniBridge.MCP.Editor.Tools { public static partial class WorkSession {\n'
            + loaded_guard + '\n} }', encoding='utf-8')
        for path in suite.glob('*.cs'):
            if 'Live' not in path.name:
                shutil.copyfile(path, work/path.name)
        (work/'source_provenance.json').write_text(json.dumps(provenance), encoding='utf-8')
        (work/'Regression.csproj').write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
            '<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>'
            '<Nullable>annotations</Nullable><NoWarn>CS0649</NoWarn>'
            '<StartupObject>Cidonix.UniBridge.RestoreSafetyRegression.RestoreSafetyRegression</StartupObject>'
            '</PropertyGroup><ItemGroup><Reference Include="Newtonsoft.Json"><HintPath>'
            + escape(str(dependency)) + '</HintPath></Reference></ItemGroup></Project>', encoding='utf-8')
        command = ['dotnet', 'run', '--project', str(work/'Regression.csproj'), '--configuration', 'Release', '--',
                   str(args.report), str(work/'source_provenance.json')]
        if args.filter:
            command.append(args.filter)
        result = subprocess.run(command, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, check=False)
        log_path = args.report.with_suffix('.log')
        log_path.write_text(result.stdout, encoding='utf-8')
        if args.report.is_file():
            report = json.loads(args.report.read_text(encoding='utf-8-sig'))
            report['buildAndRuntimeLog'] = str(log_path)
            args.report.write_text(json.dumps(report, indent=2), encoding='utf-8')
        print(result.stdout)
        return result.returncode


if __name__ == '__main__':
    raise SystemExit(main())
