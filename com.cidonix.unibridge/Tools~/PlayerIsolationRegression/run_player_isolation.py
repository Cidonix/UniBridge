"""Create an owned disposable project and build a real Windows x64 Mono Player.
Never opens or modifies an author's Unity project. Requires elevated Windows.
"""
import argparse, ctypes, hashlib, json, shutil, subprocess, time
from pathlib import Path

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--package',type=Path,required=True)
    parser.add_argument('--project',type=Path,required=True)
    parser.add_argument('--editor',type=Path,default=Path('C:/Program Files/Unity/Hub/Editor/6000.6.5f1/Editor/Unity.exe'))
    parser.add_argument('--expect-excluded',action='store_true')
    args=parser.parse_args()
    assert ctypes.windll.shell32.IsUserAnAdmin(), 'Administrator required'
    assert not args.project.exists(), 'Fresh owned project required'
    p=args.project.resolve(); (p/'Assets/Editor').mkdir(parents=True); (p/'Packages').mkdir(); (p/'ProjectSettings').mkdir()
    version=args.editor.parent.parent.name
    (p/'ProjectSettings/ProjectVersion.txt').write_text('m_EditorVersion: '+version+'\n',encoding='utf8')
    shutil.copytree(args.package,p/'Packages/com.cidonix.unibridge',ignore=shutil.ignore_patterns('__pycache__','.vs','.idea'))
    dependencies=json.loads((args.package/'package.json').read_text())['dependencies']
    dependencies=dict(dependencies,**{'com.cidonix.unibridge':'file:com.cidonix.unibridge','com.unity.modules.animation':'1.0.0','com.unity.modules.audio':'1.0.0','com.unity.modules.imgui':'1.0.0','com.unity.modules.imageconversion':'1.0.0','com.unity.modules.jsonserialize':'1.0.0','com.unity.modules.particlesystem':'1.0.0','com.unity.modules.screencapture':'1.0.0','com.unity.modules.assetbundle':'1.0.0','com.unity.modules.vehicles':'1.0.0','com.unity.modules.terrain':'1.0.0','com.unity.modules.terrainphysics':'1.0.0','com.unity.modules.video':'1.0.0'})
    (p/'Packages/manifest.json').write_text(json.dumps({'dependencies':dependencies},indent=2),encoding='utf8')
    shutil.copyfile(Path(__file__).with_name('PlayerIsolationGate.cs'),p/'Assets/Editor/PlayerIsolationGate.cs')
    (p/'PlayerIsolation_context.md').write_text('Owned disposable UniBridge Player isolation qualification project; no authored game data. Windows x64 Mono build, administrator inherited.\n',encoding='utf8')
    if args.expect_excluded:(p/'expect-editor-only').touch()
    startup=subprocess.STARTUPINFO();startup.dwFlags|=subprocess.STARTF_USESHOWWINDOW;startup.wShowWindow=0
    start=time.time();proc=subprocess.Popen([str(args.editor),'-batchmode','-nographics','-projectPath',str(p),'-executeMethod','UniBridgePlayerIsolationGate.Build','-logFile',str(p/'editor.log')],startupinfo=startup)
    print(json.dumps({'ownedPid':proc.pid,'ownedProject':str(p),'administratorInherited':True}),flush=True)
    try:code=proc.wait(timeout=600)
    except subprocess.TimeoutExpired:raise SystemExit('Owned Unity Player build timeout; PID '+str(proc.pid)+' retained for verified cleanup')
    resultpath=p/'GateOutput/result.json'
    result=json.loads(resultpath.read_text()) if resultpath.is_file() else {'passed':False,'error':'Native qualification receipt missing; inspect editor.log'}
    result.update(administratorInherited=True,exitCode=code,elapsedSeconds=round(time.time()-start,2),packageVersion=json.loads((p/'Packages/com.cidonix.unibridge/package.json').read_text())['version'],sourceAsmdefs={str(x.relative_to(p/'Packages/com.cidonix.unibridge')):hashlib.sha256(x.read_bytes()).hexdigest() for x in (p/'Packages/com.cidonix.unibridge').rglob('*.asmdef')})
    (p/'driver-result.json').write_text(json.dumps(result,indent=2),encoding='utf8')
    print(json.dumps(result));return 0 if result['passed'] and code==0 else 1
if __name__=='__main__':raise SystemExit(main())
