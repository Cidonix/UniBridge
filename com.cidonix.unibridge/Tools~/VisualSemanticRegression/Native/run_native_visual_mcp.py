"""Opt-in native Mono/MCP gate. Never launches an Editor or replays a mutation."""
import argparse
import ctypes
import hashlib
import importlib.util
import json
import os
import sys
import time
from pathlib import Path

TOOL = 'UniBridge_VisualAssetSearch'
STATE = 'UniBridge_VisualFixtureState'

def native_process_exited(pid):
    kernel=ctypes.WinDLL('kernel32',use_last_error=True)
    kernel.OpenProcess.argtypes=[ctypes.c_uint32,ctypes.c_int,ctypes.c_uint32]
    kernel.OpenProcess.restype=ctypes.c_void_p
    kernel.GetExitCodeProcess.argtypes=[ctypes.c_void_p,ctypes.POINTER(ctypes.c_uint32)]
    kernel.GetExitCodeProcess.restype=ctypes.c_int
    kernel.CloseHandle.argtypes=[ctypes.c_void_p]
    kernel.CloseHandle.restype=ctypes.c_int
    handle=kernel.OpenProcess(0x1000,0,pid)
    if not handle:
        error=ctypes.get_last_error()
        if error==87: return True
        raise OSError(error,'Cannot independently observe admitted child exit.')
    try:
        code=ctypes.c_uint32()
        if not kernel.GetExitCodeProcess(handle,ctypes.byref(code)):
            raise ctypes.WinError(ctypes.get_last_error())
        return code.value!=259
    finally: kernel.CloseHandle(handle)

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--run', action='store_true')
    parser.add_argument('--project', type=Path, required=True)
    parser.add_argument('--relay', type=Path, required=True)
    parser.add_argument('--smoke-module', type=Path, required=True)
    parser.add_argument('--validator-root', type=Path, required=True)
    parser.add_argument('--author-baseline', type=Path)
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    if not args.run: raise SystemExit('Explicit --run is required for owned fixture/import/cache changes.')
    if os.name != 'nt' or not ctypes.windll.shell32.IsUserAnAdmin(): raise SystemExit('Windows administrator token required.')
    if args.project.resolve().name != 'UniBridge_Test_Project': raise SystemExit('Only the authorized Test project is supported.')
    if args.report.exists(): raise SystemExit('Use a fresh report name; mutation requests are never retried.')
    sys.path.insert(0, str(args.validator_root.resolve()))
    from jsonschema import Draft202012Validator
    spec = importlib.util.spec_from_file_location('visual_smoke_client', args.smoke_module)
    smoke = importlib.util.module_from_spec(spec); spec.loader.exec_module(smoke)
    class ModernClient(smoke.McpClient):
        def initialize(self):
            reply = self.send('initialize', {'protocolVersion':'2025-11-25','capabilities':{},
                'clientInfo':{'name':'UniBridge Visual Native Qualification','version':'1.0'}})
            assert reply['result']['protocolVersion'] == '2025-11-25'
            self.send('notifications/initialized', {}, expect_response=False)
    report = {'passed':False,'kind':'actual-Mono6.6-ordinary-MCP-visual-qualification',
        'protocol':'2025-11-25','admin':True,'noMutationReplay':True,'calls':{},'cases':[],'cancellationAttempts':[]}
    def file_sha(path):
        digest=hashlib.sha256()
        with path.open('rb') as source:
            for block in iter(lambda:source.read(1024*1024),b''): digest.update(block)
        return digest.hexdigest()
    if args.author_baseline:
        protected = json.loads(args.author_baseline.read_text(encoding='utf-8-sig'))['UniBridge_Test_Project']
        baseline_mode='providedRootBaseline'
    else:
        protected={}
        for folder in ('Assets','ProjectSettings'):
            for path in (args.project/folder).rglob('*'):
                if path.is_file(): protected[path.relative_to(args.project).as_posix()]=file_sha(path)
        baseline_mode='freshRunnerBaseline'
    def verify_authors():
        errors=[]
        for relative, expected in protected.items():
            path=args.project/relative
            if not path.is_file(): errors.append(relative); continue
            if file_sha(path)!=expected: errors.append(relative)
        if errors: raise AssertionError('Saved author baseline mismatch: '+str(errors[:8]))
        return {'protectedFiles':len(protected),'allOriginalSha256Matched':True}
    def save():
        args.report.parent.mkdir(parents=True,exist_ok=True)
        args.report.write_text(json.dumps(report,indent=2,ensure_ascii=False),encoding='utf-8')
    def check(name, value):
        report['cases'].append({'name':name,'passed':bool(value)}); save()
        if not value: raise AssertionError(name)
    prepared=False; failure=None; schema=None
    with ModernClient(args.relay,args.project,120,args.report.with_suffix('.trace.log')) as client:
        def payload(reply):
            result=reply['raw']; body=reply['data']; structured=result.get('structuredContent')
            if schema is not None and body is not None and body.get('data',{}).get('backend')=='local_onnx_clip_cpu':
                assert isinstance(structured,dict), 'Modern MCP must deliver structuredContent'
                Draft202012Validator(schema).validate(structured)
                without_mirror={k:v for k,v in body.items() if k!='structuredContent'}
                assert without_mirror==structured and body.get('structuredContent',structured)==structured
            return body
        def call(key, action, negative=False, **fields):
            reply=client.call_tool(TOOL,{'Action':action,**fields},timeout=125,allow_error=negative)
            report['calls'][key]=reply; save(); body=payload(reply)
            assert body['success'] is (not negative), body
            if negative: assert reply['raw'].get('isError') is True
            return body['data']
        def observe(observer=client):
            result=observer.call_tool(STATE,{})['data']['data']
            assert result['sceneRetained'] is True, result
            return result
        def menu(name, phase):
            pointer=args.project/'Library/AgentValidation/VisualSemanticNative/latest.json'
            statefile=None
            if pointer.exists(): statefile=Path(json.loads(pointer.read_text(encoding='utf-8-sig'))['statePath'])
            old=statefile.stat().st_mtime_ns if statefile and statefile.exists() else None
            # Each authored/generated-data operation is sent exactly once.
            reply=client.call_tool('UniBridge_ManageMenuItem',{'Action':'Execute','MenuPath':'Tools/UniBridge Validation/Visual Semantic/'+name},allow_error=True)
            report['calls']['menu-'+name+'-'+str(len(report['calls']))]=reply; save()
            assert reply['data'].get('success') is True, reply
            until=time.monotonic()+15
            while time.monotonic()<until:
                if pointer.exists():
                    statefile=Path(json.loads(pointer.read_text(encoding='utf-8-sig'))['statePath'])
                    if statefile.exists() and statefile.stat().st_mtime_ns!=old:
                        value=json.loads(statefile.read_text(encoding='utf-8-sig'))
                        if value['phase']==phase: return value
                time.sleep(.05)
            raise TimeoutError('Fresh owned fixture phase was not observed: '+phase)
        def record(): menu('Record Cache','cache_recorded')
        def scheduler(observer):
            body=observer.call_tool('UniBridge_ExecutionStatus',{'Action':'Snapshot','RecentLimit':100,'IncludeWorkSession':False})['data']
            return smoke.response_payload(body)['scheduler']
        try:
            ready=client.call_tool('UniBridge_ManageEditor',{'Action':'WaitForReadyAfterReload','RequireNotPlaying':True,'TimeoutMs':60000})
            report['calls']['initialReadiness']=ready
            advertised=next(row for row in client.list_tools() if row['name']==TOOL)
            schema=advertised['outputSchema']; Draft202012Validator.check_schema(schema)
            report['advertisedSchema']=advertised
            report['authorBefore']={'baselineMode':baseline_mode,'providedBaselineSha256':file_sha(args.author_baseline) if args.author_baseline else None,
                'protectedFiles':len(protected),'currentFullShaVerification':'performed once after all own cleanup'}; save()
            prepared=True; fixture=menu('Prepare','prepared')
            check('actual-Mono6.6-x64',fixture['processBits']==64 and fixture['unityVersion'].startswith('6000.6') and fixture['monoRuntime'] is True)
            report['fixtureInitial']=fixture; refs=[{'Guid':a['Guid'],'LocalFileId':a['LocalFileId']} for a in fixture['assets']]
            identities={Path(a['path']).stem:a for a in fixture['assets']}
            check('four-owned-opaque-GUID-identities',len(refs)==4 and set(identities)=={'101','102','103','104'})
            status=call('status','Status'); check('offline-model-ready',status['ready'] and status['modelLanguage']=='English')
            check('ordinary-relay-project-scope-injection-accepted',status['ready'])
            installed=call('explicitExistingModelInstall','InstallModel',AllowDownload=True,TimeoutMs=120000)
            check('explicit-idempotent-model-install',installed['ready'])
            call('clearOwnedIndex','ClearIndex'); record()
            first=call('indexPage1','Index',negative=True,AssetReferences=refs,MaxAssets=2,Offset=0,TimeoutMs=30000); record()
            check('bounded-page1-truthful-partial',not first['complete'] and first['processed']==2 and first['nextOffset']==2)
            second=call('indexPage2','Index',AssetReferences=refs,MaxAssets=2,Offset=2,TimeoutMs=30000); record()
            check('bounded-page2-completion',second['complete'] and second['processed']==2 and second['indexedCount']==4)
            canonical=refs[0]['Guid'].lower()
            check('uppercase-reference-contains-case-difference',canonical.upper()!=canonical)
            mixed=[{'Guid':canonical.upper(),'LocalFileId':refs[0]['LocalFileId']},{'Guid':canonical,'LocalFileId':refs[0]['LocalFileId']}]
            normalized=call('uppercaseGuidCanonicalIndex','Index',AssetReferences=mixed,MaxAssets=2,TimeoutMs=30000); record()
            check('uppercase-GUID-canonical-identity-deduplicated',normalized['complete'] and normalized['processed']==1 and normalized['indexedCount']==4 and len(normalized['results'])==1 and normalized['results'][0]['guid']==canonical)
            index_path=args.project/'Library/UniBridge/VisualAssetSearch/index-v1.json'
            for name,fields in [('objectRefs',{'AssetReferences':{'Guid':canonical}}),('stringRefs',{'AssetReferences':canonical}),
                    ('nullRefs',{'AssetReferences':None}),('tooManyRefs',{'AssetReferences':[refs[0]]*101}),
                    ('nonObjectRef',{'AssetReferences':[canonical]}),('badRefGuid',{'AssetReferences':[{'Guid':'invalid'}]}),
                    ('whitespaceGuid',{'AssetReferences':[{'Guid':' '+canonical+' '}]}),
                    ('stringLocalId',{'AssetReferences':[{'Guid':canonical,'LocalFileId':str(refs[0]['LocalFileId'])}]}),
                    ('outOfRangeMaxAssets',{'AssetReferences':refs,'MaxAssets':101}),('fractionalOffset',{'AssetReferences':refs,'Offset':.5}),
                    ('stringDownloadFlag',{'AssetReferences':refs,'AllowDownload':'false'}),
                    ('unknownBusinessKey',{'AssetReferences':refs,'UnknownBusinessKey':True}),
                    ('unknownReservedPrefix',{'AssetReferences':refs,'__unibridge_not_a_transport_field':True})]:
                before_hash=file_sha(index_path)
                invalid=call('invalidInput-'+name,'Index',negative=True,**fields)
                check('invalid-'+name+'-no-default-enumeration-or-cache-write',not invalid['complete'] and invalid['processed']==0 and invalid['results']==[] and file_sha(index_path)==before_hash)
            states=observe(); report['afterCapture']=states
            check('actual-preview-main-thread-cleanup',states['lastCaptureThreadId']==states['mainThreadId'] and states['renderTextureRestored'] and states['textureDestroyed'] and states['temporaryReleased'])
            for query,name in [('a drawing of a red car','101'),('a drawing of a green tree','102'),('a drawing of a red apple','103'),('a drawing of a yellow banana','104')]:
                result=call('query-'+name,'Search',Query=query,Limit=4)
                check('actual-text-to-image-'+name,result['complete'] and result['results'][0]['guid']==identities[name]['Guid'])
                check('typed-result-metadata-'+name,all(row['localFileId']!=0 and len(row['sourceSha256'])==64 and row['path'].startswith(fixture['folder']+'/') for row in result['results']))
            filtered=call('noHighScore','Search',Query='a drawing of a red car',Limit=1,MinScore=.99)
            check('cosine-threshold-is-not-probability',filtered['complete'] and filtered['results']==[])
            limited=call('limited','Search',Query='a drawing of a red car',Limit=1); check('ranking-limit',len(limited['results'])==1)
            call('missingQuery','Search',negative=True)
            unknown=call('missingGuid','Index',negative=True,AssetReferences=[{'Guid':'f'*32}],MaxAssets=1); record()
            check('missing-GUID-truthful-error',not unknown['complete'] and unknown['processed']==0 and len(unknown['failures'])==1)
            menu('Hide Worker','worker_hidden')
            inactive=call('providerUnavailable','Status'); check('inactive-without-touching-user-models',not inactive['ready'] and inactive['inactiveReason']=='worker_unavailable')
            call('unreadySearch','Search',negative=True,Query='a red car')
            menu('Restore Worker','worker_restored')
            changed=menu('Swap First','first_changed')
            stale=call('changedSourceStale','Search',negative=True,Query='a red car')
            check('changed-source-excluded',stale['staleCount']==1 and all(r['guid']!=identities['101']['Guid'] for r in stale['results']))
            reindexed=call('reindexChangedGuid','Index',AssetReferences=[refs[0]],MaxAssets=1); record()
            check('same-GUID-new-source-SHA',reindexed['results'][0]['guid']==identities['101']['Guid'] and reindexed['results'][0]['sourceSha256']!=identities['101']['sourceSha256'])
            current=call('updatedQuery','Search',Query='a yellow banana',Limit=4)
            check('updated-content-embedding-used',any(r['guid']==identities['101']['Guid'] and r['score']>.3 for r in current['results']))
            menu('Delete Second','second_deleted')
            deleted=call('deletedStale','Search',negative=True,Query='a green tree')
            check('deleted-GUID-excluded',deleted['staleCount']==1 and all(r['guid']!=identities['102']['Guid'] for r in deleted['results']))
            replacement=menu('Replace Second Identity','second_replaced')['replacementIdentity']
            report['replacementIdentity']=replacement
            check('native-replacement-hashes-and-local-ID-forced-current',replacement['oldGuid']==identities['102']['Guid'] and replacement['actualGuid']!=replacement['oldGuid'] and replacement['forcedCurrentHashes'])
            check('native-identity-predicate-rejects-old-GUID-only',replacement['newNativeIdentityAccepted'] and not replacement['oldNativeIdentityAccepted'])
            report['nativeStaleOldGuidPathObserved']=replacement['oldGuidLookup']==replacement['replacementPath']
            aliased=call('oldGuidCannotBindReplacement','Search',negative=True,Query='a green tree')
            check('deleted-GUID-cannot-alias-same-path-replacement',aliased['staleCount']==1 and all(r['guid'] not in (replacement['oldGuid'],replacement['actualGuid']) for r in aliased['results']))
            menu('Delete Replacement Second','replacement_deleted')
            menu('Restore Second','second_restored')
            call('restoreDeletedIdentityIndex','Index',AssetReferences=[refs[1]],MaxAssets=1); record()
            menu('Hold Gate','gate_held')
            timed=call('deadline','Search',negative=True,Query='a red car',TimeoutMs=100)
            check('deadline-preserves-foreign-gate-owner',not timed['complete'] and observe()['gateHeld'])
            menu('Release Gate','gate_released')
            # Separate caller and observer, prove Editor admission AND an actual child before cancel.
            canceled=False
            with ModernClient(args.relay,args.project,120) as caller, ModernClient(args.relay,args.project,120) as observer:
                for attempt in range(3):
                    before=scheduler(observer); previous={row['operationId'] for key in ('activeOperations','recentOperations') for row in before.get(key,[])}
                    request=caller.start_request('tools/call',{'name':TOOL,'arguments':{'Action':'Search','Query':'a drawing of a red apple','Limit':4,'TimeoutMs':30000}})
                    end=time.monotonic()+8; admitted=None; child=None
                    while time.monotonic()<end:
                        snapshot=scheduler(observer)
                        active=[r for r in snapshot.get('activeOperations',[]) if r.get('tool')==TOOL and r.get('operationId') not in previous and r.get('outcome')=='running' and r.get('startedUtc')]
                        state=observe(observer)
                        if len(active)==1 and state['activeWorkerPid']>0: admitted=active[0]; child=state['activeWorkerPid']; break
                        if any(r.get('tool')==TOOL and r.get('operationId') not in previous for r in snapshot.get('recentOperations',[])): break
                        time.sleep(.02)
                    report['cancellationAttempts'].append({'attempt':attempt,'admitted':admitted,'actualWorkerPid':child})
                    if admitted is None:
                        caller.wait_for_response(request,15,allow_error=True)
                        # Only this explicit read may be repeated if it completed before cancellation observation.
                        continue
                    caller.cancel(request); caller.assert_no_response(request,.3)
                    end=time.monotonic()+10; finished=None
                    while time.monotonic()<end:
                        snapshot=scheduler(observer); state=observe(observer)
                        row=next((r for r in snapshot.get('recentOperations',[]) if r.get('operationId')==admitted['operationId']),None)
                        if row and row.get('outcome')=='canceled' and state['activeWorkerPid']==0 and state['gateAvailable']==1:
                            assert row.get('forcedRelease') is None; finished=row; break
                        time.sleep(.05)
                    check('admitted-real-child-cancel-and-release',finished is not None); report['canceledOperation']=finished; canceled=True; break
                if canceled: check('actual-native-child-physically-exited',native_process_exited(child))
            check('actual-child-cancel-proven',canceled)
            follow=call('followupAfterCancellation','Search',Query='a green tree',Limit=1)
            check('followup-search-after-cancel',follow['complete'] and len(follow['results'])==1)
            menu('Corrupt Cache','cache_corrupted')
            call('corruptCacheError','Status',negative=True)
            call('clearCorruptCache','ClearIndex'); record()
            empty=call('emptyIndexError','Search',negative=True,Query='a green tree')
            check('corrupt-index-clear-usable',not empty['complete'] and empty['indexedCount']==0)
        except Exception as error:
            failure=repr(error); report['error']=failure
        finally:
            if prepared:
                try:
                    report['fixtureFinal']=menu('Finish','finished'); prepared=False
                    check('own-assets-cache-restored-and-scene-retained',report['fixtureFinal']['sceneRetained'] and report['fixtureFinal']['assets']==[])
                except Exception as cleanup:
                    report['cleanupError']=repr(cleanup)
            try: report['authorFinal']=verify_authors()
            except Exception as guard: report['authorPreservationError']=repr(guard)
            report['passed']=failure is None and 'cleanupError' not in report and 'authorPreservationError' not in report and all(c['passed'] for c in report['cases']); save()
    print(json.dumps({'passed':report['passed'],'cases':len(report['cases']),'report':str(args.report),'error':report.get('error'),'cleanupError':report.get('cleanupError')}))
    if not report['passed']: raise SystemExit(1)

if __name__=='__main__': main()
