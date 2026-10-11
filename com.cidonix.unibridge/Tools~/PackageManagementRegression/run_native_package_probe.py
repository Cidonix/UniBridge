"""Opt-in ordinary MCP package gate. Does not launch an Editor or change manifests.

Root first starts the owned loopback registry and prepares the SHA guarded scope.
Add and ImportSample are each issued exactly once. Domain reload recovery is read-only.
"""
import argparse
import ctypes
import hashlib
import importlib.util
import json
import os
import sys
import time
from pathlib import Path


def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()


def _reload_gap(error):
    message = str(error).lower()
    return any(marker in message for marker in (
        'unity connection lost', 'unity connection closed', 'unity not detected',
        'no matching unibridge discovery file', 'unity disconnected',
    ))


def wait_for_editor_ready(client, observations, timeout_seconds=180, clock=time.monotonic, pause=time.sleep):
    """Poll only the known read-only checkpoint; preserve every intermediate outcome."""
    deadline = clock() + timeout_seconds
    while True:
        remaining = deadline - clock()
        if remaining <= 0:
            raise TimeoutError('Editor readiness was not verified within the bounded observation window')
        try:
            reply = client.call_tool('UniBridge_ManageEditor', {
                'Action': 'WaitForReadyAfterReload', 'RequireNotPlaying': True,
                'TimeoutMs': max(1, min(60000, int(remaining * 1000))),
            }, timeout=min(65, remaining), allow_error=True)
        except (RuntimeError, TimeoutError) as error:
            observations.append({'error': repr(error), 'readOnly': True})
            if not isinstance(error, TimeoutError) and not _reload_gap(error):
                raise
        else:
            observations.append({'reply': reply, 'readOnly': True})
            body = reply.get('data')
            if not isinstance(body, dict):
                raise AssertionError('Editor readiness response has no business envelope')
            state = body.get('data') or {}
            if not isinstance(state, dict):
                raise AssertionError('Editor readiness response has malformed state')
            health = state.get('compileHealth') or {}
            build = state.get('buildSystemHealth') or {}
            diagnostics = state.get('compilationDiagnostics') or {}
            if (health.get('hasBuildSystemIssues') is True or build.get('hasCriticalIssues') is True or
                    (build.get('criticalIssueCount') or 0) > 0 or
                    (diagnostics.get('diagnostics', {}).get('errors') or 0) > 0):
                raise AssertionError('Editor compilation/build failure during readiness observation')
            if body.get('success') is True:
                readiness = state.get('readiness') or {}
                if readiness.get('isReady') is True and health.get('healthy') is True:
                    return reply
                if (health.get('healthy') is False and
                        health.get('staleAssemblyLikely') is not True):
                    raise AssertionError('Editor readiness reports unhealthy compilation')
                if 'isReady' not in readiness or 'healthy' not in health:
                    raise AssertionError('Editor readiness lacks positive readiness/compile evidence')
            else:
                # Reload can remove discovery while an earlier checkpoint is in flight.
                # Reissuing this explicit read does not replay Add or ImportSample.
                transient = _reload_gap(body.get('connectionFailure', '')) or (
                    body.get('code') == 'TOOL_CALL_FAILED' and
                    isinstance(body.get('error'), str) and _reload_gap(body['error'])) or (
                    body.get('error') == 'Timed out waiting for Unity editor readiness.' and
                    state.get('completed') is False and state.get('waitSucceeded') is False)
                if not transient or body.get('originalTool', 'UniBridge_ManageEditor') != 'UniBridge_ManageEditor':
                    raise AssertionError('Nontransient Editor readiness failure: ' + str(body))
        pause(min(0.5, max(0, deadline - clock())))


