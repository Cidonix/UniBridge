"""Source-linked resource IO qualification; never launches Unity. Fresh output only."""
import argparse, ctypes, hashlib, json, pathlib, subprocess, sys, xml.sax.saxutils
p=argparse.ArgumentParser();p.add_argument('--candidate');p.add_argument('--source',required=True);p.add_argument('--output',required=True);args=p.parse_args()
if not ctypes.windll.shell32.IsUserAnAdmin():raise SystemExit('Administrator process required')
source=pathlib.Path(args.source).resolve();candidate=pathlib.Path(args.candidate).resolve() if args.candidate else source;out=pathlib.Path(args.output).resolve()
out.mkdir(parents=True,exist_ok=False)
base_file=candidate/'base-sha256.json';base=None;guards=[]
if candidate!=source:
    if not base_file.is_file():raise SystemExit('External candidate requires its exact base-sha256.json guard')
    base=json.loads(base_file.read_text(encoding='utf-8-sig'))
    for rel,wanted in base['files'].items():
        got=hashlib.sha256((source/rel).read_bytes()).hexdigest();guards.append({'path':rel,'actual':got,'expected':wanted,'passed':got.lower()==wanted.lower()})
if not all(x['passed'] for x in guards):raise SystemExit('Base hash changed; guarded root integration/rebase required: '+json.dumps(guards))
suite=candidate/'com.cidonix.unibridge/Tools~/ResourceBudgetRegression';module='com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor'
def linked(rel):
    overlay=candidate/module/rel
    return overlay if overlay.is_file() else source/module/rel
files=[suite/'Stubs.cs',suite/'Regression.cs']+[linked(rel) for rel in ['Helpers/ResourceBudgetIO.cs','Helpers/ResourceUriHelper.cs','Tools/ResourceTools.cs','Tools/Parameters/ResourceToolsParams.cs','ToolRegistry/ResourceActionOutputRules.cs']]+[source/module/rel for rel in ['Helpers/Response.cs','ToolRegistry/ActionOutputSchema.cs']]
def inventory(paths):return [{'path':str(f),'sha256':hashlib.sha256(f.read_bytes()).hexdigest()} for f in paths]
before=inventory(files)
def esc(s):return xml.sax.saxutils.escape(str(s),{'"':'&quot;'})
cs='<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems><Nullable>disable</Nullable><ImplicitUsings>disable</ImplicitUsings></PropertyGroup><ItemGroup>'+''.join('<Compile Include="'+esc(f)+'" />' for f in files)+'<Reference Include="Newtonsoft.Json"><HintPath>C:/Program Files/Unity/Hub/Editor/6000.6.5f1/Editor/Data/Managed/Newtonsoft.Json.dll</HintPath></Reference></ItemGroup></Project>'
(out/'Regression.csproj').write_text(cs,encoding='utf-8');(out/'NuGet.Config').write_text('<configuration><packageSources><clear /></packageSources></configuration>',encoding='utf-8')
cmd=['C:/Program Files/dotnet/dotnet.exe','run','--project',str(out/'Regression.csproj'),'--configuration','Release','--',str(out/'report.json')]
r=subprocess.run(cmd,text=True,capture_output=True,timeout=90,cwd=out)
(out/'compiler.log').write_text(r.stdout+'\n'+r.stderr,encoding='utf-8')
after=inventory(files);unchanged=before==after
for guard in guards:
    guard['actualAfter']=hashlib.sha256((source/guard['path']).read_bytes()).hexdigest();guard['unchangedDuringTest']=guard['actualAfter'].lower()==guard['actual'].lower()
unchanged=unchanged and all(x['unchangedDuringTest'] for x in guards)
receipt={'admin':True,'nativeExecuted':False,'mode':'guarded-candidate' if candidate!=source else 'published-source','baseCommit':base['baseCommit'] if base else None,'baseGuards':guards,'sourceFiles':before,'sourceFilesAfter':after,'sourceUnchangedDuringTest':unchanged,'command':cmd,'exitCode':r.returncode,'passed':r.returncode==0 and unchanged}
(out/'source-linked-receipt.json').write_text(json.dumps(receipt,indent=2),encoding='utf-8');print(r.stdout[-6000:]);print(r.stderr[-2000:]);
if not unchanged:print('Source bytes changed during qualification; this result is not qualified')
sys.exit(r.returncode if r.returncode else (0 if unchanged else 2))
