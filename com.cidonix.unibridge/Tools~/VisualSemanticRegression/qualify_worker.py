"""Portable offline native worker gate; fixture/model inputs are explicit and read-only."""
import argparse
import ctypes
import hashlib
import json
import math
import os
import subprocess
import time
import uuid
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--worker', type=Path, required=True)
    parser.add_argument('--fixtures', type=Path, default=Path(__file__).resolve().parent / 'fixtures')
    parser.add_argument('--model-directory', type=Path)
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    if os.name == 'nt' and not ctypes.windll.shell32.IsUserAnAdmin():
        raise SystemExit('Qualification must be launched as administrator.')
    if args.report.exists():
        raise SystemExit('Use a fresh report path; existing evidence is never overwritten.')
    worker = args.worker.resolve(strict=True)
    fixtures = args.fixtures.resolve(strict=True)
    manifest = json.loads((fixtures / 'fixtures-manifest.json').read_text(encoding='utf-8-sig'))
    for artifact in manifest['artifacts']:
        name = artifact['name']
        if Path(name).name != name:
            raise ValueError('A fixture manifest entry must be a single file name.')
        data = (fixtures / name).read_bytes()
        assert len(data) == artifact['bytes'] and hashlib.sha256(data).hexdigest() == artifact['sha256'], name
    started = time.monotonic()
    report = {'passed': False, 'kind': 'native-independent-ORT-worker-qualification',
        'admin': os.name == 'nt', 'fixtureManifestSha256': hashlib.sha256((fixtures / 'fixtures-manifest.json').read_bytes()).hexdigest(),
        'fixtureSource': 'independent procedural opaque-file drawings; no asset-name matching',
        'modelCache': 'explicit override' if args.model_directory else 'documented default user cache',
        'checks': [], 'offlineQueryCalls': 8, 'networkRequired': False}

    def save():
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding='utf-8')

    def check(name, value, **evidence):
        report['checks'].append({'name': name, 'passed': bool(value), **evidence})
        save()
        if not value:
            raise AssertionError(name)

    def request(action, identity, **arguments):
        command = {'action': action, 'requestId': identity, 'deadlineMs': 15000, **arguments}
        if args.model_directory and 'modelDirectory' not in command:
            command['modelDirectory'] = str(args.model_directory.resolve())
        completed = subprocess.run([str(worker), '--stdio'], input=json.dumps(command) + '\n',
            text=True, capture_output=True, timeout=25,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)
        assert len(completed.stdout) <= 32768 and len(completed.stderr) <= 8192, 'Worker output budget'
        output = json.loads(completed.stdout)
        assert output.get('requestId') == identity, output
        return output

    def embedding(output):
        assert output.get('success') is True, output
        vector = output['embedding']
        assert len(vector) == 512 and all(math.isfinite(value) for value in vector)
        assert abs(sum(value * value for value in vector) - 1) < .00001
        return vector

    try:
        images = []
        for index in range(4):
            output = request('encode_image', f'image-{index}', tensorFile=str(fixtures / f'{index + 101}.tensor'), jobRoot=str(fixtures))
            images.append(embedding(output))
            report['modelId'] = output['modelId']
            report['modelFingerprint'] = output['modelFingerprint']
        report['embeddingDimensions'] = 512
        for query, expected in [('a drawing of a red car', 0), ('a drawing of a green tree', 1),
                ('a drawing of a red apple', 2), ('a drawing of a yellow banana', 3)]:
            vector = embedding(request('encode_text', f'query-{expected}', text=query))
            scores = [sum(a * b for a, b in zip(vector, image)) for image in images]
            order = sorted(range(4), key=lambda index: scores[index], reverse=True)
            check('semantic-' + str(expected + 101), order[0] == expected, query=query, scores=scores,
                firstOpaqueFile=f'{order[0] + 101}.png', margin=scores[order[0]] - scores[order[1]])
        token = request('tokenize', 'token-contract', text='a red car')
        check('independent-public-CLIP-token-contract', token.get('success') and token['tokenIds'][:5] == [49406, 320, 736, 1615, 49407], tokenIds=token.get('tokenIds'))
        negative = request('install', 'no-download-permission', allowDownload=False)
        check('explicit-model-download-authorization', not negative['success'] and 'MODEL_DOWNLOAD_NOT_AUTHORIZED' in negative.get('error', ''), response=negative)
        invalid = request('encode_image', 'outside-job-root', tensorFile=str(worker), jobRoot=str(fixtures))
        check('tensor-job-root-containment', not invalid['success'], response=invalid)
        empty = request('encode_text', 'empty-query', text='')
        check('empty-query-error', not empty['success'], response=empty)
        missing_directory = args.report.resolve().parent / ('missing-model-' + uuid.uuid4().hex)
        assert not missing_directory.exists()
        missing = request('encode_text', 'unready-model', text='a red car', modelDirectory=str(missing_directory))
        check('unready-model-explicit-error', not missing['success'] and missing['code'] == 'model_not_ready', response=missing)
        deadline = request('encode_text', 'short-deadline', text='a red car', deadlineMs=100)
        check('native-model-deadline', not deadline['success'] and deadline['code'] == 'deadline_exceeded', response=deadline)
        report['passed'] = all(check['passed'] for check in report['checks'])
    except Exception as error:
        report['error'] = repr(error)
    finally:
        report['elapsedSeconds'] = time.monotonic() - started
        save()
    print(json.dumps({'passed': report['passed'], 'checks': len(report['checks']), 'report': str(args.report), 'error': report.get('error')}))
    if not report['passed']:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
