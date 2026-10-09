"""Qualify automatic script writer ownership and guarded revert in a live Editor."""
import argparse
import ctypes
import hashlib
import importlib.util
import json
import os
import sys
import uuid
from pathlib import Path


def sha(payload):
    return hashlib.sha256(payload).hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--project', type=Path, required=True)
    parser.add_argument('--project-id', required=True)
    parser.add_argument('--relay', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--expected-version', default='0.2.60')
    args = parser.parse_args()
    if os.name != 'nt' or not ctypes.windll.shell32.IsUserAnAdmin():
        parser.error('Run from an Administrator terminal.')
    args.project = args.project.resolve(strict=True)
    args.report = args.report.resolve()
    args.report.parent.mkdir(parents=True, exist_ok=True)
    suite = Path(__file__).resolve().parent
    sys.dont_write_bytecode = True
    spec = importlib.util.spec_from_file_location('mcp', suite.parent/'McpSmokeRegression/run_mcp_smoke_regression.py')
    mcp = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mcp)
    name = 'UniBridgeOwnedScriptRegression_' + uuid.uuid4().hex
    path = 'Assets/' + name + '.cs'
    full = args.project/path
    metadata = Path(str(full)+'.meta')
    baseline = ('public static class ' + name + '\n{\n    public static int Value() { return 1; }\n}\n').encode()
    updated = baseline.replace(b'return 1;', b'return 2;')
    manifest = args.project/'Library/UniBridge/RestoreSafetyRegression'/name/'ownership-manifest.json'
    last_owned = None
    metadata_owned = None
    created = False
    session_id = None
    before = None
    scenes_before = {}
    checks, responses = [], {}

    def check(label, condition, detail=None):
        checks.append({'name':label,'passed':bool(condition),'detail':detail})
        if not condition:
            raise AssertionError(label + ': ' + str(detail))

    def persist_manifest():
        manifest.parent.mkdir(parents=True, exist_ok=True)
        manifest.write_text(json.dumps({'owner':'UniBridge automatic script ownership regression','fixturePath':path,
            'lastOwnedSha256':last_owned,'metadataOwnedSha256':metadata_owned,'sessionId':session_id},indent=2),encoding='utf-8')

    def call(bridge, label, tool, arguments):
        result = bridge.call_tool(tool, arguments)
        envelope = result['data']
        responses[label] = result
        context = envelope.get('projectContext', {})
        check('Project identity '+label, not context.get('id') or context['id'] == args.project_id)
        if envelope.get('success') is False:
            raise RuntimeError(label + ': ' + str(envelope))
        return envelope.get('data', envelope)

    def refresh(bridge, label):
        call(bridge, label, 'UniBridge_ManageEditor', {'Action':'RefreshAssets','WaitForCompletion':True,'Force':True})
        data = call(bridge, label+'-diagnostics', 'UniBridge_ManageEditor', {'Action':'GetCompilationDiagnostics'})
        check('Fresh healthy assemblies '+label, data['compileHealth']['healthy'] and not data['assemblyFreshness']['staleLikely'], data.get('compileHealth'))

    try:
        check('Unique script absent', not full.exists() and not metadata.exists())
        with mcp.McpClient(args.relay,args.project,120,args.report.with_suffix('.trace.log')) as bridge:
            before = call(bridge,'baseline-state','UniBridge_ContextSnapshot',{'Depth':'Brief','IncludeSelection':True})
            check('Correct project/package',before['project']['id'] == args.project_id and before['package']['version'] == args.expected_version)
            check('Editor idle',not any(before['editor'][key] for key in ('isCompiling','isUpdating','isPlaying')))
            session = call(bridge,'author-session-check','UniBridge_WorkSession',{'Action':'Status'})
            check('No author active session',session.get('active') is False)
            scenes_before = {scene['path']:sha((args.project/scene['path']).read_bytes()) for scene in before['scenes']['loaded'] if scene.get('path')}
            # The known baseline exists and compiles BEFORE the session starts.
            full.write_bytes(baseline)
            created = True
            last_owned = sha(baseline)
            persist_manifest()
            refresh(bridge,'compile-owned-baseline')
            check('Baseline bytes imported unchanged',full.read_bytes() == baseline)
            metadata_owned = sha(metadata.read_bytes()) if metadata.exists() else None
            persist_manifest()
            begin = call(bridge,'begin','UniBridge_WorkSession',{'Action':'Begin','Name':'Automatic script writer ownership fixture',
                'IncludeSceneSemantics':False,'IncludeProjectSettings':False,'IncludePackageManifests':False,'MaxFiles':24000})
            session_id = begin['session']['sessionId']
            persist_manifest()
            check('Complete script baseline',begin['baseline']['ScanComplete'] is True)
            check('Own source precondition',sha(full.read_bytes()) == last_owned)
            result = call(bridge,'automatic-script-update','UniBridge_Script',{'action':'update','name':name,'path':'Assets','contents':updated.decode()})
            check('Automatic writer exact known bytes',full.read_bytes() == updated)
            last_owned = sha(updated)
            persist_manifest()
            ownership = result['workSessionOwnership']
            check('Automatic writer records expected payload',ownership['Succeeded'] is True and ownership['RecordedCount'] == 1 and ownership['ConflictCount'] == 0,ownership)
            refresh(bridge,'compile-owned-update')
            review = call(bridge,'owned-review','UniBridge_WorkSession',{'Action':'Review','SessionId':session_id,'IncludeSemanticReview':False,'MaxChanged':24000})
            changed = next(item for item in review['changedFiles'] if item['path'] == path)
            check('Script writer review eligible for guarded restore',changed['changeType'] == 'Modified' and changed['canRevert'],changed)
            preview = call(bridge,'revert-preview','UniBridge_WorkSession',{'Action':'Revert','SessionId':session_id,'Paths':[path],'DryRun':True,'DeleteAddedMetaWithAsset':False})
            check('Reviewed script restore plan',preview['status'] == 'preview' and preview['canExecute'] and preview.get('planId'))
            check('Script preview preserves updated bytes',full.read_bytes() == updated)
            reverted = call(bridge,'revert-script','UniBridge_WorkSession',{'Action':'Revert','SessionId':session_id,'Paths':[path],'DryRun':False,
                'PlanId':preview['planId'],'DeleteAddedMetaWithAsset':False})
            check('Script guarded restore completed',reverted['status'] == 'completed' and reverted['dryRun'] is False)
            check('Exact author baseline bytes restored',full.read_bytes() == baseline)
            last_owned = sha(baseline)
            persist_manifest()
            refresh(bridge,'compile-reverted-baseline')
            call(bridge,'end','UniBridge_WorkSession',{'Action':'End','SessionId':session_id})
            session_id = None
            persist_manifest()
            after = call(bridge,'after-state','UniBridge_ContextSnapshot',{'Depth':'Brief','IncludeSelection':True})
            check('Author scenes and selection preserved',before['scenes'] == after['scenes'] and before['selection'] == after['selection']
                and all(sha((args.project/item).read_bytes()) == value for item,value in scenes_before.items()))
    except Exception as error:
        checks.append({'name':'qualification exception','passed':False,'detail':repr(error)})
    finally:
        try:
            with mcp.McpClient(args.relay,args.project,120,args.report.with_suffix('.cleanup.trace.log')) as bridge:
                if session_id:
                    call(bridge,'cleanup-end','UniBridge_WorkSession',{'Action':'End','SessionId':session_id})
                    session_id = None
                if created and full.exists():
                    check('Cleanup source ownership',last_owned is not None and sha(full.read_bytes()) == last_owned)
                    if metadata.exists():
                        check('Cleanup metadata ownership',metadata_owned is not None and sha(metadata.read_bytes()) == metadata_owned)
                    call(bridge,'cleanup-delete','UniBridge_DeleteScript',{'Uri':path})
                    check('Owned script and metadata removed',not full.exists() and not metadata.exists())
                    refresh(bridge,'compile-after-cleanup')
                after = call(bridge,'final-state','UniBridge_ContextSnapshot',{'Depth':'Brief','IncludeSelection':True})
                if before:
                    check('Final author state preserved',before['scenes'] == after['scenes'] and before['selection'] == after['selection']
                        and all(sha((args.project/item).read_bytes()) == value for item,value in scenes_before.items()))
        except Exception as error:
            checks.append({'name':'owned cleanup exception','passed':False,'detail':repr(error)})
        report = {'project':str(args.project),'projectId':args.project_id,'administrator':True,'fixturePath':path,'manifest':str(manifest),
                  'checks':checks,'responses':responses,'passed':sum(item['passed'] for item in checks),
                  'failed':sum(not item['passed'] for item in checks),'ownedFixtureRemoved':not full.exists() and not metadata.exists()}
        args.report.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
        print(json.dumps({key:report[key] for key in ('passed','failed','ownedFixtureRemoved')}))
    return 0 if report['failed'] == 0 and report['ownedFixtureRemoved'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
