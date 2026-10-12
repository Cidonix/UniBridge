"""Ordinary project-scoped MCP capture load/restore and bottom-up gate; launch relay as inherited Administrator only."""
import argparse,ctypes,hashlib,importlib.util,json,os,sys,time
from pathlib import Path

def ensure(value,message):
    if not value:raise AssertionError(message)

def sha(path):return hashlib.sha256(Path(path).read_bytes()).hexdigest()

def validate_business(result,expect=None,allow_partial=False):
    body=result.get('data');ensure(isinstance(body,dict),'No business result')
    is_error=result.get('raw',{}).get('isError') is True
    if expect is not None:
        ensure(body.get('success') is False and body.get('code')==expect and is_error,'Expected negative outcome: '+expect)
    elif body.get('success') is False and allow_partial:
        ensure(is_error and body.get('code')=='RECORDED_PROFILER_DATA_PARTIAL','Unexpected scouting failure')
        data=body.get('data');ensure(isinstance(data,dict) and data.get('outcome')=='partial' and data.get('complete') is False,'Scouting partial lacks truthful coverage')
    else:
        ensure(body.get('success') is True and not is_error,'Successful outcome contradicted MCP')
    return body.get('data')

def wait_after_reload(call_ready,record_attempt=lambda value:None,budget_seconds=120,clock=time.monotonic,pause=time.sleep):
    """Retry only the read-only readiness checkpoint while discovery reconnects after reload."""
    deadline=clock()+budget_seconds
    attempts=0
    last_reason='No readiness response'
    while clock()<deadline:
        remaining=deadline-clock()
        attempts+=1
        # Reserve transport headroom without allowing one wait to overrun the total budget.
        timeout=min(20,remaining)
        arguments={'Action':'WaitForReadyAfterReload','TimeoutMs':max(100,min(15000,int(max(.1,timeout-1)*1000))),'PollIntervalMs':250,'RequireNotPlaying':True}
        attempt={'attempt':attempts,'arguments':arguments}
        try:
            result=call_ready(arguments,timeout)
            body=result.get('data')
            attempt['result']=body
            attempt['isError']=result.get('raw',{}).get('isError',False)
            data=body.get('data') if isinstance(body,dict) else None
            # Actual WaitForReadyAfterReload response: the readiness wait is nested in waitResult.
            wait_result=data.get('waitResult') if isinstance(data,dict) else None
            waited=wait_result.get('data') if isinstance(wait_result,dict) else None
            readiness=waited.get('readiness') if isinstance(waited,dict) else None
            compile_health=data.get('compileHealth') if isinstance(data,dict) else None
            ready=(isinstance(wait_result,dict) and wait_result.get('success') is True and
                   isinstance(readiness,dict) and readiness.get('isReady') is True and
                   isinstance(compile_health,dict) and compile_health.get('healthy') is True)
            if isinstance(body,dict) and body.get('success') is True and not attempt['isError'] and ready:
                attempt['ready']=True;record_attempt(attempt)
                return data
            last_reason=str(body)[:900]
        except Exception as error:
            # A disconnected relay/discovery request is safe to retry here: this is the only read operation.
            last_reason=str(error)[:900];attempt['error']=last_reason
        record_attempt(attempt)
        remaining=deadline-clock()
        if remaining>0:pause(min(.5,remaining))
    raise AssertionError('Editor did not confirm WaitForReadyAfterReload readiness within '+str(budget_seconds)+'s: '+last_reason)

