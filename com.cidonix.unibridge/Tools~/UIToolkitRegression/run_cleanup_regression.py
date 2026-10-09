"""Qualify refusal before deletion using exclusively new temporary fixtures."""
import argparse
import ctypes
import hashlib
import json
import os
import tempfile
import uuid
from pathlib import Path
from OwnedFixture import cleanup_owned_fixture


def sha(value):
    return hashlib.sha256(value).hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--report',type=Path,required=True)
    args = parser.parse_args()
    if os.name != 'nt' or not ctypes.windll.shell32.IsUserAnAdmin():
        parser.error('Run from an Administrator terminal.')
    cases=[]
    for mode in ('clean','source_changed','meta_changed','unknown_file','unknown_directory','unknown_outer_meta','outside_path','unpaired_meta','known_candidate','candidate_meta_changed','unknown_candidate_bytes'):
        with tempfile.TemporaryDirectory(prefix='unibridge-uitoolkit-cleanup-') as temporary:
            project=Path(temporary)
            fixture=project/'Assets'/('__UniBridgeUIToolkit_'+uuid.uuid4().hex)
            fixture.mkdir(parents=True)
            source=fixture/'test.uss'
            source.write_bytes(b'own known source')
            metadata=Path(str(source)+'.meta')
            metadata.write_bytes(b'own known metadata')
            outer=Path(str(fixture)+'.meta')
            outer.write_bytes(b'own folder metadata')
            sources={source.relative_to(project).as_posix():sha(source.read_bytes())}
            companions={metadata.relative_to(project).as_posix():sha(metadata.read_bytes()),outer.relative_to(project).as_posix():sha(outer.read_bytes())}
            alternatives={}
            if mode=='source_changed':source.write_bytes(b'independent latest source')
            if mode=='meta_changed':metadata.write_bytes(b'independent latest metadata')
            if mode=='unknown_file':(fixture/'author.txt').write_bytes(b'independent file')
            if mode=='unknown_directory':(fixture/'author-folder').mkdir()
            if mode=='unknown_outer_meta':companions.pop(outer.relative_to(project).as_posix())
            if mode=='outside_path':
                author=project/'author.txt';author.write_bytes(b'outside authored bytes')
                sources['author.txt']=sha(author.read_bytes())
            if mode=='unpaired_meta':sources.clear()
            if mode in ('known_candidate','candidate_meta_changed','unknown_candidate_bytes'):
                alternatives={source.relative_to(project).as_posix():[sha(b'known intended candidate')]}
                source.write_bytes(b'known intended candidate' if mode!='unknown_candidate_bytes' else b'independent unrecognized bytes')
                if mode=='candidate_meta_changed':metadata.write_bytes(b'independent changed metadata')
            before={p.relative_to(project).as_posix():sha(p.read_bytes()) for p in project.rglob('*') if p.is_file()}
            try:
                cleanup_owned_fixture(project,fixture,sources,companions,alternatives)
                if mode not in ('clean','known_candidate'):raise AssertionError('Unsafe cleanup accepted '+mode)
                if fixture.exists() or outer.exists():raise AssertionError('Known owned fixture not cleaned.')
                cases.append({'name':mode,'passed':True})
            except ValueError as error:
                after={p.relative_to(project).as_posix():sha(p.read_bytes()) for p in project.rglob('*') if p.is_file()}
                cases.append({'name':mode,'passed':mode not in ('clean','known_candidate') and before==after and fixture.exists(),'detail':str(error)})
            except Exception as error:
                cases.append({'name':mode,'passed':False,'detail':repr(error)})
    report={'administrator':True,'passed':sum(c['passed'] for c in cases),'failed':sum(not c['passed'] for c in cases),'cases':cases,
            'productionCleanupSha256':sha(Path(__file__).with_name('OwnedFixture.py').read_bytes()),'scope':'Unique temporary fake project only; no real Unity project accessed.'}
    args.report.resolve().parent.mkdir(parents=True,exist_ok=True)
    args.report.write_text(json.dumps(report,indent=2),encoding='utf-8')
    print(json.dumps({'passed':report['passed'],'failed':report['failed']}))
    return 0 if not report['failed'] else 1


if __name__=='__main__':
    raise SystemExit(main())
