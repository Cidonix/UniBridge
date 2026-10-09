"""Elevated, project-scoped MCP qualification; only one owned C# fixture is written."""
import argparse
import ctypes
import hashlib
import importlib.util
import json
import os
import time
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--project', required=True, type=Path)
    parser.add_argument('--project-id', required=True)
    parser.add_argument('--relay', required=True, type=Path)
    parser.add_argument('--report', required=True, type=Path)
    args = parser.parse_args()
    if os.name != 'nt' or not ctypes.windll.shell32.IsUserAnAdmin():
        parser.error('Run from an Administrator terminal.')
    suite = Path(__file__).resolve().parent
    spec = importlib.util.spec_from_file_location('mcp', suite.parent/'McpSmokeRegression/run_mcp_smoke_regression.py')
    mcp = importlib.util.module_from_spec(spec); spec.loader.exec_module(mcp)
    args.report.parent.mkdir(parents=True, exist_ok=True)
    results = []
    responses = {}
    fixture_name = 'UniBridgeSemanticHintsFixture' + str(time.time_ns())
    fixture_path = 'Assets/' + fixture_name + '.cs'
    fixture_file = args.project/fixture_path
    source = '''#pragma warning disable 0618
using System;
public class FIXTURE
{
    [Obsolete("Use Modern instead.")] public static int Old(int value) { return value; }
    public static float Modern(int value) { return value; }
    public static int Run() { return Old(1); }
    public static int GetInstanceID() { return 42; }
    // GetInstanceID(); FindObjectsSortMode usedByComposite
}
'''.replace('FIXTURE', fixture_name)
    last_owned_hash = None
    created = False
    def digest(data): return hashlib.sha256(data).hexdigest()
    def check(label, condition, detail=None):
        results.append({'name':label,'passed':bool(condition),'detail':detail})
        if not condition: raise AssertionError(label + ': ' + str(detail))
    def unwrap(result, allow_error=False):
        env = result['data']
        ctx = env.get('projectContext', {})
        check('Project identity: '+str(len(results)), not ctx.get('id') or ctx['id'] == args.project_id)
        if not allow_error and env.get('success') is False:
            raise RuntimeError(env.get('error') or env.get('message') or 'MCP failed')
        return env.get('data', env)
    def advisory(data):
        report = data['obsoleteApiHints']
        check('Available semantic context: '+str(len(results)), report['status'] == 'available', report)
        return report
    def fixture_hint(report):
        return next(h for h in report['hints'] if h['symbol'].endswith('.Old(int)'))
    try:
        check('Fixture path absent', not fixture_file.exists() and not Path(str(fixture_file)+'.meta').exists())
        with mcp.McpClient(args.relay,args.project,120,args.report.with_suffix('.trace.log')) as bridge:
            def call(label, name, arguments, allow_error=False):
                if allow_error:
                    raw = bridge.send('tools/call', {'name':name,'arguments':arguments})['result']
                    payload = next(block['text'] for block in raw.get('content',[]) if block.get('type')=='text')
                    result = {'text':payload,'data':json.loads(payload),'raw':raw}
                    check('Expected MCP rejection: '+label, raw.get('isError') and result['data'].get('success') is False)
                else:
                    result = bridge.call_tool(name,arguments)
                responses[label] = result
                return unwrap(result,allow_error)
            before = call('baseline','UniBridge_ContextSnapshot',{'Depth':'Brief','IncludeSelection':True})
            check('Expected project/package', before['project']['id']==args.project_id and before['package']['version']=='0.2.59')
            check('Idle clean baseline', not any(before['editor'][k] for k in ('isCompiling','isUpdating','isPlaying','hasDirtyScenes')))
            baseline_scenes = {s['path']:digest((args.project/s['path']).read_bytes()) for s in before['scenes']['loaded'] if s.get('path')}
            created_data = call('create','UniBridge_CreateScript',{'Path':fixture_path,'Contents':source,'ScriptType':'PlainCSharp'})
            created = fixture_file.exists()
            check('Created fixture exists', created)
            last_owned_hash = digest(fixture_file.read_bytes())
            report = advisory(created_data)
            hint = fixture_hint(report)
            check('Create reports custom API and changed return type', hint['severity']=='warning' and hint['replacement']['returnTypeChanged'] and hint['replacement']['newReturnType']=='float',hint)
            call('compile-fixture','UniBridge_ManageEditor',{'Action':'RefreshAssets','WaitForCompletion':True,'Force':True})
            basic = call('validate-basic','UniBridge_ValidateScript',{'Uri':fixture_path,'Level':'basic'})
            check('Basic semantic work explicitly skipped', basic['obsoleteApiHints']['status']=='skipped')
            standard = call('validate-standard','UniBridge_ValidateScript',{'Uri':fixture_path,'Level':'standard','IncludeDiagnostics':False})
            report = advisory(standard)
            check('Suppressed compiler warning still gets metadata hint', report['totalHints']==1 and fixture_hint(report)['message']=='Use Modern instead.')
            check('Advisory excluded from validation counts', standard['errors']==0 and standard['warnings']==0 and 'diagnostics' not in standard)
            analyzed = call('analyze','UniBridge_ScriptIntelligence',{'Action':'Analyze','Path':fixture_path,'IncludeSource':False})
            check('Analyze returns one real obsolete issue', advisory(analyzed['script'])['totalHints']==1 and len([i for i in analyzed['script']['issues'] if i['code']=='obsoleteApi'])==1)
            proposed = 'using UnityEngine; public class Probe { public int Check(Object obj) { return obj.GetInstanceID(); } }'
            unity = call('unity-obsolete','UniBridge_ScriptIntelligence',{'Action':'ChangeImpact','Path':fixture_path,'ProposedSource':proposed})
            uh = next(h for h in advisory(unity)['hints'] if h['symbol']=='UnityEngine.Object.GetInstanceID()')
            check('Actual Unity replacement and obsolete back-conversion', uh['replacement']['status']=='resolved' and uh['replacement']['newReturnType']=='UnityEngine.EntityId' and 'obsolete conversion' in uh['replacement']['guidance'],uh)
            modern = proposed.replace('public int Check','public EntityId Check').replace('GetInstanceID','GetEntityId')
            modern_report = advisory(call('unity-modern','UniBridge_ScriptIntelligence',{'Action':'ChangeImpact','Path':fixture_path,'ProposedSource':modern}))
            check('Current Unity API has no obsolete hints', modern_report['totalHints']==0)
            negative = 'public class Probe { public int GetInstanceID(){return 1;} public int Run(){string text="usedByComposite FindObjectsSortMode GetInstanceID()"; return GetInstanceID();} /* GetInstanceID() */ }'
            check('Comments strings and unrelated names have no hints', advisory(call('negative','UniBridge_ScriptIntelligence',{'Action':'ChangeImpact','Path':fixture_path,'ProposedSource':negative}))['totalHints']==0)
            inactive = '#if NEVER_DEFINED_UNIBRIDGE\n'+proposed+'\n#endif\npublic class Probe {}'
            check('Inactive compiler branch ignored', advisory(call('inactive','UniBridge_ScriptIntelligence',{'Action':'ChangeImpact','Path':fixture_path,'ProposedSource':inactive}))['totalHints']==0)
            active = '#if UNITY_EDITOR\n'+proposed+'\n#endif'
            check('Actual Unity editor define respected', advisory(call('active','UniBridge_ScriptIntelligence',{'Action':'ChangeImpact','Path':fixture_path,'ProposedSource':active}))['totalHints']==1)
            preview_args = {'Name':fixture_name,'Path':'Assets','Preview':True,'PreconditionSha256':last_owned_hash,'Options':{'validate':'standard','refresh':'none'},'Edits':[{'op':'replace_method','className':fixture_name,'methodName':'Run','replacement':'public static int Run() { return Old(2); }'}]}
            preview = call('structured-preview','UniBridge_ScriptApplyEdits',preview_args)
            check('Structured preview hints and zero writes', advisory(preview)['totalHints']==1 and preview['editsApplied']==0 and digest(fixture_file.read_bytes())==last_owned_hash)
            preview_args['Edits']=[{'op':'anchor_replace','anchor':r'Old\(1\)','text':'Old(3)'}]
            preview = call('anchor-preview','UniBridge_ScriptApplyEdits',preview_args)
            check('Text anchor preview hints and zero writes', advisory(preview)['totalHints']==1 and preview.get('editsApplied',0)==0 and digest(fixture_file.read_bytes())==last_owned_hash)
            preview_args['Edits']=[{'op':'regex_replace','pattern':r'Old\(1\)','replacement':'Old(4)'}]
            preview = call('regex-preview','UniBridge_ScriptApplyEdits',preview_args)
            check('Regex preview hints and zero writes', advisory(preview)['totalHints']==1 and digest(fixture_file.read_bytes())==last_owned_hash)
            stale = dict(preview_args,PreconditionSha256='0'*64)
            rejected = call('stale-preview','UniBridge_ScriptApplyEdits',stale,True)
            check('Stale source rejects before semantic proposal', 'obsoleteApiHints' not in rejected and digest(fixture_file.read_bytes())==last_owned_hash)
            changed = source.replace('Use Modern instead.','Use Modern instead. Fresh source message.').replace('float Modern','double Modern')
            updated = call('update','UniBridge_Script',{'action':'update','name':fixture_name,'path':'Assets','contents':changed})
            last_owned_hash = digest(fixture_file.read_bytes())
            updated_hint = fixture_hint(advisory(updated))
            check('Updated local source supersedes stale own DLL', updated_hint['message'].endswith('Fresh source message.') and updated_hint['replacement']['newReturnType']=='double',updated_hint)
            call('compile-update','UniBridge_ManageEditor',{'Action':'RefreshAssets','WaitForCompletion':True,'Force':True})
            latest = fixture_hint(advisory(call('validate-update','UniBridge_ValidateScript',{'Uri':fixture_path,'Level':'standard','IncludeDiagnostics':True})))
            check('Post-reload source and references remain current', latest['replacement']['newReturnType']=='double')
            for level in ('comprehensive','strict'):
                check(level+' returns advisory metadata', fixture_hint(advisory(call('validate-'+level,'UniBridge_ValidateScript',{'Uri':fixture_path,'Level':level})))['replacement']['newReturnType']=='double')
            noop = call('text-noop','UniBridge_ApplyTextEdits',{'Uri':fixture_path,'PreconditionSha256':last_owned_hash,'Options':{'validate':'standard'},'Edits':[{'startLine':7,'startCol':1,'endLine':7,'endCol':1,'newText':''}]})
            check('Exact text no-op includes hints', advisory(noop)['totalHints']==1 and noop['applyTextEditsDetailInfo']['no_op'])
            invalid = changed + '\npublic class Invalid { public void Broken( {'
            failed = call('failed-validation','UniBridge_Script',{'action':'update','name':fixture_name,'path':'Assets','contents':invalid},True)
            check('Failed validation retains advisory hints without write', failed['obsoleteApiHints']['status']=='partial' and failed['obsoleteApiHints']['totalHints']==1 and digest(fixture_file.read_bytes())==last_owned_hash)
            disabled = call('disabled','UniBridge_ScriptIntelligence',{'Action':'Analyze','Path':fixture_path,'IncludeObsoleteApiHints':False})
            check('Opt-out reports skipped and removes obsolete issues', disabled['script']['obsoleteApiHints']['status']=='skipped' and not any(i['code']=='obsoleteApi' for i in disabled['script']['issues']))
            hot = call('hotspots','UniBridge_ScriptIntelligence',{'Action':'Hotspots','MaxSemanticScripts':1,'MaxScanScripts':30,'Limit':500})
            check('Bulk budget explicitly reports partial coverage', hot['semanticCoverage']['status']=='partial' and hot['semanticCoverage']['analyzed']==1 and hot['semanticCoverage']['skipped']>0,hot['semanticCoverage'])
            check('Owned fixture content before cleanup', digest(fixture_file.read_bytes())==last_owned_hash)
            call('delete','UniBridge_DeleteScript',{'Uri':fixture_path})
            check('Owned fixture and metadata removed', not fixture_file.exists() and not Path(str(fixture_file)+'.meta').exists())
            created = False
            call('compile-cleanup','UniBridge_ManageEditor',{'Action':'RefreshAssets','WaitForCompletion':True,'Force':True})
            diagnostics = call('final-diagnostics','UniBridge_ManageEditor',{'Action':'GetCompilationDiagnostics'})
            check('Final compile health', diagnostics['compileHealth']['healthy'] and not diagnostics['assemblyFreshness']['staleLikely'],diagnostics.get('summary'))
            after = call('final-state','UniBridge_ContextSnapshot',{'Depth':'Brief','IncludeSelection':True})
            check('Scene selection and author scene bytes preserved', before['scenes']==after['scenes'] and before['selection']==after['selection'] and all(digest((args.project/p).read_bytes())==h for p,h in baseline_scenes.items()))
    except Exception as ex:
        results.append({'name':'qualification exception','passed':False,'detail':repr(ex)})
    finally:
        if created and fixture_file.exists() and digest(fixture_file.read_bytes())==last_owned_hash:
            try:
                with mcp.McpClient(args.relay,args.project,120,args.report.with_suffix('.cleanup.log')) as bridge:
                    responses['cleanup-delete']=bridge.call_tool('UniBridge_DeleteScript',{'Uri':fixture_path})
                    responses['cleanup-refresh']=bridge.call_tool('UniBridge_ManageEditor',{'Action':'RefreshAssets','WaitForCompletion':True,'Force':True})
            except Exception as ex:
                results.append({'name':'owned fixture cleanup','passed':False,'detail':repr(ex)})
        report = {'project':str(args.project),'projectId':args.project_id,'fixturePath':fixture_path,'administrator':True,
                  'passed':sum(x['passed'] for x in results),'failed':sum(not x['passed'] for x in results),'checks':results,'responses':responses,
                  'ownedFixtureRemoved':not fixture_file.exists() and not Path(str(fixture_file)+'.meta').exists()}
        args.report.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
        print(json.dumps({k:report[k] for k in ('passed','failed','ownedFixtureRemoved')}))
    return 0 if report['failed']==0 and report['ownedFixtureRemoved'] else 1


if __name__=='__main__': raise SystemExit(main())
