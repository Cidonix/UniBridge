"""Independent Draft2020-12 counterproofs plus actual whole-scheduler preservation."""
import argparse
import copy
import ctypes
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import uuid
from pathlib import Path


def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--repo', type=Path, required=True)
    parser.add_argument('--candidate', type=Path, required=True)
    parser.add_argument('--core-report', type=Path, required=True)
    parser.add_argument('--build', type=Path, required=True, help='owned build directory reported by run_package_management_regression')
    parser.add_argument('--validator-path', type=Path, help='existing jsonschema4.25.1 dependency directory; no downloads are performed')
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args(); sys.stdout.reconfigure(encoding='utf-8')
    if os.name != 'nt' or not ctypes.windll.shell32.IsUserAnAdmin(): raise SystemExit('Administrator token required')
    if args.report.exists(): raise SystemExit('Choose a fresh evidence suffix')
    if args.validator_path: sys.path.insert(0, str(args.validator_path.resolve(strict=True)))
    from jsonschema import Draft202012Validator
    report = json.loads(args.core_report.read_text(encoding='utf-8-sig'))
    checks = []
    def check(name, passed):
        checks.append({'name': name, 'passed': bool(passed)})
        if not passed: raise AssertionError(name)
    schemas = report['outputSchemas']
    validators = {}
    for action, schema in schemas.items():
        Draft202012Validator.check_schema(schema); validators[action] = Draft202012Validator(schema)
        check('closed-family-' + action, schema['type'] == 'object')
    input_validator = Draft202012Validator(report['inputSchema']); Draft202012Validator.check_schema(report['inputSchema'])
    unknown = Draft202012Validator(report['unknownActionSchema'])
    for index, row in enumerate(report['responses']):
        validator = validators.get(row['action'], unknown)
        check('whole-service-response-' + str(index), validator.is_valid(row['response']))
    Draft202012Validator.check_schema(report['registeredOutputSchema'])
    registered = Draft202012Validator(report['registeredOutputSchema'])
    for index, row in enumerate(report['registeredResponses']):
        check('actual-registered-contract-response-' + str(index), registered.is_valid(row['response']))
    source = next(row['response'] for row in report['responses'] if row['action'] == 'List' and row['response']['success'])
    def reject(name, mutate):
        value = copy.deepcopy(source); mutate(value)
        check('counterproof-' + name, not validators['List'].is_valid(value))
    reject('unknown-envelope-property', lambda value: value.__setitem__('unexpected', True))
    reject('wrong-action-discriminator', lambda value: value['data'].__setitem__('action', 'Add'))
    reject('unknown-business-property', lambda value: value['data'].__setitem__('unexpected', True))
    reject('missing-required-business-state', lambda value: value['data'].pop('requestCompleted'))
    reject('wrong-editor-readiness-type', lambda value: value['data']['editor'].__setitem__('readinessRequiresFollowUp', 'ready'))
    reject('wrong-package-version-type', lambda value: value['data']['packages'][0].__setitem__('version', 123))
    reject('unknown-package-property', lambda value: value['data']['packages'][0].__setitem__('unbounded', {}))
    reject('over-budget-packages', lambda value: value['data'].__setitem__('packages', [value['data']['packages'][0]] * 101))
    reject('over-budget-compatible-versions', lambda value: value['data']['packages'][0].__setitem__('compatibleVersions', ['1.2.3'] * 31))
    reject('over-budget-dependencies', lambda value: value['data']['packages'][0].__setitem__('dependencies', [{'name':'com.owned.x','version':'1.0.0'}] * 61))
    for name, value in [('unknown-input-field', {'Force': True}), ('unknown-action', {'Action':'Remove'}), ('numeric-input-action', {'Action':3}), ('negative-index', {'SampleIndex':-1}), ('too-large-limit', {'MaxResults':101}), ('timeout-too-small', {'TimeoutMs':999})]:
        check('input-counterproof-' + name, not input_validator.is_valid(value))
    check('input-default-list', input_validator.is_valid({}))
    check('input-exact-add', input_validator.is_valid({'Action':'Add','PackageId':'com.cidonix.qualifier@1.2.3'}))
    # Compile both complete schedulers, then compare every retained old-tool policy/annotation.
    relative = Path('com.cidonix.unibridge/Modules/Cidonix.UniBridge.MCP.Editor/ToolRegistry/ToolExecutionScheduler.cs')
    baseline = args.repo / relative; candidate = args.candidate / relative
    names = sorted(set(re.findall(r'"(UniBridge_[A-Za-z0-9_]+)"', baseline.read_text(encoding='utf-8-sig'))) - {'UniBridge_ManagePackages'})
    actions = sorted(set(re.findall(r'"([A-Z][A-Za-z0-9]+)"', baseline.read_text(encoding='utf-8-sig')))) + ['unknown', '0', '1', '2', '3', '4', '5']
    targets = []
    for name in names:
        targets.append({'tool':name,'parameters':None})
        for action in actions:
            for key in ('Action','action','Operation','operation'):
                for dry_run in (False,True): targets.append({'tool':name,'parameters':{key:action,'DryRun':dry_run}})
    work = args.report.resolve().parent / ('policy-preservation-' + uuid.uuid4().hex); work.mkdir()
    targets_path = work / 'targets.json'; targets_path.write_text(json.dumps(targets), encoding='utf-8')
    outputs = []
    for label, scheduler in [('baseline',baseline),('candidate',candidate)]:
        build = work / label; build.mkdir()
        for source_path in args.build.glob('*.cs'): shutil.copyfile(source_path, build / source_path.name)
        shutil.copyfile(scheduler, build / 'ToolExecutionScheduler.cs')
        shutil.copyfile(Path(__file__).resolve().parent / 'PolicySnapshot.cs', build / 'PolicySnapshot.cs')
        project = (args.build / 'PackageRegression.csproj').read_text(encoding='utf-8')
        project = project.replace('PackageManagementRegression.Regression','PackageManagementRegression.PolicySnapshot')
        (build / 'PolicySnapshot.csproj').write_text(project, encoding='utf-8')
        shutil.copyfile(args.build / 'NuGet.Config', build / 'NuGet.Config')
        output = build / 'policies.json'
        process = subprocess.run(['dotnet','run','--project',str(build / 'PolicySnapshot.csproj'),'--configuration','Release','--',str(targets_path),str(output)], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding='utf-8', errors='replace', timeout=90)
        (build / 'build.log').write_text(process.stdout, encoding='utf-8')
        if process.returncode: raise RuntimeError(label + '\n' + process.stdout[-2000:])
        outputs.append(json.loads(output.read_text(encoding='utf-8-sig')))
    check('all-existing-tool-policy-and-annotation-preserved', outputs[0] == outputs[1])
    value = {'passed':True,'administrator':True,'nativeEditorExecution':False,'checks':checks,'checkCount':len(checks),'existingPolicyRows':len(targets),'existingToolCount':len(names),
             'sources':{str(baseline.resolve()):sha(baseline),str(candidate.resolve()):sha(candidate),str(args.core_report.resolve()):sha(args.core_report)},
             'proof':'Independent JSON Schema validator plus two complete production scheduler compilations. Business responses came from deterministic native request seams, not a native Editor.', 'policyEvidenceDirectory':str(work)}
    with args.report.open('x',encoding='utf-8') as out: json.dump(value,out,indent=2)
    print(json.dumps({'passed':True,'checkCount':len(checks),'existingPolicyRows':len(targets),'existingToolCount':len(names)}))


if __name__ == '__main__': main()
