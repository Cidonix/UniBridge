"""Project-scoped UXML/USS structural and actual Unity-import qualification.

Launch only after the coordinator installs and compiles the candidate package.
Every source is a new unique fixture, with expected hashes known before mutation.
"""
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


def author_files(project, fixture):
    paths = list((project/'Assets').rglob('*')) + list((project/'ProjectSettings').rglob('*'))
    paths += [project/'Packages/manifest.json', project/'Packages/packages-lock.json']
    return {path.relative_to(project).as_posix():sha(path.read_bytes()) for path in paths
            if path.is_file() and path != Path(str(fixture)+'.meta') and not path.is_relative_to(fixture)}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--project', type=Path, required=True)
    parser.add_argument('--project-id', required=True)
    parser.add_argument('--relay', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    parser.add_argument('--expected-version', required=True)
    args = parser.parse_args()
    if os.name != 'nt' or not ctypes.windll.shell32.IsUserAnAdmin():
        parser.error('Run from an Administrator terminal.')
    args.project = args.project.resolve(strict=True)
    args.report = args.report.resolve()
    args.report.parent.mkdir(parents=True, exist_ok=True)
    sys.dont_write_bytecode = True
    suite = Path(__file__).resolve().parent
    from OwnedFixture import cleanup_owned_fixture
    spec = importlib.util.spec_from_file_location('mcp', suite.parent/'McpSmokeRegression/run_mcp_smoke_regression.py')
    mcp = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mcp)
    name = '__UniBridgeUIToolkit_' + uuid.uuid4().hex
    relative_root = 'Assets/' + name
    fixture = args.project/relative_root
    manifest = args.project/'Library/UniBridge/UIToolkitRegression'/name/'ownership-manifest.json'
    sources, companions, directories, source_alternatives = {}, {}, set(), {}
    checks, responses = [], {}
    before = protected_before = None
    approved_cleanup = False

    def persist():
        manifest.parent.mkdir(parents=True, exist_ok=True)
        manifest.write_text(json.dumps({'owner':'UniBridge UIToolkitRegression', 'fixtureRoot':relative_root,
            'sourceKnownHashes':sources, 'metadataObservedHashes':companions, 'directories':sorted(directories),
            'sourceAuthorizedCandidateHashes':source_alternatives,
            'policy':'No author paths; paired source+metadata hashes required for cleanup.'},indent=2),encoding='utf-8')

    def check(label, condition, detail=None):
        checks.append({'name':label, 'passed':bool(condition), 'detail':detail})
        if not condition:
            raise AssertionError(label + ': ' + str(detail))

    def raw_call(bridge, label, tool, arguments, expect_error=False):
        raw = bridge.send('tools/call', {'name':tool, 'arguments':arguments})['result']
        text = next(item['text'] for item in raw.get('content', []) if item.get('type') == 'text')
        envelope = json.loads(text)
        responses[label] = {'arguments':arguments, 'raw':raw, 'envelope':envelope}
        context = envelope.get('projectContext', {})
        check('Project identity '+label, not context.get('id') or context['id'] == args.project_id, context)
        check('Truthful MCP error flag '+label, (envelope.get('success') is False) == (raw.get('isError') is True), raw)
        if expect_error:
            check('Rejected '+label, envelope.get('success') is False, envelope)
        elif envelope.get('success') is False:
            raise RuntimeError(label + ': ' + str(envelope))
        return envelope.get('data', envelope)

    def command(bridge, label, action, path=None, expect_error=False, **parameters):
        arguments = dict(parameters, Action=action)
        if path is not None:
            arguments['Path'] = path
        return raw_call(bridge, label, 'UniBridge_ManageUIToolkit', arguments, expect_error)

    def no_write(data, path, previous=None):
        full = args.project/path
        check('No write/import '+path, data.get('written') is False and data.get('imported') is False, data)
        check('Source unchanged '+path, full.read_bytes() == previous if previous is not None else not full.exists())

    def claim_generated_companions():
        # Metadata is observed only alongside an unchanged, known fixture source.
        for relative, expected in sources.items():
            source = args.project/relative
            if source.is_file() and sha(source.read_bytes()) == expected:
                metadata = Path(str(source)+'.meta')
                if metadata.is_file() and relative+'.meta' not in companions:
                    companions[relative+'.meta'] = sha(metadata.read_bytes())
        if fixture.exists():
            directories.add(relative_root)
            metadata = Path(str(fixture)+'.meta')
            if metadata.is_file() and relative_root+'.meta' not in companions:
                companions[relative_root+'.meta'] = sha(metadata.read_bytes())
        persist()

    def create(bridge, label, action, path, content):
        check('Create confined to unique root '+label, path.startswith(relative_root+'/'))
        check('New source and metadata absent '+label, not (args.project/path).exists() and not (args.project/(path+'.meta')).exists())
        # Expectations are registered before the tool writes known UTF-8 source.
        sources[path] = sha(content.encode('utf-8'))
        persist()
        data = command(bridge, label, action, path, Content=content)
        check('Actual import completed '+label, data.get('status') == 'completed' and data.get('written') is True
              and data.get('imported') is True and data.get('import', {}).get('checked') is True
              and data.get('import', {}).get('assetLoaded') is True, data)
        check('Exact payload/readback '+label, (args.project/path).read_bytes() == content.encode('utf-8')
              and data.get('currentSha256') == sources[path] and data.get('bytes') == len(content.encode('utf-8')), data)
        claim_generated_companions()
        return data

    def register_candidate(path,content):
        # Register intended bytes BEFORE mutation; an unexpected native success
        # can be cleaned only when these exact bytes and original metadata remain.
        check('Candidate belongs to declared unchanged source '+path,path in sources
              and sha((args.project/path).read_bytes()) == sources[path])
        metadata=path+'.meta'
        check('Candidate original metadata precondition '+path,metadata in companions
              and sha((args.project/metadata).read_bytes()) == companions[metadata])
        choices=source_alternatives.setdefault(path,[])
        candidate=sha(content.encode('utf-8'))
        for known in (sources[path],candidate):
            if known not in choices:
                choices.append(known)
        persist()

    uxml = '<ui:UXML xmlns:ui="UnityEngine.UIElements"><ui:VisualElement name="root"><ui:Label name="title" text="Привіт &amp; hello" /></ui:VisualElement></ui:UXML>'
    binding_uxml='<ui:UXML xmlns:ui="UnityEngine.UIElements"><ui:Label text="example"><Bindings><ui:DataBinding property="text" data-source-path="PlayerName" /></Bindings></ui:Label></ui:UXML>'
    uss = '.root { --panel-color: #125684; background-color: var(--panel-color); width: 120px; -unity-font-style: bold; }'
    warning = '.root { unibridge-intentionally-unknown-property: 17px; }'
    invalid_uxml = {'xml':'<UXML><VisualElement></UXML>',
        'dtd':"<!DOCTYPE UXML [<!ENTITY x 'expanded'>]><UXML>&x;</UXML>",
        'namespace':'<UXML xmlns="not.Unity" />'}

    try:
        check('Unique fixture initially absent', not fixture.exists() and not Path(str(fixture)+'.meta').exists())
        with mcp.McpClient(args.relay, args.project, 120, args.report.with_suffix('.trace.log')) as bridge:
            before = raw_call(bridge, 'initial-state', 'UniBridge_ContextSnapshot', {'Depth':'Brief', 'IncludeSelection':True})
            check('Exact project/package', before['project']['id'] == args.project_id and before['package']['version'] == args.expected_version)
            check('Editor idle', not any(before['editor'][key] for key in ('isCompiling','isUpdating','isPlaying')))
            session = raw_call(bridge, 'author-session', 'UniBridge_WorkSession', {'Action':'Status'})
            check('No author WorkSession active', session.get('active') is False)
            protected_before = author_files(args.project, fixture)
            approved_cleanup = True
            for preview in (True, False):
                path = relative_root+'/styles.txt'
                result = command(bridge, 'wrong-extension-'+str(preview), 'CreateStyleSheet', path,
                    expect_error=True, Content=uss, Preview=preview)
                no_write(result, path)
                check('Wrong extension creates no parent/meta '+str(preview), not fixture.exists()
                      and not Path(str(fixture)+'.meta').exists() and not (args.project/(path+'.meta')).exists())
            for action, extension, content in [('CreateDocument','uxml',uxml),('CreateStyleSheet','uss',uss)]:
                path = relative_root+'/preview.'+extension
                result = command(bridge, 'valid-preview-'+extension, action, path, Content=content, Preview=True)
                no_write(result, path)
                check('Structural preview has no semantic assertion '+extension,
                      result.get('status') == 'preview' and result['validation']['scope'] == 'structural'
                      and result['validation']['semanticValidation'] == 'not_run' and not fixture.exists(), result)
            for label, content in invalid_uxml.items():
                for preview in (True, False):
                    path = relative_root+'/invalid-'+label+'.uxml'
                    result = command(bridge, 'invalid-'+label+'-'+str(preview), 'CreateDocument', path,
                        expect_error=True, Content=content, Preview=preview)
                    no_write(result, path)
                    check('Invalid XML creates no parent '+label, not fixture.exists())
            doc_path, style_path = relative_root+'/document.uxml', relative_root+'/styles.uss'
            create(bridge, 'create-valid-uxml', 'CreateDocument', doc_path, uxml)
            create(bridge, 'create-valid-uss', 'CreateStyleSheet', style_path, uss)
            binding_path=relative_root+'/binding.uxml'
            binding_validation=command(bridge,'validate-runtime-binding','ValidateUxml',None,Content=binding_uxml)
            check('Documented runtime binding structurally accepted',binding_validation['validation']['valid']
                  and binding_validation['validation']['semanticValidation'] == 'not_run',binding_validation)
            binding_import=create(bridge,'create-runtime-binding','CreateDocument',binding_path,binding_uxml)
            check('Runtime binding actual Unity import accepted within scope',binding_import['import']['semanticValidationScope'] == 'unity_import'
                  and binding_import['import']['instantiationValidation'] == 'not_run',binding_import)
            doc_meta = sha((args.project/(doc_path+'.meta')).read_bytes())
            style_meta = sha((args.project/(style_path+'.meta')).read_bytes())
            for action in ('AddElement','SetClasses','SetInlineStyle'):
                result = command(bridge, 'existing-preview-'+action, action, doc_path, Preview=True,
                    ParentName='root',ElementName='title',ElementType='Label',Text='preview',Classes=['preview'],Style='width:12px')
                no_write(result, doc_path, uxml.encode())
                check('Existing preview retains exact metadata '+action, sha((args.project/(doc_path+'.meta')).read_bytes()) == doc_meta)
            result = command(bridge, 'existing-invalid-inline', 'SetInlineStyle', doc_path,
                expect_error=True, ElementName='title',Style='width:;')
            no_write(result, doc_path, uxml.encode())
            for label, content in invalid_uxml.items():
                result = command(bridge, 'existing-invalid-'+label, 'CreateDocument', doc_path, expect_error=True, Content=content)
                no_write(result, doc_path, uxml.encode())
                check('Existing invalid input metadata preserved '+label, sha((args.project/(doc_path+'.meta')).read_bytes()) == doc_meta)
            missing_style,missing_template='missing.uss','missing.uxml'
            check('Negative UXML dependencies absent',not (fixture/missing_style).exists() and not (fixture/missing_template).exists())
            semantic_uxml_cases={
                'missing-style':'<ui:UXML xmlns:ui="UnityEngine.UIElements"><ui:Style src="missing.uss"/><ui:VisualElement/></ui:UXML>',
                'missing-template':'<ui:UXML xmlns:ui="UnityEngine.UIElements"><ui:Template name="missing" src="missing.uxml"/><ui:Instance template="missing"/></ui:UXML>',
                'undefined-instance':'<ui:UXML xmlns:ui="UnityEngine.UIElements"><ui:Instance template="undefined"/></ui:UXML>'}
            for label,semantic_uxml in semantic_uxml_cases.items():
                preflight=command(bridge,'semantic-negative-preflight-uxml-'+label,'ValidateUxml',None,Content=semantic_uxml)
                check('UXML dependency negative remains structural-only '+label,preflight['validation']['valid']
                      and preflight['validation']['semanticValidation'] == 'not_run',preflight)
                register_candidate(doc_path,semantic_uxml)
                result=command(bridge,'actual-import-negative-uxml-'+label,'CreateDocument',doc_path,expect_error=True,Content=semantic_uxml)
                check('Actual Unity rejects UXML dependency '+label,result.get('written') is True and result.get('imported') is False
                      and result.get('restoredBaseline') is True and result.get('status') == 'restored_baseline'
                      and (result.get('import',{}).get('hasErrors') or result.get('import',{}).get('hasWarnings'))
                      and result['import']['semanticValidationScope'] == 'unity_import'
                      and result['import']['instantiationValidation'] == 'not_run',result)
                check('UXML importer failure restores known source/meta '+label,(args.project/doc_path).read_bytes() == uxml.encode()
                      and sha((args.project/(doc_path+'.meta')).read_bytes()) == doc_meta)
            for label, content in [('selector','[name="x"] {width:12px;}'),('value','.root {width:potato;}')]:
                preflight = command(bridge, 'semantic-negative-preflight-'+label,'ValidateUss',None,Content=content)
                check('Semantic negative is honestly structural-only '+label, preflight['validation']['valid']
                      and preflight['validation']['semanticValidation'] == 'not_run', preflight)
                register_candidate(style_path,content)
                result = command(bridge, 'actual-import-negative-'+label, 'CreateStyleSheet', style_path, expect_error=True, Content=content)
                check('Actual Unity diagnostic rejects '+label, result.get('written') is True and result.get('imported') is False
                      and result.get('restoredBaseline') is True and result.get('status') == 'restored_baseline'
                      and (result.get('import', {}).get('hasErrors') or result.get('import', {}).get('hasWarnings')), result)
                check('Known existing source/meta restored '+label, (args.project/style_path).read_bytes() == uss.encode()
                      and sha((args.project/(style_path+'.meta')).read_bytes()) == style_meta)
            register_candidate(style_path,warning)
            result = command(bridge, 'warning-default-refused', 'CreateStyleSheet', style_path, expect_error=True, Content=warning)
            check('Real Unity warning rejected by default', result.get('written') is True and result.get('imported') is False
                  and result.get('restoredBaseline') is True and result.get('status') == 'restored_baseline'
                  and result['import']['hasWarnings'] is True and result['import']['hasErrors'] is False, result)
            check('Warning refusal restores source/meta', (args.project/style_path).read_bytes() == uss.encode()
                  and sha((args.project/(style_path+'.meta')).read_bytes()) == style_meta)
            check('Before optional warning source still ours', sha((args.project/style_path).read_bytes()) == sources[style_path])
            sources[style_path] = sha(warning.encode())
            persist()
            result = command(bridge, 'warning-explicit-accepted', 'CreateStyleSheet', style_path, Content=warning, FailOnImportWarnings=False)
            check('Explicit warning opt-in truthful', result.get('written') is True and result.get('imported') is True
                  and result.get('status') == 'completed_with_warnings' and result['import']['hasWarnings'] is True
                  and result['import']['semanticValidation'] == 'warnings' and result['validation']['semanticValidation'] == 'not_run', result)
            check('Warning source exact known bytes', (args.project/style_path).read_bytes() == warning.encode())
            sources[style_path] = sha(uss.encode())
            persist()
            result = command(bridge,'restore-owned-valid-style','CreateStyleSheet',style_path,Content=uss)
            check('Own valid style restored/imported',result.get('status') == 'completed' and result.get('imported') is True
                  and (args.project/style_path).read_bytes() == uss.encode())
            after = raw_call(bridge, 'after-fixtures-state', 'UniBridge_ContextSnapshot', {'Depth':'Brief','IncludeSelection':True})
            check('Author scene/selection/Prefab state preserved', before['scenes'] == after['scenes']
                  and before['selection'] == after['selection'] and before.get('prefabStage') == after.get('prefabStage'))
            check('All author bytes and file set preserved', author_files(args.project, fixture) == protected_before)
    except Exception as error:
        checks.append({'name':'qualification exception','passed':False,'detail':repr(error)})
    finally:
        try:
            if approved_cleanup:
                claim_generated_companions()
                removed=cleanup_owned_fixture(args.project,fixture,sources,companions,source_alternatives)
                check('Paired ownership cleanup completed',not fixture.exists() and not Path(str(fixture)+'.meta').exists(),removed)
                with mcp.McpClient(args.relay,args.project,120,args.report.with_suffix('.cleanup.trace.log')) as bridge:
                    raw_call(bridge,'cleanup-refresh','UniBridge_ManageEditor',{'Action':'RefreshAssets','WaitForCompletion':True,'Force':True})
                    health=raw_call(bridge,'final-compile-health','UniBridge_ManageEditor',{'Action':'GetCompilationDiagnostics'})
                    check('Final compile healthy/fresh',health['compileHealth']['healthy'] and not health['assemblyFreshness']['staleLikely'],health)
                    final=raw_call(bridge,'final-state','UniBridge_ContextSnapshot',{'Depth':'Brief','IncludeSelection':True})
                    check('Final author state preserved',before['scenes'] == final['scenes'] and before['selection'] == final['selection']
                          and before.get('prefabStage') == final.get('prefabStage'))
                    check('Final all author bytes/file set preserved',author_files(args.project,fixture) == protected_before)
        except Exception as error:
            checks.append({'name':'owned cleanup exception','passed':False,'detail':repr(error)})
        report={'project':str(args.project),'projectId':args.project_id,'expectedVersion':args.expected_version,
            'administrator':True,'fixtureRoot':relative_root,'manifest':str(manifest),'checks':checks,'responses':responses,
            'protectedAuthorFileCount':len(protected_before or {}),'passed':sum(c['passed'] for c in checks),
            'failed':sum(not c['passed'] for c in checks),'fixtureRemoved':not fixture.exists() and not Path(str(fixture)+'.meta').exists(),
            'scope':'Actual Unity importer; no authored scenes/assets saved, no Play mode, no Console history clear.'}
        args.report.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
        print(json.dumps({k:report[k] for k in ('passed','failed','fixtureRemoved','protectedAuthorFileCount')}))
    return 0 if report['failed'] == 0 and report['fixtureRemoved'] else 1


if __name__=='__main__':
    raise SystemExit(main())
