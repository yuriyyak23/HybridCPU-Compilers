"""R2-02 fixture-only Core-reader mutations (never imported as a Core consumer).

Mutates isolated copies of a successful Bridge-produced Core-v2 workspace.
The caller must invoke the actual pinned Core BridgeSegmentIndexImporterV2
and check its *specific* diagnostic code. Recomputed test pins are forged on
purpose and have no provenance/qualification standing.
"""
import hashlib
import json
from pathlib import Path

MUTATIONS = {
    'input-bytes': 'CWINT03_STALE_BYTES',
    'chunk-bytes': 'CWINT03_V2_CHUNK_SHA',
    'omitted-chunk': 'CWINT03_V2_IO',
    'wrong-index-pin': 'CWINT03_V2_INDEX_SHA',
    'duplicate-root': 'CWINT03_V2_ROOT',
    'duplicate-json-key': 'CWINT03_V2_SCHEMA',
    'wrong-role': 'CWINT03_V2_ROLE',
}


def digest(data):
    return hashlib.sha256(data).hexdigest().upper()


def canonical(obj):
    # Bridge's canonical index and JSONL transport only uses ASCII-safe fields.
    return json.dumps(obj, separators=(',', ':'), ensure_ascii=True).encode('ascii')


def _unique(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError('duplicate JSON key in original producer workspace')
        result[key] = value
    return result


def _flip(data):
    if not data:
        raise ValueError('empty target cannot be mutated')
    return bytes([data[0] ^ 1]) + data[1:]


def prepare_fixture_mutation(workspace, kind):
    """Mutate a private copy; return (index SHA pin, closure pin, expected code).

    A successful positive Core import MUST precede these negative checks. No
    other caller may use the returned synthetic, recomputed review pins.
    """
    if kind not in MUTATIONS:
        raise ValueError('unknown R2-02 fixture mutation')
    workspace = Path(workspace)
    index_path = workspace / 'bridge-index.json'
    original = index_path.read_bytes()
    index = json.loads(original, object_pairs_hook=_unique)
    if (index.get('Schema') != 'circuitweave.bridge-source-closure-index/v2' or
            not isinstance(index.get('Chunks'), list) or not index['Chunks'] or
            not isinstance(index.get('Roots'), list) or not index['Roots']):
        raise ValueError('not a successful staged Core-v2 fixture')
    chunk_paths = [workspace / item['Path'] for item in index['Chunks']]
    closure = index['ClosureSha256']

    if kind == 'input-bytes':
        inputs = sorted((workspace / 'inputs').glob('*.cs'))
        if not inputs:
            raise ValueError('no staged source input to corrupt')
        target = next((p for p in inputs if p.stat().st_size), None)
        if target is None:
            raise ValueError('no nonempty staged source input')
        target.write_bytes(_flip(target.read_bytes()))
    elif kind == 'chunk-bytes':
        chunk_paths[0].write_bytes(_flip(chunk_paths[0].read_bytes()))
    elif kind == 'omitted-chunk':
        chunk_paths[0].unlink()
    elif kind == 'wrong-index-pin':
        # Keep staged bytes intact: only the caller-owned expected index SHA is wrong.
        return ('0' * 64 if digest(original) != '0' * 64 else 'F' * 64,
                closure, MUTATIONS[kind])
    elif kind == 'duplicate-root':
        index['Roots'].append(dict(index['Roots'][0]))
        index_path.write_bytes(canonical(index))
    elif kind == 'duplicate-json-key':
        # Valid JSON syntax; the duplicate shape itself must be rejected by Core.
        marker = b',"Roots":'
        if original.count(marker) != 1:
            raise ValueError('unexpected Core index layout')
        index_path.write_bytes(original.replace(marker, b',"Schema":"duplicate",' + b'"Roots":', 1))
    elif kind == 'wrong-role':
        # Re-pin the deliberately modified chunk/index and closure to reach the
        # Core role validator, rather than only triggering the prior SHA gate.
        chunk = chunk_paths[0]
        lines = chunk.read_bytes().splitlines(keepends=True)
        if not lines or not lines[0].endswith(b'\n'):
            raise ValueError('missing canonical JSONL row')
        row = json.loads(lines[0][:-1], object_pairs_hook=_unique)
        row['Role'] = 'unauthorized-body'
        lines[0] = canonical(row) + b'\n'
        chunk.write_bytes(b''.join(lines))
        index['Chunks'][0]['Sha256'] = digest(chunk.read_bytes())
        index['Chunks'][0]['Bytes'] = chunk.stat().st_size
        closure = digest(b''.join(p.read_bytes() for p in chunk_paths))
        index['ClosureSha256'] = closure
        index_path.write_bytes(canonical(index))
    return digest(index_path.read_bytes()), closure, MUTATIONS[kind]
