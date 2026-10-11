"""Native ordinary MCP gate for an already begun owned fixture; never launches Unity."""
import argparse, ctypes, hashlib, importlib.util, json, os, sys
from pathlib import Path

def main():
    p=argparse.ArgumentParser()
    p.add_argument('--repo',type=Path,required=True); p.add_argument('--project',type=Path,required=True)
    p.add_argument('--native-report',type=Path,required=True); p.add_argument('--report',type=Path,required=True)
    a=p.parse_args(); sys.stdout.reconfigure(encoding='utf-8')
    if os.name!='nt' or not ctypes.windll.shell32.IsUserAnAdmin(): raise SystemExit('Administrator token required')
    if a.project.resolve().name!='UniBridge_Test_Project': raise SystemExit('Only approved Test project')
    native=a.native_report.resolve()
    if native.name!='native-report.json' or not native.parent.name.startswith('unibridge-reflection-native-'): raise SystemExit('Expected UUID-owned native report')
    if a.report.exists(): raise SystemExit('Fresh report suffix required; no mutation replay')
    state=json.loads(native.read_text(encoding='utf-8-sig'))
    if state['next']!=0 or state.get('closed'): raise SystemExit('Already consumed fixture; create a fresh owned run')
    source=a.repo/'com.cidonix.unibridge/Tools~/McpSmokeRegression/run_mcp_smoke_regression.py'
    spec=importlib.util.spec_from_file_location('reflection_mcp',source); m=importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
    result={'administrator':True,'nativeReport':str(native),'mutationIssued':0,'cases':[],'failure':None,'runnerSha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest()}
    with m.McpClient(a.repo/'com.cidonix.unibridge/RelayApp~/unibridge_relay_win.exe',a.project,60,a.report.with_suffix('.trace.log')) as client:
        try:
            for index,case in enumerate(state['cases']):
                if Path(case['wirePath']).exists(): raise AssertionError('Wire evidence exists; no replay')
                result['mutationIssued']+=1
                reply=client.call_tool('UniBridge_ManageGameObject',case['payload'],allow_error=True)
                with Path(case['wirePath']).open('x',encoding='utf-8') as f: json.dump(reply['raw'],f,ensure_ascii=False,indent=2)
                result['cases'].append({'name':case['name'],'reply':reply,'issuedOnce':True})
                verified=client.call_tool('UniBridge_ManageMenuItem',{'Action':'Execute','MenuPath':'Tools/UniBridge Qualification/Reflection Paths/Verify'})
                result['cases'][-1]['verify']=verified
                current=json.loads(native.read_text(encoding='utf-8-sig'))
                if current['next']!=index+1 or current['checks'][-1]['passed'] is not True:
                    raise AssertionError('Native physical/wire case failed: '+case['name'])
        except Exception as error:
            result['failure']=repr(error)
        finally:
            try:
                result['finish']=client.call_tool('UniBridge_ManageMenuItem',{'Action':'Execute','MenuPath':'Tools/UniBridge Qualification/Reflection Paths/Finish'},allow_error=True)
            except Exception as error: result['finishFailure']=repr(error)
    result['native']=json.loads(native.read_text(encoding='utf-8-sig'))
    result['passed']=result['failure'] is None and result['native'].get('closed') is True and result['native'].get('passed') is True
    a.report.parent.mkdir(parents=True,exist_ok=True)
    with a.report.open('x',encoding='utf-8') as f: json.dump(result,f,ensure_ascii=False,indent=2)
    print('PASS' if result['passed'] else 'FAIL',a.report)
    return 0 if result['passed'] else 1

if __name__=='__main__': raise SystemExit(main())