def observe_add_delivery(reply):
    """Keep native completion separate from transport loss; never dispatch a retry."""
    body = reply.get('data') if isinstance(reply, dict) else None
    if not isinstance(body, dict):
        raise AssertionError('Add response has no business/transport envelope')
    state = body.get('data')
    if body.get('success') is True:
        if (not isinstance(state, dict) or state.get('status') != 'native_request_succeeded' or
                state.get('requestCompleted') is not True or
                not isinstance(state.get('editor'), dict) or
                state['editor'].get('readinessRequiresFollowUp') is not True):
            raise AssertionError('Add success lacks native completion and follow-up readiness evidence')
        outcome = 'native_result_verified'
    elif isinstance(state, dict) and body.get('success') is False:
        if state.get('status') in ('completed', 'success', 'native_request_succeeded'):
            raise AssertionError('Failed Add response claims a successful native result')
        outcome = 'business_error'
    elif state is None and body.get('success') is not True and body.get('code') in ('outcome_unknown', 'TOOL_CALL_FAILED'):
        if (body.get('automaticReplay') is True or
                body.get('originalTool', 'UniBridge_ManagePackages') != 'UniBridge_ManagePackages'):
            raise AssertionError('Unexpected Add transport replay/tool envelope')
        outcome = 'outcome_unknown' if body['code'] == 'outcome_unknown' else 'delivery_failed'
    else:
        raise AssertionError('Malformed or unrecognized Add response: ' + str(body))
    return {'outcome': outcome, 'nativeCompletionVerified': outcome == 'native_result_verified',
            'actualInstalledStateRequiresInspection': True, 'replayed': False}


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--repo',type=Path,required=True); parser.add_argument('--project',type=Path,required=True)
    parser.add_argument('--fixture',type=Path,required=True); parser.add_argument('--report',type=Path,required=True)
    args=parser.parse_args(); sys.stdout.reconfigure(encoding='utf-8')
    if os.name!='nt' or not ctypes.windll.shell32.IsUserAnAdmin(): raise SystemExit('Administrator token required')
    if args.project.resolve().name!='UniBridge_Test_Project': raise SystemExit('Only the approved Test project is supported')
    if args.report.exists(): raise SystemExit('Choose a fresh report suffix; do not replay a previous Add/Import')
    fixture=json.loads(args.fixture.read_text(encoding='utf-8-sig'))
    module_path=args.repo/'com.cidonix.unibridge/Tools~/McpSmokeRegression/run_mcp_smoke_regression.py'
    spec=importlib.util.spec_from_file_location('package_native_mcp',module_path); module=importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
    replies={}; checks=[]; add_issued=0; import_issued=0; failure=None
    def check(name,value):
        checks.append({'name':name,'passed':bool(value)})
        if not value: raise AssertionError(name)
    def response(reply): return reply['data']
    def data(reply): return response(reply)['data']
    def call(client,key,tool,arguments):
        reply=client.call_tool(tool,arguments,timeout=120,allow_error=True); replies[key]=reply; return reply
    def ready(client):
        # Failed transient polls are evidence, not terminal failed test assertions.
        observations=[]
        replies.setdefault('editorReadinessObservations',[]).append(observations)
        replies['editorReady']=wait_for_editor_ready(client,observations)
        check('actual-editor-readiness',True)
    try:
        with module.McpClient(args.repo/'com.cidonix.unibridge/RelayApp~/unibridge_relay_win.exe',args.project,120,args.report.with_suffix('.trace.log')) as client:
            ready(client)
            listed=call(client,'boundedList','UniBridge_ManagePackages',{'Action':'List','MaxResults':2})
            check('bounded-native-list',response(listed).get('success') is True and data(listed)['returnedCount']<=2 and len(data(listed)['packages'])<=2)
            inspected=call(client,'inspectUniBridge','UniBridge_ManagePackages',{'Action':'Inspect','PackageName':'com.cidonix.unibridge'})
            check('installed-unibridge-inspection',response(inspected).get('success') is True and data(inspected)['packages'][0]['name']=='com.cidonix.unibridge')
            absent=call(client,'absentPackage','UniBridge_ManagePackages',{'Action':'Inspect','PackageName':fixture['name']+'.absent'})
            check('missing-package-truthful-error',response(absent).get('success') is False and response(absent).get('code')=='PACKAGE_NOT_INSTALLED')
            invalid=call(client,'rangeRejected','UniBridge_ManagePackages',{'Action':'Add','PackageId':fixture['name']+'@^1.2.3'})
            check('range-rejected-before-mutation',response(invalid).get('success') is False and response(invalid).get('code')=='EXACT_REGISTRY_VERSION_REQUIRED' and data(invalid)['mutationAttempted'] is False)
            searched=call(client,'exactRegistrySearch','UniBridge_ManagePackages',{'Action':'Search','Query':fixture['name'],'Offline':False,'MaxResults':2,'TimeoutMs':60000})
            check('local-registry-search-exact-version',response(searched).get('success') is True and any(row['name']==fixture['name'] and row['version']==fixture['version'] for row in data(searched)['packages']))
            before=call(client,'beforeAddInspection','UniBridge_ManagePackages',{'Action':'Inspect','PackageName':fixture['name']})
            check('owned-package-absent-before-sole-add',response(before).get('success') is False and response(before).get('code')=='PACKAGE_NOT_INSTALLED')
            add_issued+=1
            try:
                added=call(client,'addExactOnce','UniBridge_ManagePackages',{'Action':'Add','PackageId':fixture['exactId'],'TimeoutMs':60000})
                delivery=observe_add_delivery(added)
                replies['addDeliveryObservation']=delivery
                if delivery['nativeCompletionVerified']:
                    check('native-add-result-separates-editor-readiness',True)
                elif delivery['outcome']=='business_error':
                    check('native-add-error-never-claims-completed',True)
                # Unknown/failed delivery is retained above, without a completed-success claim.
                # The existing read-only checkpoint and exact installed-state inspection are mandatory.
            except (RuntimeError,TimeoutError) as error:
                replies['addDeliveryInterrupted']={'error':repr(error),'replayed':False}
            ready(client)
            after=call(client,'afterAddInspection','UniBridge_ManagePackages',{'Action':'Inspect','PackageName':fixture['name']})
            check('actual-registry-package-installed-exact-version',response(after).get('success') is True and data(after)['packages'][0]['name']==fixture['name'] and data(after)['packages'][0]['version']==fixture['version'] and data(after)['packages'][0]['source']=='Registry')
            samples=call(client,'listSamples','UniBridge_ManagePackages',{'Action':'ListSamples','PackageName':fixture['name'],'MaxResults':2})
            check('native-sample-list',response(samples).get('success') is True and data(samples)['returnedCount']==1 and data(samples)['samples'][0]['index']==0)
            path=Path(data(samples)['samples'][0]['importPath']); destination=path if path.is_absolute() else args.project/path
            sample_file=destination/'sample.txt'; sample_meta=destination/'sample.txt.meta'
            check('owned-sample-destination-absent',not destination.exists() and not Path(str(destination)+'.meta').exists())
            import_issued+=1
            imported=call(client,'importSampleOnce','UniBridge_ManagePackages',{'Action':'ImportSample','PackageName':fixture['name'],'SampleIndex':0,'TimeoutMs':60000})
            check('native-sample-result-verified',response(imported).get('success') is True and data(imported)['status']=='sample_import_verified' and data(imported)['verifiedFileCount']>=2 and data(imported)['editor']['readinessRequiresFollowUp'] is True)
            check('physical-sample-bytes-and-guid',sha(sample_file)==fixture['files']['Samples~/Demo/sample.txt'] and sha(sample_meta)==fixture['files']['Samples~/Demo/sample.txt.meta'] and fixture['sampleGuid'] in sample_meta.read_text(encoding='utf-8-sig'))
            before_repeat={str(sample_file):sha(sample_file),str(sample_meta):sha(sample_meta)}
            repeat=call(client,'existingSampleBlocked','UniBridge_ManagePackages',{'Action':'ImportSample','PackageName':fixture['name'],'SampleIndex':0})
            check('existing-sample-does-not-overwrite',response(repeat).get('success') is False and response(repeat).get('code') in ('SAMPLE_ALREADY_IMPORTED','SAMPLE_DESTINATION_EXISTS') and all(sha(Path(path))==value for path,value in before_repeat.items()) and data(repeat)['mutationStarted'] is False)
            ready(client)
            diagnostic=call(client,'finalCompilation','UniBridge_ManageEditor',{'Action':'GetCompilationDiagnostics'})
            check('final-native-editor-compilation-success',response(diagnostic).get('success') is True and data(diagnostic)['compileHealth']['healthy'] is True)
    except Exception as error: failure=repr(error)
    args.report.parent.mkdir(parents=True,exist_ok=True)
    result={'passed':failure is None,'administrator':True,'nativeMcpCalls':True,'addIssued':add_issued,'importIssued':import_issued,'mutationReplay':False,'error':failure,'checks':checks,'replies':replies,
            'fixturePath':str(args.fixture.resolve()),'fixtureSha256':sha(args.fixture),'proof':'Ordinary MCP through existing live Editor; no Editor launch. Generated package/sample/manifest cleanup remains explicit and guarded by the root task.'}
    with args.report.open('x',encoding='utf-8') as out: json.dump(result,out,indent=2,ensure_ascii=False)
    print(json.dumps({'passed':failure is None,'checks':len(checks),'error':failure,'addIssued':add_issued,'importIssued':import_issued}))
    return 0 if failure is None else 1


if __name__=='__main__': raise SystemExit(main())
