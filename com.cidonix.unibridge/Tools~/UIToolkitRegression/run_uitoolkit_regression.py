"""Compile unchanged UI Toolkit production code against a deterministic Editor substrate."""
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
    parser.add_argument('--newtonsoft-dll', type=Path, required=True)
    parser.add_argument('--filter')
    parser.add_argument('--tool-source-ref', help='Focused old-tool control; pure new helpers remain current and are recorded separately.')
    args = parser.parse_args()
    if os.name != 'nt' or not ctypes.windll.shell32.IsUserAnAdmin():
        parser.error('Run from an Administrator terminal.')
    suite = Path(__file__).resolve().parent
    repo = suite.parents[2]
    package = repo/'com.cidonix.unibridge'
    dependency = args.newtonsoft_dll.resolve(strict=True)
    args.report = args.report.resolve()
    args.report.parent.mkdir(parents=True, exist_ok=True)
    paths = [package/'Modules/Cidonix.UniBridge.MCP.Editor/Tools/ManageUIToolkit.cs',
             package/'Modules/Cidonix.UniBridge.MCP.Editor/Helpers/Response.cs']
    paths.extend(sorted((package/'Modules/Cidonix.UniBridge.MCP.Editor/Helpers').glob('UIToolkit*.cs')))
    if len(paths) < 4:
        parser.error('Production UI Toolkit validation/writer helpers are not ready.')
    provenance = {'administrator':True,'toolSourceRef':args.tool_source_ref or 'working-tree',
                  'helperSourceRef':'working-tree','sources':{},'newtonsoftSha256':hashlib.sha256(dependency.read_bytes()).hexdigest(),
                  'scope':'Entire unchanged production tool/helpers; deterministic Unity import substrate, not actual Unity CSS validation.'}
    with tempfile.TemporaryDirectory(prefix='unibridge-uitoolkit-build-') as temporary:
        work = Path(temporary)
        needs_mcp_json = False
        for path in paths:
            relative = path.relative_to(repo)
            if args.tool_source_ref and path.name=='ManageUIToolkit.cs':
                content = subprocess.check_output(['git','show',f'{args.tool_source_ref}:{relative.as_posix()}'],cwd=repo).decode('utf-8-sig')
            else:
                content = path.read_text(encoding='utf-8-sig')
            provenance['sources'][relative.as_posix()] = hashlib.sha256(content.encode()).hexdigest()
            (work/path.name).write_text(content,encoding='utf-8')
            needs_mcp_json |= 'McpJson' in content or 'ToObjectIndependent' in content
        if needs_mcp_json:
            path = package/'Modules/Cidonix.UniBridge.MCP.Editor/Helpers/McpJson.cs'
            content = path.read_text(encoding='utf-8-sig')
            provenance['sources'][path.relative_to(repo).as_posix()] = hashlib.sha256(content.encode()).hexdigest()
            (work/path.name).write_text(content,encoding='utf-8')
        for path in suite.glob('*.cs'):
            shutil.copyfile(path,work/path.name)
        (work/'source-provenance.json').write_text(json.dumps(provenance),encoding='utf-8')
        (work/'Regression.csproj').write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
            '<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>'
            '<Nullable>annotations</Nullable><NoWarn>CS0649</NoWarn>'
            '<StartupObject>Cidonix.UniBridge.UIToolkitRegression.Regression</StartupObject>'
            '</PropertyGroup><ItemGroup><Reference Include="Newtonsoft.Json"><HintPath>'
            +escape(str(dependency))+'</HintPath></Reference></ItemGroup></Project>',encoding='utf-8')
        command = ['dotnet','run','--project',str(work/'Regression.csproj'),'--configuration','Release','--',
                   str(args.report),str(work/'source-provenance.json')]
        if args.filter:
            command.append(args.filter)
        result = subprocess.run(command,text=True,stdout=subprocess.PIPE,stderr=subprocess.STDOUT,check=False)
        log = args.report.with_suffix('.log')
        log.write_text(result.stdout,encoding='utf-8')
        if args.report.exists():
            report = json.loads(args.report.read_text(encoding='utf-8-sig'))
            report['buildAndRuntimeLog'] = str(log)
            args.report.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
        print(result.stdout)
        return result.returncode


if __name__=='__main__':
    raise SystemExit(main())