def owned_loaded_lease(result,arguments,expected_load):
    """Retain only our positively acknowledged native mutation before wire/schema assertions."""
    body=result.get('data')
    if arguments.get('Action')!='LoadCapture' or not isinstance(body,dict) or body.get('success') is not True:return None
    data=body.get('data');ensure(isinstance(data,dict),'Loaded capture reply omitted lease data')
    expected_root=Path(expected_load['ExpectedProjectRoot']).resolve()
    ensure(isinstance(arguments.get('ExpectedProjectRoot'),str) and Path(arguments['ExpectedProjectRoot']).resolve()==expected_root,'Load request project root differs from owned fixture')
    ensure(isinstance(data.get('projectRoot'),str) and Path(data['projectRoot']).resolve()==expected_root,'Loaded capture reply belongs to another project')
    expected_path=expected_load['CapturePath'].replace('\\','/')
    ensure(arguments.get('CapturePath','').replace('\\','/')==expected_path and data.get('capturePath')==expected_path,'Loaded capture reply differs from owned capture path')
    expected_sha=expected_load['CaptureSha256'].lower()
    ensure(len(expected_sha)==64 and all(c in '0123456789abcdef' for c in expected_sha),'Owned fixture capture SHA is invalid')
    ensure(arguments.get('CaptureSha256','').lower()==expected_sha and data.get('captureSha256')==expected_sha,'Loaded capture reply differs from owned capture hash')
    lease_id=data.get('leaseId')
    ensure(isinstance(lease_id,str) and len(lease_id)==32 and all(c in '0123456789abcdef' for c in lease_id),'Loaded capture reply has an invalid lease identity')
    ensure(data.get('schema')=='unibridge.runtimeProfiler.captureLease.v1' and data.get('state')=='loaded' and data.get('recordingEnabled') is False,'Loaded capture reply is not a stopped owned lease')
    return dict(data)

def validate_wire(result,validator):
    body=result.get('data');ensure(isinstance(body,dict),'No business result')
    projection=dict(body);mirror=projection.pop('structuredContent',None)
    structured=result.get('raw',{}).get('structuredContent')
    ensure(isinstance(structured,dict),'Modern MCP response omitted structuredContent')
    ensure(structured==projection and (mirror is None or mirror==structured),'Text/structured payload mismatch')
    errors=list(validator.iter_errors(structured))
    ensure(not errors,'Registered profiler outputSchema rejected payload: '+'; '.join(error.message for error in errors[:4]))

def modern_client_type(smoke):
    class ModernClient(smoke.McpClient):
        def initialize(self):
            negotiated=self.send('initialize',{'protocolVersion':'2025-11-25','capabilities':{},'clientInfo':{'name':'UniBridge Profiler Extensions Regression','version':'1.0'}})
            ensure(negotiated.get('result',{}).get('protocolVersion')=='2025-11-25','Modern MCP negotiation failed')
            self.send('notifications/initialized',{},expect_response=False)
    return ModernClient

