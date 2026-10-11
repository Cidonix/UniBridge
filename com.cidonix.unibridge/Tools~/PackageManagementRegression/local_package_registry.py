"""Owned loopback-only exact-version UPM fixture and hash/structure guarded restoration.

No Unity or MCP call is made by this driver. Only prepare/restore-manifest explicitly
touch a selected project's package files; every other operation stays in fresh work.
"""
import argparse
import base64
import ctypes
import hashlib
import http.server
import io
import json
import os
import sys
import tarfile
import threading
import urllib.parse
import urllib.request
import uuid
from pathlib import Path


def administrator():
    if os.name != 'nt' or not ctypes.windll.shell32.IsUserAnAdmin(): raise RuntimeError('Administrator token required')


def digest(raw): return hashlib.sha256(raw).hexdigest()
def read_json(path): return json.loads(path.read_bytes().decode('utf-8-sig'))
def write_new(path, value):
    with path.open('x', encoding='utf-8') as out: json.dump(value, out, indent=2)
def json_bytes(value): return (json.dumps(value, indent=2) + '\n').encode('utf-8')


def create_fixture(work):
    work = work.resolve(); work.mkdir(parents=True, exist_ok=False)
    identity = uuid.uuid4().hex
    name = 'com.cidonix.unibridge.qualifier.p' + identity
    display = 'UniBridge Qualifier ' + identity
    sample_guid = uuid.uuid4().hex
    version = '1.2.3'
    package = {'name':name,'version':version,'displayName':display,'description':'Owned local package management qualifier; no external dependency.',
               'unity':'2020.3','dependencies':{},'samples':[{'displayName':'Owned Demo','description':'Byte/GUID preservation fixture.','path':'Samples~/Demo'}]}
    files = {'package.json':json_bytes(package),
             'Runtime/OwnedPackageMarker.cs':('namespace UniBridgePackageQualifier.P' + identity + ' { public static class OwnedPackageMarker { public const string Version = "1.2.3"; } }\n').encode('utf-8'),
             'Runtime/OwnedPackageMarker.cs.meta':('fileFormatVersion: 2\nguid: ' + uuid.uuid4().hex + '\nMonoImporter:\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {instanceID: 0}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n').encode('utf-8'),
             'Runtime/OwnedPackageQualifier.asmdef':json_bytes({'name':'UniBridgePackageQualifier.P' + identity,'references':[]}),
             'Runtime/OwnedPackageQualifier.asmdef.meta':('fileFormatVersion: 2\nguid: ' + uuid.uuid4().hex + '\nAssemblyDefinitionImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n').encode('utf-8'),
             'Samples~/Demo/sample.txt':('Owned sample ' + identity + '\n').encode('utf-8'),
             'Samples~/Demo/sample.txt.meta':('fileFormatVersion: 2\nguid: ' + sample_guid + '\nDefaultImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n').encode('utf-8')}
    source = work / 'package'
    for relative, raw in files.items():
        path = source / relative; path.parent.mkdir(parents=True,exist_ok=True); path.write_bytes(raw)
    tarball = work / (name + '-' + version + '.tgz')
    with tarfile.open(tarball,'w:gz',format=tarfile.PAX_FORMAT) as archive:
        for relative, raw in sorted(files.items()):
            info = tarfile.TarInfo('package/' + relative); info.size = len(raw); info.mode = 0o644; info.mtime = 0
            archive.addfile(info,io.BytesIO(raw))
    raw = tarball.read_bytes()
    descriptor = {'schema':'unibridge.local-package-fixture.v1','name':name,'version':version,'exactId':name+'@'+version,'displayName':display,
                  'sampleGuid':sample_guid,'sampleIndex':0,'sampleRelativePath':'Samples~/Demo/sample.txt','tarball':tarball.name,
                  'tarballSha256':digest(raw),'shasum':hashlib.sha1(raw).hexdigest(),'integrity':'sha512-'+base64.b64encode(hashlib.sha512(raw).digest()).decode('ascii'),
                  'package':package,'files':{relative:digest(value) for relative,value in files.items()},'administrator':True}
    write_new(work/'fixture.json',descriptor)
    return descriptor


