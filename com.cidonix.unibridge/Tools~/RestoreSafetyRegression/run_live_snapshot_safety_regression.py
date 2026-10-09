"""Qualify only the transient six-case snapshot fixture in an elevated Editor."""
import argparse
import ctypes
import hashlib
import importlib.util
import json
import os
import sys
import uuid
from pathlib import Path


def digest(payload):
    return hashlib.sha256(payload).hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--project', type=Path, required=True)
    parser.add_argument('--project-id', required=True)
    parser.add_argument('--relay', type=Path, required=True)
    parser.add_argument('--editor-pid', type=int, required=True)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--expected-version', default='0.2.60')
    parser.add_argument('--snapshot-tool', default='UniBridge_RestoreSnapshotProbe')
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
    from SnapshotOwnedDialog import OwnedSnapshotSaveDialogGuard
    run_id = uuid.uuid4().hex
    checks, responses = [], {}
    before = None
    author_scene_hashes = {}
    dialog_guard = None
    snapshot = None

    def check(name, condition, detail=None):
        checks.append({'name':name, 'passed':bool(condition), 'detail':detail})
        if not condition:
            raise AssertionError(name+': '+str(detail))

    def call(bridge, label, tool, arguments):
        raw = bridge.send('tools/call', {'name':tool, 'arguments':arguments})['result']
        text = next(item['text'] for item in raw.get('content', []) if item.get('type') == 'text')
        envelope = json.loads(text)
        responses[label] = {'raw':raw, 'envelope':envelope}
        context = envelope.get('projectContext', {})
        check('Project-scoped response '+label, not context.get('id') or context['id'] == args.project_id, context)
        return envelope, envelope.get('data', envelope)

    try:
        with mcp.McpClient(args.relay,args.project,120,args.report.with_suffix('.trace.log')) as bridge:
            envelope, before = call(bridge,'before-state','UniBridge_ContextSnapshot',{'Depth':'Brief','IncludeSelection':True})
            check('Author baseline read succeeded',envelope.get('success') is True)
            check('Correct project/package',before['project']['id'] == args.project_id and before['package']['version'] == args.expected_version)
            check('Editor idle',not any(before['editor'][key] for key in ('isCompiling','isUpdating','isPlaying')))
            envelope, session = call(bridge,'author-session-check','UniBridge_WorkSession',{'Action':'Status'})
            check('No author WorkSession active',envelope.get('success') is True and session.get('active') is False)
            author_scene_hashes = {scene['path']:digest((args.project/scene['path']).read_bytes())
                for scene in before['scenes']['loaded'] if scene.get('path')}
            dialog_guard = OwnedSnapshotSaveDialogGuard(editor_pid=args.editor_pid,project=args.project,
                                                       run_id=run_id,timeout_seconds=120)
            with dialog_guard:
                envelope, snapshot = call(bridge,'snapshot-fixture',args.snapshot_tool,{'RunId':run_id})
            actions = dialog_guard.evidence.get('actions', [])
            check('Owned dialog guard stopped safely',dialog_guard.evidence.get('threadStopped')
                  and not dialog_guard.evidence.get('errors') and len(actions) <= 1
                  and all(item.get('posted') and item.get('dialogClosedObserved') for item in actions),dialog_guard.evidence)
            check('Live snapshot response succeeded',envelope.get('success') is True
                  and not responses['snapshot-fixture']['raw'].get('isError'),snapshot)
            cases = snapshot.get('cases', [])
            check('All six live scene cases passed without skips',len(cases) == 6
                  and all(item.get('passed') and not item.get('skipped') for item in cases),cases)
            check('Fixture errors absent',snapshot.get('failedCount') == 0 and not snapshot.get('errors'),snapshot.get('errors'))
            check('Original handles/state preserved by fixture',snapshot.get('authorSceneStatePreserved') is True)
            check('Only owned scene fixture cleaned',snapshot.get('fixtureDirectoryRemaining') is False)
    except Exception as error:
        checks.append({'name':'qualification exception','passed':False,'detail':repr(error)})
    finally:
        # The fixture owns scene cleanup. Follow-up calls are read-only and do
        # not repeat a possibly pending mutating probe or clear Console history.
        if before is not None:
            try:
                with mcp.McpClient(args.relay,args.project,120,args.report.with_suffix('.final.trace.log')) as bridge:
                    envelope, after = call(bridge,'after-state','UniBridge_ContextSnapshot',{'Depth':'Brief','IncludeSelection':True})
                    check('Final author scene/selection identity preserved',envelope.get('success') is True
                          and before['scenes'] == after['scenes'] and before['selection'] == after['selection'])
                    check('Author scene disk bytes preserved',all(digest((args.project/path).read_bytes()) == expected
                          for path,expected in author_scene_hashes.items()))
                    envelope, diagnostics = call(bridge,'final-diagnostics','UniBridge_ManageEditor',{'Action':'GetCompilationDiagnostics'})
                    check('Final fresh healthy compilation',envelope.get('success') is True and diagnostics['compileHealth']['healthy']
                          and not diagnostics['assemblyFreshness']['staleLikely'],diagnostics.get('summary'))
            except Exception as error:
                checks.append({'name':'read-only final preservation check exception','passed':False,'detail':repr(error)})
        tool_source = args.project/'Packages/com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor/Tools/EditorSnapshotTool.cs'
        report = {'scope':'snapshot-only actual Unity Editor qualification','project':str(args.project),'projectId':args.project_id,
                  'administrator':True,'editorPid':args.editor_pid,'runId':run_id,'checks':checks,'responses':responses,
                  'snapshotCases':snapshot.get('cases', []) if snapshot else [],
                  'ownedFixtureRemoved':snapshot.get('fixtureDirectoryRemaining') is False if snapshot else None,
                  'snapshotDialogAudit':dialog_guard.evidence if dialog_guard else None,
                  'productionSnapshotSha256':digest(tool_source.read_text(encoding='utf-8-sig').encode()) if tool_source.exists() else None,
                  'authorSceneBaselineSha256':author_scene_hashes,'passed':sum(item['passed'] for item in checks),
                  'failed':sum(not item['passed'] for item in checks)}
        args.report.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
        print(json.dumps({key:report[key] for key in ('passed','failed')}))
    return 0 if report['failed'] == 0 else 1


if __name__ == '__main__':
    raise SystemExit(main())
