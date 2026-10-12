"""Compile exact capture/bottom-up production sources with portable file/native seams; never launches Unity."""
import argparse,ctypes,hashlib,json,os,shutil,subprocess,uuid
from pathlib import Path
from xml.sax.saxutils import escape

def main():
    p=argparse.ArgumentParser()
    p.add_argument('--repo',type=Path,default=Path(__file__).resolve().parents[3])
    p.add_argument('--overlay',type=Path)
    p.add_argument('--prior-overlay',type=Path)
    p.add_argument('--step',type=int,choices=(2,3),required=True)
    p.add_argument('--newtonsoft',type=Path,required=True)
    p.add_argument('--unity-core',type=Path,required=True)
    p.add_argument('--report',type=Path,required=True)
    a=p.parse_args()
    if os.name=='nt' and not ctypes.windll.shell32.IsUserAnAdmin():raise SystemExit('Administrator shell required')
    report=a.report.resolve()
    if report.exists():raise SystemExit('Report exists; choose fresh evidence path')
    report.parent.mkdir(parents=True,exist_ok=True)
    work=report.parent/('profiler-extension-build-'+uuid.uuid4().hex);work.mkdir()
    editor=Path('com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor');runtime=Path('com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Runtime/Serialization')
    relatives=[editor/'Helpers/ProfilerCaptureLease.cs',editor/'Tools/RuntimeProfiler.CaptureLease.cs',editor/'Helpers/McpJson.cs',editor/'Helpers/Response.cs',
               editor/'ToolRegistry/RuntimeProfilerActionOutputRules.cs',editor/'Tools/Parameters/RuntimeProfilerParams.cs',editor/'ToolRegistry/McpAttributes.cs',
               runtime/'UnityValueSerialization.cs',runtime/'UnityTypeConverters.cs']
    if a.step==3:relatives += [editor/'Helpers/RecordedProfilerBottomUp.cs',editor/'Tools/RuntimeProfiler.RecordedBottomUp.cs',editor/'Helpers/RecordedProfilerFrameAnalysis.cs',editor/'Helpers/RecordedProfilerFrameReader.cs']
    sources={}
    for relative in relatives:
        source=next((base/relative for base in (a.overlay,a.prior_overlay,a.repo) if base and (base/relative).is_file()),None)
        if source is None:raise SystemExit('Source missing: '+str(relative))
        shutil.copyfile(source,work/source.name);sources[relative.as_posix()]=hashlib.sha256(source.read_bytes()).hexdigest()
    fixtures={}
    for name in ('Regression.cs','Fixtures.cs'):
        relative=Path('com.cidonix.unibridge/Tools~/ProfilerExtensionsRegression')/name
        fixture=next((base/relative for base in (a.overlay,a.prior_overlay,a.repo) if base and (base/relative).is_file()),Path(__file__).resolve().parent/name)
        shutil.copyfile(fixture,work/name);fixtures[name]=fixture
    policy_relative=editor/'ToolRegistry/ToolExecutionScheduler.cs'
    policy_source=next(base/policy_relative for base in (a.overlay,a.prior_overlay,a.repo) if base and (base/policy_relative).is_file())
    policy=policy_source.read_text(encoding='utf-8-sig')
    anchor='            if (string.Equals(toolName, "UniBridge_RuntimeProfiler", StringComparison.Ordinal))'
    begin=policy.index(anchor);end=policy.index('            if (string.Equals(toolName, "UniBridge_ManagePackages", StringComparison.Ordinal))',begin)
    body=policy[begin:end]
    (work/'Policy.cs').write_text('using System;using System.Linq;using Newtonsoft.Json.Linq;namespace Cidonix.UniBridge.MCP.Editor.ToolRegistry { static class ProductionPolicy { public static ToolExecutionPolicy Resolve(JObject parameters) { string toolName="UniBridge_RuntimeProfiler";'+body+'return ToolExecutionPolicy.ReadOnly;}}}',encoding='utf-8')
    sources[policy_relative.as_posix()]=hashlib.sha256(policy_source.read_bytes()).hexdigest()
    define=('BOTTOM_UP;' if a.step==3 else '')+('UNITY_6000_0_OR_NEWER' if b'GetEntityId' in a.unity_core.read_bytes() else '')
    project='<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><DefineConstants>'+define+'</DefineConstants><NoWarn>CS0649;CS0618</NoWarn></PropertyGroup><ItemGroup><Reference Include="Newtonsoft.Json"><HintPath>'+escape(str(a.newtonsoft.resolve()))+'</HintPath></Reference><Reference Include="UnityEngine.CoreModule"><HintPath>'+escape(str(a.unity_core.resolve()))+'</HintPath></Reference></ItemGroup></Project>'
    (work/'Regression.csproj').write_text(project,encoding='utf-8');(work/'NuGet.Config').write_text('<configuration><packageSources><clear /></packageSources></configuration>')
    run=subprocess.run(['dotnet','run','--project',str(work/'Regression.csproj'),'--configuration','Release','--',str(report),str(work/'owned')],stdout=subprocess.PIPE,stderr=subprocess.STDOUT,text=True,timeout=120,check=False)
    report.with_suffix('.build.log').write_text(run.stdout,encoding='utf-8')
    result=json.loads(report.read_text()) if report.exists() else {'passed':False,'error':'No report; inspect build log','exitCode':run.returncode}
    result.update(sourceHashes=sources,fixtureHashes={name:hashlib.sha256(fixture.read_bytes()).hexdigest() for name,fixture in fixtures.items()},administrator=os.name!='nt' or bool(ctypes.windll.shell32.IsUserAnAdmin()),step=a.step)
    report.write_text(json.dumps(result,indent=2),encoding='utf-8');print(json.dumps({key:result.get(key) for key in ('passed','passedCount','failedCount','exitCode','error')}))
    return run.returncode
if __name__=='__main__':raise SystemExit(main())
