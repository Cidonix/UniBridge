"""Ordinary MCP positive/negative and independent output-schema gate; no Editor launch/mutation."""
import argparse, ctypes, importlib.util, json, os, sys
from pathlib import Path

def main():
    p=argparse.ArgumentParser();p.add_argument('--repo',type=Path,default=Path(__file__).resolve().parents[3]);p.add_argument('--project',type=Path,required=True)
    p.add_argument('--native-result',type=Path,required=True);p.add_argument('--validator-path',type=Path);p.add_argument('--report',type=Path,required=True)
    a=p.parse_args();assert os.name=='nt' and ctypes.windll.shell32.IsUserAnAdmin(), 'Administrator Windows shell required'
    assert not a.report.exists();a.report.parent.mkdir(parents=True,exist_ok=True)
    if a.validator_path:sys.path.insert(0,str(a.validator_path.resolve(strict=True)))
    from jsonschema import Draft202012Validator
    native=json.loads(a.native_result.read_text(encoding='utf-8-sig'));assert native['passed'] and native['processBits']==64
    expected=json.loads((a.native_result.parent/'production-exact-quality.json').read_text(encoding='utf-8-sig'))['data']['properties'][0]['value']
    source=a.repo/'com.cidonix.unibridge/Tools~/McpSmokeRegression/run_mcp_smoke_regression.py'
    spec=importlib.util.spec_from_file_location('mcp',source);m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
    class ModernClient(m.McpClient):
        def initialize(self):
            negotiated=self.send('initialize',{'protocolVersion':'2025-11-25','capabilities':{},'clientInfo':{'name':'UniBridge ProjectSettings qualification','version':'1.0'}})
            assert negotiated.get('result',{}).get('protocolVersion')=='2025-11-25'
            self.send('notifications/initialized',{},expect_response=False)
    cases=[];calls=[];failure=None;info=None
    def check(name,condition,detail=None):
        cases.append({'name':name,'passed':bool(condition),'detail':None if condition else detail});assert condition,(name,detail)
    try:
        with ModernClient(a.repo/'com.cidonix.unibridge/RelayApp~/unibridge_relay_win.exe',a.project,90) as c:
            ping=c.call_tool('UniBridge_Discover',{'Action':'Ping'})
            check('Exact target project identity',ping['data']['success'] and Path(ping['data']['data']['unity']['projectPath']).resolve()==a.project.resolve())
            info=next(t for t in c.list_tools() if t['name']=='UniBridge_ProjectSettings')
            schema=info['outputSchema'];Draft202012Validator.check_schema(schema);validator=Draft202012Validator(schema)
            check('Advertised read-only tool and closed output contract',info['annotations'].get('uniBridgeExecution',{}).get('readOnly') is True and 'AuthoredJsonValue' in schema.get('$defs',{}))
            def call(label,args,error=None):
                r=c.call_tool('UniBridge_ProjectSettings',args,allow_error=True);calls.append({'label':label,'args':args,'reply':r})
                business=dict(r['data']); mirror=business.pop('structuredContent',None);structured=r['raw'].get('structuredContent')
                errors=list(validator.iter_errors(structured))
                check(label+' text/structured/schema',structured==business and (mirror is None or mirror==structured) and not errors,
                      {'schemaErrors':[e.message for e in errors],'businessMatches':structured==business,'mirrorMatches':mirror is None or mirror==structured})
                if error:check(label,r['raw'].get('isError') is True and r['data'].get('success') is False and r['data'].get('code')==error,r)
                else:check(label,r['raw'].get('isError') is not True and r['data'].get('success') is True,r)
                return r['data'].get('data')
            catalog=call('List actual native settings',{})
            check('QualitySettings available in actual catalog',any(x['name']=='QualitySettings' and x['available'] for x in catalog['settings']))
            bounded=call('List cap',{'MaxSettings':1})
            check('List result cap truthful',len(bounded['settings'])==1 and bounded['complete'] is False and 'settings_limit' in bounded['limitations'])
            scalar=call('Quality scalar',{'Action':'Inspect','Name':'QualitySettings','PropertyPath':'m_CurrentQuality','MaxDepth':0})
            check('Value matches independent native control',scalar['complete'] and len(scalar['properties'])==1 and scalar['properties'][0]['value']==expected)
            capped=call('Quality array cap',{'Action':'Inspect','Name':'QualitySettings','MaxArrayElements':0})
            check('Array omissions declared',not capped['complete'] and 'array_limit' in capped['limitations'])
            alias=call('PlayerSettings alias',{'Action':'Inspect','Name':'PlayerSettings','MaxProperties':1})
            check('Alias points to canonical system file',alias['sourcePath']=='ProjectSettings/ProjectSettings.asset')
            omitted=call('Do not read values',{'Action':'Inspect','Name':'QualitySettings','PropertyPath':'m_CurrentQuality','IncludeValues':False})
            check('Omitted value remains explicit',omitted['properties'][0]['value'] is None and omitted['properties'][0]['valueStatus']=='not_requested')
            call('Missing settings',{'Action':'Inspect','Name':'MissingUniBridgeSettings20261012'},'SETTINGS_NOT_FOUND')
            call('Missing property',{'Action':'Inspect','Name':'QualitySettings','PropertyPath':'NoSuchProperty'},'SETTINGS_PROPERTY_NOT_FOUND')
            call('Path traversal',{'Action':'Inspect','Name':'../QualitySettings'},'INVALID_SETTINGS_NAME')
            call('Invalid index syntax',{'Action':'Inspect','Name':'QualitySettings','PropertyPath':'m_QualitySettings[-1]'},'INVALID_PROPERTY_PATH')
            call('Invalid observation budget',{'Action':'Inspect','Name':'QualitySettings','MaxProperties':513},'INVALID_SETTINGS_LIMIT')
    except Exception as e:failure=repr(e)
    result={'passed':failure is None,'administrator':True,'nativeControl':str(a.native_result),'cases':cases,'calls':calls,'toolInfo':info,'failure':failure,'mutationIssued':0}
    with a.report.open('x',encoding='utf-8') as f:json.dump(result,f,indent=2,ensure_ascii=False)
    print(json.dumps({'passed':result['passed'],'checks':len(cases),'calls':len(calls),'failure':failure}))
    return 0 if result['passed'] else 1
if __name__=='__main__':raise SystemExit(main())