def server_for(work,port):
    fixture = read_json(work/'fixture.json'); tarball = work/fixture['tarball']
    if digest(tarball.read_bytes()) != fixture['tarballSha256']: raise RuntimeError('Owned tarball changed')
    class Handler(http.server.BaseHTTPRequestHandler):
        def log_message(self,*args): pass
        def do_HEAD(self): self.do_GET()
        def do_GET(self):
            path = urllib.parse.unquote(urllib.parse.urlsplit(self.path).path).rstrip('/')
            url = 'http://127.0.0.1:' + str(self.server.server_port)
            metadata = dict(fixture['package']); metadata['dist'] = {'tarball':url+'/'+fixture['name']+'/-/'+fixture['tarball'],'shasum':fixture['shasum'],'integrity':fixture['integrity']}
            document = {'name':fixture['name'],'dist-tags':{'latest':fixture['version']},'versions':{fixture['version']:metadata},'time':{'created':'2026-10-11T00:00:00.000Z','modified':'2026-10-11T00:00:00.000Z',fixture['version']:'2026-10-11T00:00:00.000Z'}}
            if path == '/'+fixture['name']+'/-/'+fixture['tarball']: raw=tarball.read_bytes(); content='application/octet-stream'
            elif path == '/'+fixture['name']: raw=json_bytes(document); content='application/json'
            elif path == '/'+fixture['name']+'/'+fixture['version']: raw=json_bytes(metadata); content='application/json'
            elif path.startswith('/-/v1/search'): raw=json_bytes({'objects':[{'package':metadata}],'total':1}); content='application/json'
            elif path == '/-/all': raw=json_bytes({'_updated':0,fixture['name']:dict(metadata,versions={fixture['version']:'latest'})}); content='application/json'
            elif path.startswith('/-/all/since'): raw=json_bytes({fixture['name']:metadata}); content='application/json'
            elif path in ('/','/-/ping'): raw=json_bytes({}); content='application/json'
            else: self.send_error(404); return
            self.send_response(200); self.send_header('Content-Type',content); self.send_header('Content-Length',str(len(raw))); self.end_headers()
            if self.command!='HEAD': self.wfile.write(raw)
    return http.server.ThreadingHTTPServer(('127.0.0.1',port),Handler)


def registry(fixture,url): return {'name':fixture['displayName'],'url':url,'scopes':[fixture['name']]}


def prepare_manifest(fixture_path,project,url,manifest_sha,lock_sha,receipt):
    fixture = read_json(fixture_path); project=project.resolve()
    if project.name not in ('UniBridge_Test_Project','OwnedMockProject'): raise RuntimeError('Driver is restricted to the approved Test project or its owned mock')
    manifest=project/'Packages/manifest.json'; lock=project/'Packages/packages-lock.json'
    before_manifest=manifest.read_bytes(); before_lock=lock.read_bytes()
    if digest(before_manifest)!=manifest_sha or digest(before_lock)!=lock_sha: raise RuntimeError('Author manifest/lock baseline SHA mismatch; no file was changed')
    original=read_json(manifest); original_lock=read_json(lock)
    if fixture['name'] in original.get('dependencies',{}) or fixture['name'] in original_lock.get('dependencies',{}): raise RuntimeError('Fixture package already exists')
    if any(fixture['name'] in entry.get('scopes',[]) for entry in original.get('scopedRegistries',[])): raise RuntimeError('Fixture scope already exists')
    parsed_url=urllib.parse.urlsplit(url)
    if parsed_url.scheme!='http' or parsed_url.hostname!='127.0.0.1' or not parsed_url.port: raise RuntimeError('Registry must be this loopback server')
    after=dict(original); after['scopedRegistries']=list(original.get('scopedRegistries',[]))+[registry(fixture,url)]
    expected=json_bytes(after)
    if receipt.exists(): raise RuntimeError('Receipt exists')
    backups=receipt.parent/(receipt.stem+'-backups'); backups.mkdir(parents=True,exist_ok=False)
    (backups/'manifest.json').write_bytes(before_manifest); (backups/'packages-lock.json').write_bytes(before_lock)
    value={'schema':'unibridge.owned-upm-manifest.v1','project':str(project),'fixturePath':str(fixture_path.resolve()),'registry':registry(fixture,url),'backups':str(backups),
           'baselineManifestSha256':digest(before_manifest),'baselineLockSha256':digest(before_lock),'preparedManifestSha256':digest(expected)}
    # Capture the intent before writing. Recheck physical baseline immediately before the only mutation.
    write_new(receipt,value)
    if manifest.read_bytes()!=before_manifest or lock.read_bytes()!=before_lock: raise RuntimeError('Concurrent package file changed; no write was made')
    manifest.write_bytes(expected)
    return value


