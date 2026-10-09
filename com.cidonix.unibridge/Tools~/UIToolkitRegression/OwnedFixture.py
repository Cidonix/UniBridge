"""Nonrecursive cleanup restricted to a new UUID fixture and verified file pairs."""
import hashlib
import stat
from pathlib import Path


def cleanup_owned_fixture(project, fixture, sources, companions, known_source_alternatives=None):
    project, fixture = Path(project).resolve(), Path(fixture).resolve()
    if fixture.parent != project/'Assets' or not fixture.name.startswith('__UniBridgeUIToolkit_'):
        raise ValueError('Cleanup target is not the exact isolated Assets fixture.')
    declarations = dict(sources)
    declarations.update(companions)
    alternatives=known_source_alternatives or {}
    if not set(alternatives)<=set(sources):
        raise ValueError('Candidate hashes must belong to an already declared source.')
    declared = {}
    for relative, expected_hash in declarations.items():
        path = project/relative
        if path != Path(str(fixture)+'.meta') and path.parent != fixture:
            raise ValueError('Declared path is outside the exact fixture: '+relative)
        if path != Path(str(fixture)+'.meta') and path.suffix == '.meta' and str(path)[:-5] not in {str(project/p) for p in sources}:
            raise ValueError('Metadata lacks a declared source pair: '+relative)
        for current in (path, path.parent):
            if current.exists() and getattr(current.lstat(), 'st_file_attributes', 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
                raise ValueError('Cleanup refuses a reparse path: '+str(current))
        declared[path] = {expected_hash,*alternatives.get(relative,())} if relative in sources else {expected_hash}
    if fixture.exists():
        actual = set(fixture.iterdir())
        if any(path.is_dir() for path in actual) or not actual <= set(declared):
            raise ValueError('Unknown fixture child; the whole fixture remains for inspection.')
    outer = Path(str(fixture)+'.meta')
    if outer.exists() and outer not in declared:
        raise ValueError('Unknown outer metadata; the whole fixture remains for inspection.')
    for path, expected in declared.items():
        if path.exists() and (not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() not in expected):
            raise ValueError('Changed fixture source/metadata; the whole fixture remains: '+str(path))
    removed = []
    # All declarations, pairs, child sets and hashes passed before any mutation.
    for path in declared:
        if path.exists() and path != outer:
            path.unlink()
            removed.append(str(path))
    if fixture.exists():
        fixture.rmdir()
    if outer.exists():
        outer.unlink()
        removed.append(str(outer))
    return removed
