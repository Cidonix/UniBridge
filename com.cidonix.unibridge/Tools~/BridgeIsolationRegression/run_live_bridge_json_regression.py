"""Elevated, Test-only native JSON and ordinary short-lived MCP qualification.

An exact UUID-owned fixture temporarily installs a throwing global Newtonsoft
factory. Its original delegate is restored explicitly, on reload/quit, or by a
55-second Editor update guard. No secondary Bridge or author assets are used.
"""
import argparse
import ctypes
import hashlib
import importlib.util
import json
import os
import stat
import sys
import time
import uuid
from pathlib import Path


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def response_identity(envelope, project_id, tool, params):
    context = envelope.get('projectContext')
    if context is not None:
        assert context.get('id') == project_id, 'Response project identity mismatch'
        return 'explicit-project-context'
    data = envelope.get('data', {})
    assert (tool == 'UniBridge_ManageEditor' and params.get('Action') == 'RefreshAssets'
            and data.get('recoveredAfterRefreshReload') is True
            and data.get('originalTool') == tool and data.get('originalAction') == 'RefreshAssets'), 'Untagged response outside the known relay reload recovery envelope'
    # The relay recovery envelope has no projectContext. Its connection is bound
    # to the root/ID verified by the initial snapshot; the subsequent fresh
    # WaitForReadyAfterReload response must supply the explicit identity again.
    return 'verified-target-relay-reload-recovery'


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--project', type=Path, required=True)
    p.add_argument('--project-id', required=True)
    p.add_argument('--relay', type=Path, required=True)
    p.add_argument('--expected-version', required=True)
    p.add_argument('--freeze', type=Path, required=True)
    p.add_argument('--report', type=Path, required=True)
    p.add_argument('--allow-test-fixture', action='store_true', help='Confirm this specific project is an owner-authorized test target for temporary Assets and bounded global JSON settings changes.')
    args = p.parse_args()
    assert os.name == 'nt' and ctypes.windll.shell32.IsUserAnAdmin(), 'Administrator launch required'
    args.project = args.project.resolve(strict=True)
    assert args.allow_test_fixture, 'Explicit owner-authorized test fixture opt-in required'
    identity = json.loads((args.project/'ProjectSettings/UniBridge/project.json').read_text(encoding='utf-8-sig'))
    assert identity['project_id'] == args.project_id, 'Configured project identity mismatch'
    args.report = args.report.resolve()
    assert not args.report.exists(), 'Retain all earlier attempts'
    args.report.parent.mkdir(parents=True, exist_ok=True)
    sys.dont_write_bytecode = True
    suite = Path(__file__).resolve().parent
    spec = importlib.util.spec_from_file_location('mcp', suite.parent/'McpSmokeRegression/run_mcp_smoke_regression.py')
    mcp = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mcp)
    class OwnedClient(mcp.McpClient):
        def __enter__(self):
            try:
                return super().__enter__()
            except BaseException:
                self.__exit__(*sys.exc_info())
                raise
    token = uuid.uuid4().hex
    fixture = args.project/'Assets'/('__UniBridgeJson_' + token)
    native = args.project/'Library/AgentValidation'/('UniBridgeJson' + token)
    menu = 'Tools/UniBridge Agent Tests/JSON ' + token + '/'
    sources, calls, checks = {}, {}, []
    baseline = None
    installed = began = False
    restore_confirmed = False
    failure = None
    completed = False
    manifest = args.report.with_suffix('.ownership.json')
    frozen = json.loads(args.freeze.read_text(encoding='utf-8-sig'))

    def save_report():
        args.report.write_text(json.dumps({'passed':completed and failure is None and bool(checks) and all(c['passed'] for c in checks),
            'completed':completed,
            'administrator':True, 'checks':checks, 'calls':calls, 'failure':failure,
            'fixtureRoot':str(fixture), 'nativeEvidence':str(native), 'fixtureRemoved':not fixture.exists(),
            'restoreConfirmed':restore_confirmed}, ensure_ascii=False, indent=2), encoding='utf-8')

    def check(name, value, detail=None):
        checks.append({'name':name, 'passed':bool(value), 'detail':detail})
        if not value:
            raise AssertionError(name + ': ' + str(detail))

    def call(client, label, tool, params):
        result = client.call_tool(tool, params)
        calls[label] = {'params':params, 'result':result}
        envelope = result['data']
        check('truthful success '+label, envelope.get('success') is not False)
        mode = response_identity(envelope, args.project_id, tool, params)
        calls[label]['identityEvidence'] = mode
        check('project identity '+label, True, mode)
        return envelope.get('data', envelope)

    def state(client, label):
        return call(client, label, 'UniBridge_ContextSnapshot', {'Depth':'Brief', 'IncludeSelection':True, 'IncludeConsole':True})

    def snapshot_files():
        files = {path for area in ('Assets', 'ProjectSettings') for path in (args.project/area).rglob('*')
                 if path.is_file() and not path.is_relative_to(fixture) and path != Path(str(fixture)+'.meta')}
        files.update(args.project/'Packages'/n for n in ('manifest.json', 'packages-lock.json'))
        return {path.relative_to(args.project).as_posix():sha(path) for path in sorted(files)}

    def package_parity():
        return all(sha(args.project/'Packages'/path) == digest for path, digest in frozen['hashes'].items())

    def refresh(label):
        # This mutation is dispatched exactly once, and never replayed on a disconnect.
        calls[label+'-dispatch'] = {'dispatched':True, 'replayAllowed':False}
        save_report()
        with OwnedClient(args.relay, args.project, 90, args.report.with_suffix('.trace.log')) as client:
            call(client, label, 'UniBridge_ManageEditor', {'Action':'RefreshAssets', 'WaitForCompletion':True, 'Force':True})
        with OwnedClient(args.relay, args.project, 90) as client:
            call(client, label+'-ready', 'UniBridge_ManageEditor', {'Action':'WaitForReadyAfterReload', 'RequireNotPlaying':True, 'TimeoutMs':60000})
            diag = call(client, label+'-diagnostics', 'UniBridge_ManageEditor', {'Action':'GetCompilationDiagnostics'})
            check('fresh healthy compilation '+label, diag['diagnostics']['errors'] == 0 and diag['compileHealth']['healthy']
                  and diag['assemblyFreshness']['staleAssemblyCount'] == 0 and diag['assemblyFreshness']['missingOutputAssemblyCount'] == 0)

    def latest_native():
        files = sorted(native.glob('*.json'))
        check('native report present', bool(files))
        return json.loads(files[-1].read_text(encoding='utf-8-sig'))

    def cleanup():
        # Whole child set, reparse state and every known source/meta hash pass first.
        assert fixture.parent == args.project/'Assets' and fixture.name == '__UniBridgeJson_' + token
        actual = set(fixture.iterdir()) if fixture.exists() else set()
        assert actual == {path for path in sources if path.parent == fixture}, 'Unknown fixture child preserved'
        for path, digest in sources.items():
            assert path.is_file() and sha(path) == digest, 'Changed fixture source/meta preserved: '+str(path)
            assert not getattr(path.lstat(), 'st_file_attributes', 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT
        assert not getattr(fixture.lstat(), 'st_file_attributes', 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT
        for path in sources:
            if path.parent == fixture:
                path.unlink()
        fixture.rmdir()
        Path(str(fixture)+'.meta').unlink()

    try:
        check('qualified frozen package installed', package_parity())
        with OwnedClient(args.relay, args.project, 90) as client:
            before = state(client, 'baseline-state')
        check('actual target project', before['project']['id'] == args.project_id and Path(before['project']['root']).resolve() == args.project)
        check('expected candidate', before['package']['version'] == args.expected_version)
        check('idle test editor', not any(before['editor'][k] for k in ('isPlaying','isPlayingOrWillChangePlaymode','isCompiling','isUpdating')))
        check('clean test scenes', not before['editor']['hasDirtyScenes'])
        baseline = snapshot_files()
        check('new unique fixture absent', not fixture.exists() and not Path(str(fixture)+'.meta').exists())
        source = (suite/'LiveJsonFixture.cs').read_text(encoding='utf-8-sig').replace('__TOKEN__', token).encode('utf-8')
        source_path = fixture/'LiveJsonFixture.cs'
        payloads = {source_path:source,
            Path(str(source_path)+'.meta'):('fileFormatVersion: 2\nguid: '+uuid.uuid4().hex+'\nMonoImporter:\n  externalObjects: {}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n').encode(),
            Path(str(fixture)+'.meta'):('fileFormatVersion: 2\nguid: '+uuid.uuid4().hex+'\nfolderAsset: yes\nDefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n').encode()}
        sources = {path:hashlib.sha256(data).hexdigest() for path, data in payloads.items()}
        manifest.write_text(json.dumps({'fixtureRoot':str(fixture), 'expectedHashes':{str(path):digest for path,digest in sources.items()},
            'policy':'Exact UUID-owned source/meta pairs only. Unknown or changed paths are preserved.'},indent=2),encoding='utf-8')
        fixture.mkdir()
        for path, payload in payloads.items():
            with path.open('xb') as f:
                f.write(payload)
        installed = True
        with OwnedClient(args.relay, args.project, 90) as client:
            validation = call(client, 'fixture-standard-validation', 'UniBridge_ValidateScript', {'Uri':str(source_path), 'Level':'standard', 'IncludeDiagnostics':True, 'IncludeObsoleteApiHints':False})
            check('native fixture validation', validation.get('errors',0) == 0)
        refresh('fixture-import')
        with OwnedClient(args.relay, args.project, 90) as client:
            exists = call(client, 'owned-menu-exists', 'UniBridge_ManageMenuItem', {'Action':'Exists', 'MenuPath':menu+'Begin', 'Refresh':True})
            check('native owned menu imported', exists.get('exists') is True, exists)
            began = True
            call(client, 'begin-global-isolation', 'UniBridge_ManageMenuItem', {'Action':'Execute', 'MenuPath':menu+'Begin', 'Refresh':False})
        begin_report = latest_native()
        check('native isolated helper checks', begin_report['active'] and begin_report['factoryCalls'] == 0 and all(begin_report['checks'].values()), begin_report)
        start = time.monotonic()
        for index in range(4):
            with OwnedClient(args.relay, args.project, 20, args.report.with_suffix('.trace.log')) as client:
                tools = client.list_tools()
                calls['fresh-tools-'+str(index)] = tools
                check('actual eager tool catalog '+str(index), len(tools) >= 66 and any(t['name']=='UniBridge_ContextSnapshot' for t in tools))
                current = state(client, 'poisoned-context-'+str(index))
                check('same native state under global factory '+str(index), all(current[k] == before[k] for k in ('scenes','selection','prefabStage')))
                call(client, 'poisoned-editor-'+str(index), 'UniBridge_ManageEditor', {'Action':'GetState'})
                validation = call(client, 'poisoned-uitoolkit-'+str(index), 'UniBridge_ManageUIToolkit', {
                    'Action':'ValidateUxml', 'Content':'<ui:UXML xmlns:ui="UnityEngine.UIElements"><ui:Label text="native" /></ui:UXML>'})
                check('complex typed route under global factory '+str(index), validation.get('validation',{}).get('valid') is True
                      and validation.get('written') is False and validation.get('imported') is False, validation)
            check('bounded native guard not elapsed '+str(index), time.monotonic()-start < 40)
        with OwnedClient(args.relay, args.project, 20) as client:
            call(client, 'inspect-global-isolation', 'UniBridge_ManageMenuItem', {'Action':'Execute', 'MenuPath':menu+'Inspect', 'Refresh':False})
        inspected = latest_native()
        check('actual calls never consulted global callback', inspected['reason']=='active_inspected' and inspected['active']
              and inspected['factoryCalls']==0 and all(inspected['checks'].values()), inspected)
    except Exception as exc:
        failure = type(exc).__name__ + ': ' + str(exc)
    finally:
        if began:
            try:
                with OwnedClient(args.relay, args.project, 60) as client:
                    call(client, 'restore-original-global', 'UniBridge_ManageMenuItem', {'Action':'Execute', 'MenuPath':menu+'Restore', 'Refresh':False})
                restored = latest_native()
                restore_confirmed = not restored['active'] and restored['checks'].get('exact original callback restored') is True
                check('exact original global delegate restored', restore_confirmed, restored)
                check('no global coupling during native actual calls', restored['factoryCalls']==0, restored)
            except Exception as exc:
                failure = (failure or '')+'; restore: '+str(exc)
        if installed and (not began or restore_confirmed):
            try:
                cleanup()
                refresh('fixture-cleanup')
                check('temporary source and pairs removed', not fixture.exists() and not Path(str(fixture)+'.meta').exists())
            except Exception as exc:
                failure = (failure or '')+'; cleanup: '+str(exc)
        if baseline is not None:
            try:
                check('all original author/dependency files retained', snapshot_files()==baseline)
                check('frozen package retained', package_parity())
                with OwnedClient(args.relay, args.project, 90) as client:
                    after = state(client, 'final-state')
                check('scenes selection prefab dirty state retained', all(after[k]==before[k] for k in ('scenes','selection','prefabStage'))
                      and after['editor']['hasDirtyScenes']==before['editor']['hasDirtyScenes'])
            except Exception as exc:
                failure = (failure or '')+'; preservation: '+str(exc)
        completed = True
        save_report()
    print(json.dumps({'passed':failure is None, 'assertions':len(checks), 'failure':failure, 'fixtureRemoved':not fixture.exists(), 'restoreConfirmed':restore_confirmed}),flush=True)
    return 0 if failure is None else 1


if __name__ == '__main__':
    raise SystemExit(main())
