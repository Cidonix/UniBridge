"""Elevated project-scoped restore qualification on a uniquely owned fixture folder."""
import argparse
import ctypes
import hashlib
import importlib.util
import json
import os
import sys
import time
import uuid
from pathlib import Path


def digest(payload):
    return hashlib.sha256(payload).hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--project', type=Path, required=True)
    parser.add_argument('--project-id', required=True)
    parser.add_argument('--relay', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--expected-version', default='0.2.60')
    parser.add_argument('--snapshot-tool', help='Transient root-installed live snapshot fixture tool to invoke after the WorkSession checks.')
    parser.add_argument('--snapshot-editor-pid', type=int, help='Verified Unity Editor PID for the exact owned save-failure dialog guard.')
    args = parser.parse_args()
    if os.name != 'nt' or not ctypes.windll.shell32.IsUserAnAdmin():
        parser.error('Run from an Administrator terminal.')
    if args.snapshot_tool and not args.snapshot_editor_pid:
        parser.error('--snapshot-tool requires --snapshot-editor-pid from the verified test Editor.')
    args.project = args.project.resolve(strict=True)
    args.report = args.report.resolve()
    args.report.parent.mkdir(parents=True, exist_ok=True)
    suite = Path(__file__).resolve().parent
    sys.dont_write_bytecode = True
    spec = importlib.util.spec_from_file_location('mcp', suite.parent/'McpSmokeRegression/run_mcp_smoke_regression.py')
    mcp = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mcp)
    unique = 'UniBridgeRestoreSafety_' + uuid.uuid4().hex
    relative_root = 'Assets/' + unique
    fixture = args.project/relative_root
    manifest = args.project/'Library/UniBridge/RestoreSafetyRegression'/unique/'ownership-manifest.json'
    owned = {}
    checks, responses = [], {}
    session_id = None
    before = None
    scene_hashes = {}
    folder_created = False
    dialog_guard = None

    def persist_manifest():
        manifest.parent.mkdir(parents=True, exist_ok=True)
        manifest.write_text(json.dumps({'owner':'UniBridge RestoreSafetyRegression', 'fixtureRoot':relative_root,
                                       'lastOwnedSha256':owned, 'sessionId':session_id}, indent=2), encoding='utf-8')

    def check(name, condition, detail=None):
        checks.append({'name':name, 'passed':bool(condition), 'detail':detail})
        if not condition:
            raise AssertionError(name + ': ' + str(detail))

    def raw_call(bridge, label, tool, arguments, expect_error=False):
        raw = bridge.send('tools/call', {'name':tool, 'arguments':arguments})['result']
        text = next(item['text'] for item in raw.get('content', []) if item.get('type') == 'text')
        envelope = json.loads(text)
        responses[label] = {'raw':raw, 'envelope':envelope}
        context = envelope.get('projectContext', {})
        check('Project-scoped response '+label, not context.get('id') or context['id'] == args.project_id, context)
        if expect_error:
            check('MCP isError '+label, raw.get('isError') is True and envelope.get('success') is False, envelope)
        elif envelope.get('success') is False or raw.get('isError'):
            raise RuntimeError(label + ': ' + str(envelope))
        return envelope, envelope.get('data', envelope)

    def call(bridge, label, action, arguments=None, expect_error=False):
        payload = dict(arguments or {}, Action=action)
        if session_id:
            payload['SessionId'] = session_id
        return raw_call(bridge, label, 'UniBridge_WorkSession', payload, expect_error)

    def write_owned(path, payload):
        full = args.project/path
        check('Write confined to owned fixture '+path, str(full.resolve()).startswith(str(fixture.resolve())+os.sep))
        if full.exists() and path in owned:
            check('Fixture before-state '+path, digest(full.read_bytes()) == owned[path])
        full.write_bytes(payload)
        owned[path] = digest(payload)
        persist_manifest()

    def tracked_write(bridge, label, path, payload):
        _, token = call(bridge, label+'-begin-write', 'BeginWrite', {'Paths':[path], 'Source':'RestoreSafetyRegression/explicit-known-payload'})
        # The bytes and hash are known before the write, independent of disk readback.
        expected = digest(payload)
        write_owned(path, payload)
        _, receipt = call(bridge, label+'-complete-write', 'CompleteWrite', {'TokenId':token['tokenId'], 'AfterSha256':{path:expected}})
        check('Known write recorded '+label, receipt['recorded'] == 1 and receipt['conflicted'] == 0, receipt)

    def preview(bridge, label, paths):
        _, data = call(bridge, label, 'Revert', {'Paths':paths, 'DryRun':True, 'DeleteAddedMetaWithAsset':False})
        check('Reviewed plan '+label, data['status'] == 'preview' and data['dryRun'] is True and data['canExecute'] and data.get('planId'), data)
        return data

    def observe_generated_metadata():
        # Only exact companions of our declared source files may be observed.
        # An unrelated file created in this folder remains outside ownership,
        # including its metadata.
        for source in list(owned):
            if source.endswith('.meta'):
                continue
            relative = source+'.meta'
            path = args.project/relative
            if path.exists() and relative not in owned:
                owned[relative] = digest(path.read_bytes())
        outer = Path(str(fixture)+'.meta')
        if outer.exists():
            relative = outer.relative_to(args.project).as_posix()
            if relative not in owned:
                owned[relative] = digest(outer.read_bytes())
        persist_manifest()

    try:
        check('Unique folder absent', not fixture.exists() and not Path(str(fixture)+'.meta').exists())
        with mcp.McpClient(args.relay, args.project, 120, args.report.with_suffix('.trace.log')) as bridge:
            _, before = raw_call(bridge, 'initial-state', 'UniBridge_ContextSnapshot', {'Depth':'Brief', 'IncludeSelection':True})
            check('Correct project and package', before['project']['id'] == args.project_id and before['package']['version'] == args.expected_version)
            check('Editor idle before fixture', not any(before['editor'][key] for key in ('isCompiling','isUpdating','isPlaying')))
            _, current_session = call(bridge, 'existing-session', 'Status')
            check('No author WorkSession active', current_session.get('active') is False, current_session.get('session'))
            scene_hashes = {scene['path']:digest((args.project/scene['path']).read_bytes()) for scene in before['scenes']['loaded'] if scene.get('path')}
            fixture.mkdir()
            folder_created = True
            persist_manifest()
            a, b = relative_root+'/a.txt', relative_root+'/b.txt'
            write_owned(a, b'baseline a\n')
            write_owned(b, b'baseline b\n')
            raw_call(bridge, 'import-owned-baseline', 'UniBridge_ManageEditor', {'Action':'RefreshAssets','WaitForCompletion':True,'Force':True})
            observe_generated_metadata()
            _, begin = call(bridge, 'begin-session', 'Begin', {'Name':'Restore safety isolated live fixtures',
                'IncludeSceneSemantics':False, 'IncludeProjectSettings':False, 'IncludePackageManifests':False, 'MaxFiles':24000})
            session_id = begin['session']['sessionId']
            persist_manifest()
            check('Complete live baseline', begin['baseline']['ScanComplete'] is True, begin['baseline'])
            tracked_write(bridge, 'first-owned-edit', a, b'owned a\n')
            plan = preview(bridge, 'owned-preview', [a])
            check('Preview writes nothing', (args.project/a).read_bytes() == b'owned a\n')
            envelope, result = call(bridge, 'owned-execute', 'Revert', {'Paths':[a], 'DryRun':False, 'PlanId':plan['planId'],'DeleteAddedMetaWithAsset':False})
            check('Real revert truthful completed', envelope['success'] and result['status'] == 'completed' and result['dryRun'] is False)
            check('Exact baseline bytes restored', (args.project/a).read_bytes() == b'baseline a\n')
            owned[a] = digest(b'baseline a\n')
            persist_manifest()
            tracked_write(bridge, 'second-owned-edit', a, b'second owned a\n')
            envelope, refused = call(bridge, 'consumed-plan-refusal', 'Revert', {'Paths':[a], 'DryRun':False,'PlanId':plan['planId'],'DeleteAddedMetaWithAsset':False}, True)
            check('Consumed plan blocked', refused['status'] == 'blocked' and (args.project/a).read_bytes() == b'second owned a\n')
            envelope, refused = call(bridge, 'missing-plan-refusal', 'Revert', {'Paths':[a], 'DryRun':False,'DeleteAddedMetaWithAsset':False}, True)
            check('Unreviewed execute blocked with truthful dryRun', refused['status'] == 'blocked' and refused['dryRun'] is False)
            plan = preview(bridge, 'second-preview', [a])
            # This explicit fixture edit represents an external author/concurrent edit.
            write_owned(a, b'simulated independent edit after preview\n')
            _, refused = call(bridge, 'stale-plan-refusal', 'Revert', {'Paths':[a], 'DryRun':False,'PlanId':plan['planId'],'DeleteAddedMetaWithAsset':False}, True)
            check('Concurrent bytes preserved', refused['status'] == 'blocked' and (args.project/a).read_bytes() == b'simulated independent edit after preview\n')
            write_owned(b, b'simulated unrelated author change\n')
            _, refused = call(bridge, 'unowned-preview-refusal', 'Revert', {'Paths':[b],'DryRun':True,'DeleteAddedMetaWithAsset':False}, True)
            check('Unowned difference rejected', refused['status'] == 'blocked' and not refused['canExecute'])
            call(bridge, 'end-session', 'End')
            session_id = None
            if args.snapshot_tool:
                from SnapshotOwnedDialog import OwnedSnapshotSaveDialogGuard
                snapshot_run = uuid.uuid4().hex
                dialog_guard = OwnedSnapshotSaveDialogGuard(editor_pid=args.snapshot_editor_pid,
                    project=args.project, run_id=snapshot_run, timeout_seconds=120)
                with dialog_guard:
                    _, snapshot_result = raw_call(bridge, 'live-snapshot-fixture', args.snapshot_tool, {'RunId':snapshot_run})
                dialog_actions = dialog_guard.evidence.get('actions', [])
                check('Owned save-dialog watcher stopped safely', dialog_guard.evidence.get('threadStopped')
                      and not dialog_guard.evidence.get('errors') and len(dialog_actions) <= 1
                      and all(item.get('posted') and item.get('dialogClosedObserved') for item in dialog_actions), dialog_guard.evidence)
                check('Live EditorSnapshot fixture passed', snapshot_result.get('failedCount') == 0
                      and not snapshot_result.get('errors') and snapshot_result.get('authorSceneStatePreserved')
                      and not snapshot_result.get('fixtureDirectoryRemaining'), snapshot_result)
                snapshot_cases = snapshot_result.get('cases', [])
                check('All six live snapshot cases executed without skips', len(snapshot_cases) == 6
                      and all(item.get('passed') and not item.get('skipped') for item in snapshot_cases), snapshot_cases)
            _, after = raw_call(bridge, 'state-after-tests', 'UniBridge_ContextSnapshot', {'Depth':'Brief','IncludeSelection':True})
            check('Author scenes and selection preserved', before['scenes'] == after['scenes'] and before['selection'] == after['selection'])
            check('Author scene disk bytes preserved', all(digest((args.project/path).read_bytes()) == value for path,value in scene_hashes.items()))
    except Exception as error:
        checks.append({'name':'qualification exception','passed':False,'detail':repr(error)})
    finally:
        # Cleanup is limited to manifest-owned fixture bytes. Unknown or changed
        # metadata remains for inspection, and no author path is removed.
        try:
            if session_id:
                with mcp.McpClient(args.relay,args.project,120,args.report.with_suffix('.cleanup.trace.log')) as bridge:
                    call(bridge, 'cleanup-end-session', 'End')
                session_id = None
            if folder_created:
                # Verify a declared asset and its exact companion together before
                # deleting either. Retain a changed/unknown companion's asset so
                # Unity cannot remove that independent metadata on refresh.
                blocked_cleanup = set()
                for relative in owned:
                    if relative.endswith('.meta'):
                        continue
                    pair = (relative, relative+'.meta')
                    for member in pair:
                        full = args.project/member
                        if full.exists() and (member not in owned or digest(full.read_bytes()) != owned[member]):
                            blocked_cleanup.update(pair)
                            checks.append({'name':'cleanup ownership mismatch '+member,'passed':False})
                outer_relative = relative_root+'.meta'
                outer = args.project/outer_relative
                outer_safe = not outer.exists() or (outer_relative in owned and digest(outer.read_bytes()) == owned[outer_relative])
                if not outer_safe:
                    checks.append({'name':'cleanup ownership mismatch '+outer_relative,'passed':False})
                for relative, expected in list(owned.items()):
                    if relative in blocked_cleanup or relative == outer_relative:
                        continue
                    full = args.project/relative
                    if full.exists():
                        if digest(full.read_bytes()) != expected:
                            checks.append({'name':'cleanup ownership mismatch '+relative,'passed':False})
                            continue
                        full.unlink()
                if fixture.exists() and not any(fixture.iterdir()) and outer_safe:
                    fixture.rmdir()
                    if outer.exists():
                        outer.unlink()
                with mcp.McpClient(args.relay,args.project,120,args.report.with_suffix('.cleanup.trace.log')) as bridge:
                    raw_call(bridge, 'refresh-after-owned-cleanup', 'UniBridge_ManageEditor', {'Action':'RefreshAssets','WaitForCompletion':True,'Force':True})
                    _, diagnostics = raw_call(bridge, 'final-diagnostics', 'UniBridge_ManageEditor', {'Action':'GetCompilationDiagnostics'})
                    check('Final compilation healthy', diagnostics['compileHealth']['healthy'] and not diagnostics['assemblyFreshness']['staleLikely'], diagnostics.get('summary'))
                    _, after = raw_call(bridge, 'final-state', 'UniBridge_ContextSnapshot', {'Depth':'Brief','IncludeSelection':True})
                    if before:
                        check('Final author state preserved', before['scenes'] == after['scenes'] and before['selection'] == after['selection'] and all(digest((args.project/path).read_bytes()) == value for path,value in scene_hashes.items()))
        except Exception as error:
            checks.append({'name':'owned fixture cleanup exception','passed':False,'detail':repr(error)})
        removed = not fixture.exists() and not Path(str(fixture)+'.meta').exists()
        report = {'project':str(args.project),'projectId':args.project_id,'administrator':True,'fixtureRoot':relative_root,
                  'manifest':str(manifest),'passed':sum(check['passed'] for check in checks),
                  'failed':sum(not check['passed'] for check in checks),'checks':checks,'responses':responses,
                  'ownedFixtureRemoved':removed,'snapshotDialogAudit':dialog_guard.evidence if dialog_guard else None}
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2),encoding='utf-8')
        print(json.dumps({key:report[key] for key in ('passed','failed','ownedFixtureRemoved')}))
    return 0 if report['failed'] == 0 and report['ownedFixtureRemoved'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