def restore_manifest(receipt_path):
    receipt=read_json(receipt_path); project=Path(receipt['project']); backups=Path(receipt['backups']); fixture=read_json(Path(receipt['fixturePath']))
    manifest=project/'Packages/manifest.json'; lock=project/'Packages/packages-lock.json'
    before_manifest=manifest.read_bytes(); before_lock=lock.read_bytes()
    original_manifest=(backups/'manifest.json').read_bytes(); original_lock=(backups/'packages-lock.json').read_bytes()
    if digest(original_manifest)!=receipt['baselineManifestSha256'] or digest(original_lock)!=receipt['baselineLockSha256']: raise RuntimeError('Baseline backup bytes changed; restore blocked')
    current=json.loads(before_manifest.decode('utf-8-sig')); current_lock=json.loads(before_lock.decode('utf-8-sig'))
    original=json.loads(original_manifest.decode('utf-8-sig')); baseline_lock=json.loads(original_lock.decode('utf-8-sig'))
    expected=copy_document(original); expected['scopedRegistries']=list(original.get('scopedRegistries',[]))+[receipt['registry']]
    installed=current.get('dependencies',{}).get(fixture['name'])
    if installed is not None:
        if installed!=fixture['version']: raise RuntimeError('Installed fixture version changed; restore blocked')
        expected.setdefault('dependencies',{})[fixture['name']]=fixture['version']
    if current!=expected: raise RuntimeError('Author/concurrent manifest values changed; restore blocked')
    row=current_lock.get('dependencies',{}).get(fixture['name'])
    expected_lock=copy_document(baseline_lock)
    if row is not None:
        allowed={'version':fixture['version'],'depth':0,'source':'registry','dependencies':{},'url':receipt['registry']['url']}
        if row!=allowed: raise RuntimeError('Fixture lock row is not the known exact native registry shape; restore blocked')
        expected_lock.setdefault('dependencies',{})[fixture['name']]=allowed
    if current_lock!=expected_lock: raise RuntimeError('Author/concurrent lock values changed; restore blocked')
    if manifest.read_bytes()!=before_manifest or lock.read_bytes()!=before_lock: raise RuntimeError('Concurrent package file changed immediately before restore; no write')
    manifest.write_bytes(original_manifest); lock.write_bytes(original_lock)
    if manifest.read_bytes()!=original_manifest or lock.read_bytes()!=original_lock: raise RuntimeError('Restored bytes differ')
    return {'restored':True,'observedManifestSha256':digest(before_manifest),'observedLockSha256':digest(before_lock),'manifestSha256':digest(original_manifest),'lockSha256':digest(original_lock)}


def copy_document(value): return json.loads(json.dumps(value))