def main():
    p=argparse.ArgumentParser();p.add_argument('--repo',type=Path,required=True);p.add_argument('--relay',type=Path,required=True);p.add_argument('--project',type=Path,required=True)
    p.add_argument('--inputs',type=Path,required=True);p.add_argument('--report',type=Path,required=True);p.add_argument('--step',choices=('2','3'),required=True);p.add_argument('--domain-reload',action='store_true');p.add_argument('--validator-path',type=Path,help='Optional installed jsonschema dependency directory compatible with this Python.')
    a=p.parse_args()
    if os.name!='nt' or not ctypes.windll.shell32.IsUserAnAdmin():raise SystemExit('Administrator Windows process required')
    ensure(not a.report.exists(),'Fresh report path required')
    source=a.repo/'com.cidonix.unibridge/Tools~/McpSmokeRegression/run_mcp_smoke_regression.py'
    spec=importlib.util.spec_from_file_location('profiler_extensions_smoke',source);smoke=importlib.util.module_from_spec(spec);spec.loader.exec_module(smoke)
    Client=modern_client_type(smoke)
    if a.validator_path:sys.path.insert(0,str(a.validator_path.resolve(strict=True)))
    from jsonschema import Draft202012Validator
    inputs=json.loads(a.inputs.read_text(encoding='utf-8-sig'));ensure(inputs.get('passed') is True,'Native fixture did not pass')
    ensure(Path(inputs['expectedProjectRoot']).resolve()==a.project.resolve(),'Fixture belongs to a different project')
    ensure(inputs['unityVersion'].startswith('6000.6.') and inputs['processBits']==64,'Mono6.6 x64 native gate required')
    ensure(sha(a.project/inputs['capturePath'])==inputs['captureSha256'],'Known capture was changed')
    report={'passed':False,'step':a.step,'administrator':True,'inputs':str(a.inputs),'calls':[],'checks':[],'restorationVerified':False,'protocolVersion':'2025-11-25','schemaValidatedResponses':0,'readinessAttempts':[]}
    a.report.parent.mkdir(parents=True,exist_ok=True)
    def save():a.report.write_text(json.dumps(report,indent=2),encoding='utf-8')
    validator=None
    def call(client,arguments,expect=None,allow_partial=False):
        nonlocal lease
        result=client.call_tool('UniBridge_RuntimeProfiler',arguments,timeout=45,allow_error=expect is not None or allow_partial)
        body=result.get('data');ensure(isinstance(body,dict),'No business result')
        report['calls'].append({'arguments':arguments,'result':body,'structuredContent':result['raw'].get('structuredContent'),'isError':result['raw'].get('isError',False)});save()
        owned=owned_loaded_lease(result,arguments,load)
        if owned is not None:
            lease=owned;report['leaseId']=owned['leaseId'];report['leaseRegisteredBeforeValidation']=True;save()
        validate_wire(result,validator);report['schemaValidatedResponses']+=1;save()
        return validate_business(result,expect,allow_partial)
    root=str(a.project.resolve());load={'Action':'LoadCapture','ExpectedProjectRoot':root,'CapturePath':inputs['capturePath'],'CaptureSha256':inputs['captureSha256']}
    lease=None
    try:
        with Client(a.relay,a.project,45) as client:
            client.initialize()
            info=next(tool for tool in client.list_tools() if tool['name']=='UniBridge_RuntimeProfiler')
            schema=info['outputSchema'];Draft202012Validator.check_schema(schema);validator=Draft202012Validator(schema)
            report['outputSchema']=schema;report['outputSchemaSha256']=hashlib.sha256(json.dumps(schema,sort_keys=True,separators=(',',':')).encode()).hexdigest();save()
            status=call(client,{'Action':'GetCaptureLease'});ensure(status['state']=='none','Existing owned lease must be handled explicitly before this gate')
            call(client,{**load,'CaptureSha256':'0'*64},'CAPTURE_HASH_MISMATCH');report['checks'].append('stale hash rejected before replacement')
            if a.step=='2':
                call(client,{**load,'CapturePath':inputs['invalidPath'],'CaptureSha256':inputs['invalidSha256']},'CAPTURE_LOAD_FAILED_RESTORED')
                report['checks'].append('real invalid native load restored original buffer and flags')
            lease=call(client,load);ensure(lease['state']=='loaded' and lease['recordingEnabled'] is False,'No stopped loaded lease')
            report['leaseId']=lease['leaseId'];save()
            call(client,load,'CAPTURE_LEASE_ACTIVE');report['checks'].append('duplicate explicit load not replayed')
            if a.domain_reload:
                owned=a.inputs.parent
                ensure(not (owned/'before-reload.json').exists() and not (owned/'after-reload.json').exists(),'Fresh reload evidence required')
                client.call_tool('UniBridge_ManageMenuItem',{'Action':'Execute','MenuPath':'Tools/UniBridge Validation/Profiler Extensions/RequestDomainReload'},timeout=30)
                deadline=time.monotonic()+120
                while not (owned/'after-reload.json').exists():
                    ensure(time.monotonic()<deadline,'No actual domain reload evidence');time.sleep(.25)
                before=json.loads((owned/'before-reload.json').read_text());after=json.loads((owned/'after-reload.json').read_text())
                ensure(before['editorPid']==after['editorPid'] and after['enabled'] is False,'Reload changed process/recording')
                report['domainReload']={'before':before,'after':after};save()
                def record_readiness(attempt):report['readinessAttempts'].append(attempt);save()
                report['afterReloadReadiness']=wait_after_reload(lambda arguments,timeout:client.call_tool('UniBridge_ManageEditor',arguments,timeout=timeout,allow_error=True),record_readiness)
                report['checks'].append('actual Editor readiness confirmed after domain reload and discovery reconnection');save()
            status=call(client,{'Action':'GetCaptureLease'});ensure(status['leaseId']==lease['leaseId'] and status['loadedSha256']==lease['loadedSha256'],'Durable lease lost')
            if a.step=='3':
                # Start with one latest frame without the full raw-sample response. The bounded fallback
                # uses source-selected actual frame IDs, never a guessed contiguous range.
                scout={'Action':'RecordedFrames','FrameCount':1,'MaxThreads':64,'MaxRawSamples':100000,
                       'ReturnSamples':False,'SaveToFile':False,'MarkerFilters':[inputs['expectedMarker']],
                       'MaxTopSamples':1,'MaxHierarchyDepth':1,'IncludeMetadata':False}
                found=None;seen=set()
                for frame_count in (1,12):
                    summary=call(client,{**scout,'FrameCount':frame_count},allow_partial=True)
                    selected=set(summary['selectedFrameIndices'])
                    for frame in reversed(summary['frames']):
                        index=frame['frameIndex'];ensure(index in selected,'Scouting returned an unselected frame ID')
                        for candidate in frame['threads']:
                            key=(index,candidate['threadId'])
                            if key in seen:continue
                            seen.add(key)
                            if not any(sample['name']==inputs['expectedMarker'] for sample in candidate.get('topSamples',[])):continue
                            physical=call(client,{**scout,'FrameIndex':index,'FrameCount':1,'ThreadIds':[candidate['threadId']],
                                                  'MaxThreads':1,'ReturnSamples':True},allow_partial=True)
                            for control_frame in physical['frames']:
                                for thread in control_frame['threads']:
                                    if thread.get('rawCoverageComplete') is not True or thread.get('topologyComplete') is not True:continue
                                    samples=[sample for sample in thread.get('samples',[]) if sample['name']==inputs['expectedMarker']]
                                    if len(samples)>=inputs['minimumCallsPerFrame']:found=(index,thread,samples);break
                                if found:break
                            if found:break
                        if found:break
                    if found:break
                ensure(found,'Known repeated physical samples missing')
                index,thread,samples=found
                bottom=call(client,{'Action':'RecordedBottomUp','FrameIndex':index,'FrameCount':1,'ThreadIds':[thread['threadId']],'MaxThreads':1,'MaxRawSamples':100000,'MarkerFilters':[inputs['expectedMarker']],'MaxBottomUpGroups':40,'SaveToFile':False})
                groups=[group for group in bottom['groups'] if group['name']==inputs['expectedMarker']]
                ensure(bottom['complete'] is True and len(groups)==1,'Expected one complete repeated marker group')
                group=groups[0];ensure(group['physicalCallCount']==len(samples),'Repeated call count differs from physical source')
                ensure(abs(group['selfTimeMs']-sum(sample['selfTimeMs'] for sample in samples))<.00001,'Aggregated self time differs from physical source')
                ensure(group['inclusiveTimeMs']<sum(sample['totalTimeMs'] for sample in samples),'Recursive inclusive intervals were double-counted')
                ensure(group['groupId'].startswith('aggregate:') and set(group['sampleIds'])<=set(sample['sampleId'] for sample in samples),'Aggregate identity leaked into physical IDs')
                rejected=call(client,{'Action':'RecordedFrames','FrameIndex':index,'FrameCount':1,'IncludeCallstacks':True,'CallstackSampleIds':[group['groupId']],'SaveToFile':False},'INVALID_RECORDED_CALLSTACK_SAMPLE_ID')
                ensure(rejected['copiedSamples']==0 and rejected['frames']==[],'Aggregate identity opened physical frame views')
                report['bottomUpEvidence']={'frameIndex':index,'threadId':thread['threadId'],'physicalCount':len(samples),'physicalSumSelfMs':sum(sample['selfTimeMs'] for sample in samples),'group':group};report['checks'].append('actual recursive repeated marker count/self/interval union matches physical samples');save()
            restored=call(client,{'Action':'RestoreCapture','ExpectedProjectRoot':root,'CaptureLeaseId':lease['leaseId']})
            ensure(restored['restorationVerified'] is True and restored['state']=='restored','Restoration was not verified')
            report['restorationVerified']=True;lease=None
            ensure(call(client,{'Action':'GetCaptureLease'})['state']=='none','Active lease pointer retained after restoration')
            report['passed']=True;report['checks'].append('original capture serialization and profiler flags restored');save()
    except Exception as error:
        report['error']=str(error)[:1800];save()
    finally:
        if lease:
            try:
                with Client(a.relay,a.project,45) as client:
                    client.initialize();restored=call(client,{'Action':'RestoreCapture','ExpectedProjectRoot':root,'CaptureLeaseId':lease['leaseId']});report['cleanupRestore']=restored;report['restorationVerified']=restored.get('restorationVerified') is True
            except Exception as error:report['cleanupError']=str(error)[:1200]
            save()
    print(json.dumps({key:report.get(key) for key in ('passed','step','restorationVerified','error','cleanupError')}));return 0 if report['passed'] else 1
if __name__=='__main__':raise SystemExit(main())
