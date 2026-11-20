#!/usr/bin/env python3
"""R3-03 Git-byte inventory gate. Read-only; source identity is NOT HWIR evidence."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys

SCHEMA = 'hybridcpu.hardwarebridge.r3-03.projection-review-inventory/v2'
HISTORICAL_SCHEMA = 'hybridcpu.hardwarebridge.hb-int-05.projection-inventory/v1'
HEX40 = re.compile(r'[0-9a-f]{40}\Z')
MAX_JSON_BYTES = 1024 * 1024
MAX_SOURCE_BYTES = 2 * 1024 * 1024


class Blocked(ValueError):
    pass


def require(test, reason):
    if not test:
        raise Blocked(reason)


def strict_json(path):
    p = Path(path)
    require(not any(part.is_symlink() for part in (p, *p.parents)) and
            p.is_file() and p.stat().st_size <= MAX_JSON_BYTES,
            'INDEX_MISSING_OR_OVERSIZE')
    def pairs(items):
        result = {}
        for k, v in items:
            require(k not in result, 'DUPLICATE_JSON_FIELD')
            result[k] = v
        return result
    try:
        return json.loads(p.read_bytes().decode('utf-8'), object_pairs_hook=pairs,
                          parse_constant=lambda _: (_ for _ in ()).throw(Blocked('NONFINITE_JSON')))
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise Blocked('INVALID_JSON') from exc


def git_blob(data):
    return hashlib.sha1(b'blob ' + str(len(data)).encode('ascii') + b'\x00' + data).hexdigest()


def captured_bytes(path):
    require(not any(part.is_symlink() for part in (path, *path.parents)) and path.is_file(),
            'SOURCE_MISSING_OR_SYMLINK')
    before = path.stat()
    require(0 < before.st_size <= MAX_SOURCE_BYTES, 'SOURCE_SIZE_BUDGET')
    with path.open('rb') as stream:
        data = stream.read(MAX_SOURCE_BYTES + 1)
    after = path.stat()
    require((before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns) ==
            (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns), 'SOURCE_TOUCHED')
    require(len(data) == before.st_size, 'SOURCE_SIZE_RACE')
    return data


def git_show(root, commit, relative):
    try:
        out = subprocess.run(['git', 'show', commit + ':' + relative],
                             cwd=root, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                             timeout=15, check=False)
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise Blocked('HISTORICAL_GIT_UNAVAILABLE') from exc
    require(out.returncode == 0, 'HISTORICAL_GIT_UNAVAILABLE')
    require(0 < len(out.stdout) <= MAX_SOURCE_BYTES, 'HISTORICAL_GIT_SIZE')
    return out.stdout


def audit(hwir, require_history=False):
    hwir = Path(hwir).absolute()
    require(not any(part.is_symlink() for part in (hwir, *hwir.parents)),
            'SOURCE_ROOT_SYMLINK')
    directory = hwir / 'Migration'
    old = strict_json(directory / 'projection-inventory.v1.json')
    new = strict_json(directory / 'r3_03_projection-review-inventory.v2.json')
    require(old.get('schema') == HISTORICAL_SCHEMA and old.get('status') == 'report-only-incomplete',
            'HISTORICAL_SCHEMA_DRIFT')
    require(new.get('schema') == SCHEMA and
            new.get('status') == 'Blocked:IndependentCoreSourceDerivedLoweringMissing',
            'REVIEW_SCHEMA_DRIFT')
    require(new.get('historicalHead') == old.get('sourceHead') and
            HEX40.fullmatch(str(new.get('auditedHead', ''))) is not None and
            HEX40.fullmatch(str(old.get('sourceHead', ''))) is not None,
            'BASELINE_HEAD_DRIFT')
    entries = new.get('entries')
    originals = old.get('entries')
    require(isinstance(entries, list) and isinstance(originals, list) and
            len(entries) == len(originals) == new.get('entriesCount') == old.get('entriesCount') == 60,
            'INVENTORY_COUNT')
    paths = sorted(p for p in hwir.glob('BridgeHwir*.cs') if p.is_file())
    require([p.name for p in paths] == [e.get('file') for e in entries] ==
            [e.get('file') for e in originals], 'INVENTORY_SET')
    require(len({e['file'] for e in entries}) == 60, 'DUPLICATE_ENTRY')
    try:
        repo = subprocess.run(['git', 'rev-parse', '--show-toplevel'], cwd=hwir,
                              stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                              text=True, timeout=10, check=False)
    except (OSError, subprocess.TimeoutExpired):
        repo = None
    history = repo is not None and repo.returncode == 0
    require(not require_history or history, 'HISTORICAL_GIT_UNAVAILABLE')
    root = Path(repo.stdout.strip()) if history else None
    changed = 0
    for path, row, old_row in zip(paths, entries, originals):
        require(row.get('file') == path.name and path.name.startswith('BridgeHwir') and
                re.fullmatch(r'BridgeHwir[A-Za-z0-9_]+\.cs', path.name) is not None,
                'SOURCE_PATH_DRIFT')
        b = row.get('historical')
        c = row.get('reviewedHead')
        require(isinstance(b, dict) and isinstance(c, dict), 'MISSING_SOURCE_PIN')
        require(b.get('commit') == old['sourceHead'] and
                b.get('gitBlobSha1') == old_row.get('bridgeCodeGitBlobSha1') and
                b.get('bytes') == old_row.get('bridgeCodeBytes') and
                c.get('commit') == new['auditedHead'] and
                HEX40.fullmatch(str(c.get('gitBlobSha1', ''))) is not None and
                type(c.get('bytes')) is int and c['bytes'] > 0, 'BASELINE_PIN_DRIFT')
        actual = captured_bytes(path)
        require(len(actual) == c['bytes'], 'CURRENT_SOURCE_SIZE_DRIFT')
        require(git_blob(actual) == c['gitBlobSha1'], 'CURRENT_SOURCE_BLOB_DRIFT')
        changed_here = b['gitBlobSha1'] != c['gitBlobSha1']
        changed += changed_here
        require(row.get('deltaDisposition') == (
            'source-delta-inspected-declaration-typing-only-not-semantic-proof'
            if changed_here else 'unchanged'), 'REVIEW_DISPOSITION_DRIFT')
        require(row.get('semanticEquivalence') == 'Blocked' and
                row.get('nativeReceipt') == 'NotRun' and
                row.get('processorAcceptance') == 'Blocked' and
                row.get('independentEvidence') == 'Blocked:CW-INT-06/CW-INT-13' and
                row.get('exactGoldenSourceIdentity') is None and
                isinstance(row.get('knownSemanticGap'), str) and row['knownSemanticGap'],
                'UNQUALIFIED_SOURCE_PROMOTION')
        if history:
            relative = path.relative_to(root).as_posix()
            require(git_blob(git_show(root, old['sourceHead'], relative)) ==
                    b['gitBlobSha1'], 'HISTORICAL_BLOB_DRIFT')
            require(len(git_show(root, old['sourceHead'], relative)) ==
                    b['bytes'], 'HISTORICAL_SIZE_DRIFT')
            require(git_blob(git_show(root, new['auditedHead'], relative)) ==
                    c['gitBlobSha1'], 'AUDITED_GIT_BLOB_DRIFT')
    require(changed == new.get('changedSourceFiles') == 20, 'REVIEW_CHANGE_COUNT')
    return {'status': 'Blocked', 'inventory': 'SourceBytesPinned',
            'files': len(entries), 'reviewedDeltas': changed,
            'historicalGitVerified': history, 'semanticEquivalence': 'Blocked',
            'native': 'NotRun', 'independentRtlAcceptance': 'Blocked',
            'dependency': 'CW-INT-06/CW-INT-13 source-derived generic lowering and P06 evidence'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--hwir-dir', type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument('--require-history', action='store_true')
    args = parser.parse_args()
    try:
        print(json.dumps(audit(args.hwir_dir, args.require_history), sort_keys=True))
        return 0  # audit passed, semantic gate REMAINS Blocked
    except (Blocked, OSError, ValueError, TypeError, KeyError) as exc:
        print(json.dumps({'status': 'Blocked', 'inventory': 'Refused',
                          'reason': str(exc), 'independentRtlAcceptance': 'Blocked'},
                         sort_keys=True), file=sys.stderr)
        return 2


if __name__ == '__main__':
    sys.exit(main())