def self_test(work):
    fixture=create_fixture(work); server=server_for(work,0); thread=threading.Thread(target=server.serve_forever,daemon=True); thread.start()
    url='http://127.0.0.1:'+str(server.server_port); checks=[]
    def check(name,value):
        checks.append({'name':name,'passed':bool(value)})
        if not value: raise AssertionError(name)
    try:
        metadata=json.load(urllib.request.urlopen(url+'/'+fixture['name'],timeout=3))
        check('exact-version-metadata',metadata['versions'][fixture['version']]['version']==fixture['version'])
        raw=urllib.request.urlopen(metadata['versions'][fixture['version']]['dist']['tarball'],timeout=3).read()
        check('tarball-integrity',digest(raw)==fixture['tarballSha256'])
        with tarfile.open(fileobj=io.BytesIO(raw),mode='r:gz') as archive:
            check('sample-GUID-in-tarball',fixture['sampleGuid'].encode() in archive.extractfile('package/Samples~/Demo/sample.txt.meta').read())
        project=work/'OwnedMockProject'; (project/'Packages').mkdir(parents=True)
        manifest=project/'Packages/manifest.json'; lock=project/'Packages/packages-lock.json'
        original_manifest=b'{"dependencies":{"com.example.author":"7.8.9"},"authorField":"keep"}\r\n'
        original_lock=b'{"dependencies":{"com.example.author":{"version":"7.8.9","depth":0,"source":"registry","dependencies":{},"url":"https://example.invalid"}}}\r\n'
        manifest.write_bytes(original_manifest); lock.write_bytes(original_lock)
        try: prepare_manifest(work/'fixture.json',project,url,'wrong',digest(original_lock),work/'wrong-receipt.json'); raise AssertionError('wrong SHA accepted')
        except RuntimeError: check('wrong-baseline-does-not-change-author-bytes',manifest.read_bytes()==original_manifest and lock.read_bytes()==original_lock)
        prepare_manifest(work/'fixture.json',project,url,digest(original_manifest),digest(original_lock),work/'receipt.json')
        after=read_json(manifest); after['dependencies'][fixture['name']]=fixture['version']; manifest.write_bytes(json_bytes(after))
        after_lock=read_json(lock); after_lock['dependencies'][fixture['name']]={'version':fixture['version'],'depth':0,'source':'registry','dependencies':{},'url':url}; lock.write_bytes(json_bytes(after_lock))
        restore_manifest(work/'receipt.json')
        check('exact-author-manifest-lock-restored',manifest.read_bytes()==original_manifest and lock.read_bytes()==original_lock)
        prepare_manifest(work/'fixture.json',project,url,digest(original_manifest),digest(original_lock),work/'parallel-receipt.json')
        changed=read_json(manifest); changed['authorField']='concurrent author value'; manifest.write_bytes(json_bytes(changed)); author_bytes=manifest.read_bytes()
        try: restore_manifest(work/'parallel-receipt.json'); raise AssertionError('author change overwritten')
        except RuntimeError: check('parallel-author-value-blocks-restore',manifest.read_bytes()==author_bytes and lock.read_bytes()==original_lock)
        report={'passed':True,'administrator':True,'nativeUnityExecution':False,'checks':checks,'fixturePath':str(work/'fixture.json'),'proof':'Owned mock package manifests and actual loopback HTTP/npm tarball; no Unity or native UPM execution.'}
        write_new(work/'self-test.json',report); print(json.dumps({'passed':True,'checks':len(checks),'evidence':str(work/'self-test.json')}))
    finally: server.shutdown(); server.server_close(); thread.join(timeout=3)


def main():
    administrator(); sys.stdout.reconfigure(encoding='utf-8')
    parser=argparse.ArgumentParser(); sub=parser.add_subparsers(dest='mode',required=True)
    for name in ('create','self-test'): sub.add_parser(name).add_argument('--work',type=Path,required=True)
    serve=sub.add_parser('serve'); serve.add_argument('--work',type=Path,required=True); serve.add_argument('--ready-file',type=Path,required=True); serve.add_argument('--port',type=int,default=0)
    prepare=sub.add_parser('prepare-manifest'); prepare.add_argument('--fixture',type=Path,required=True); prepare.add_argument('--project',type=Path,required=True); prepare.add_argument('--registry-url',required=True); prepare.add_argument('--expected-manifest-sha',required=True); prepare.add_argument('--expected-lock-sha',required=True); prepare.add_argument('--receipt',type=Path,required=True)
    restore=sub.add_parser('restore-manifest'); restore.add_argument('--receipt',type=Path,required=True)
    args=parser.parse_args()
    if args.mode=='create': print(json.dumps(create_fixture(args.work)))
    elif args.mode=='self-test': self_test(args.work.resolve())
    elif args.mode=='prepare-manifest': print(json.dumps(prepare_manifest(args.fixture,args.project,args.registry_url,args.expected_manifest_sha,args.expected_lock_sha,args.receipt)))
    elif args.mode=='restore-manifest': print(json.dumps(restore_manifest(args.receipt)))
    else:
        server=server_for(args.work.resolve(),args.port)
        ready={'pid':os.getpid(),'url':'http://127.0.0.1:'+str(server.server_port),'fixturePath':str(args.work.resolve()/'fixture.json'),'administrator':True}
        write_new(args.ready_file,ready); print(json.dumps(ready),flush=True)
        try: server.serve_forever()
        finally: server.server_close()


if __name__=='__main__': main()
