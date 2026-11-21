#!/usr/bin/env python3
"""R3-03 bounded, unqualified, dual-artifact + Golden observation review gate.

This is a *report-only* comparison of external evidence envelopes; it never
establishes that the envelope's claimed producer really executed. Independent
source qualification, formal/native proof and RTL acceptance belong to Core.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import stat
import sys
import time

_parity_spec = importlib.util.spec_from_file_location(
    'R3_03_legacy_report_only_parity', Path(__file__).with_name('parity_guard.py'))
parity = importlib.util.module_from_spec(_parity_spec)
_parity_spec.loader.exec_module(parity)

REVIEW_SCHEMA = 'hybridcpu.hardwarebridge.r3-03.dual-artifact-review/v1'
GOLDEN_SCHEMA = 'hybridcpu.hardwarebridge.r3-03.golden-vector-review/v1'
REPORT_SCHEMA = 'hybridcpu.hardwarebridge.r3-03.dual-artifact-report/v1'
HEX40 = re.compile(r'[0-9a-f]{40}\Z')
HEX64 = re.compile(r'[0-9a-f]{64}\Z')
METHOD_TOKEN = re.compile(r'0x06[0-9a-fA-F]{6}\Z')
MAX_BYTES = {'review': 256 * 1024, 'golden': 4 * 1024 * 1024,
             'source': 8 * 1024 * 1024, 'hwir': 16 * 1024 * 1024,
             'observation': 4 * 1024 * 1024, 'native': 1024 * 1024}


class Blocked(ValueError):
    pass


def require(test, code):
    if not test:
        raise Blocked(code)


def sha256(raw):
    return hashlib.sha256(raw).hexdigest()


def git_blob(raw):
    return hashlib.sha1(b'blob ' + str(len(raw)).encode() + b'\0' + raw).hexdigest()


def canonical(document):
    return (json.dumps(document, sort_keys=True, separators=(',', ':'),
                       ensure_ascii=False, allow_nan=False) + '\n').encode('utf-8')


def check_keys(obj, keys, code):
    require(type(obj) is dict and set(obj) == set(keys), code)
    return obj


def hex_pin(value, code, bits=256):
    require(type(value) is str and (HEX40 if bits == 160 else HEX64).fullmatch(value), code)


def bounded_read(path, category, deadline):
    """Immutable read attempt, bounded bytes/time, with file handle no-follow.

    This guards ordinary symlink and replacement races, not hostile privileged
    mutation of the containing filesystem. Consumers must independently pin.
    """
    require(time.monotonic() < deadline, 'R303_TIMEOUT')
    p = Path(path).absolute()
    require(not any(part.is_symlink() for part in (p, *p.parents)), 'R303_SYMLINK_PATH')
    flags = os.O_RDONLY | getattr(os, 'O_BINARY', 0) | getattr(os, 'O_NOFOLLOW', 0)
    try:
        fd = os.open(p, flags)
    except OSError as exc:
        raise Blocked('R303_MISSING_OR_UNSAFE_INPUT') from exc
    try:
        before = os.fstat(fd)
        require(stat.S_ISREG(before.st_mode) and 0 < before.st_size <= MAX_BYTES[category],
                'R303_INPUT_SIZE_OR_TYPE')
        with os.fdopen(fd, 'rb', closefd=False) as stream:
            raw = stream.read(MAX_BYTES[category] + 1)
        after = os.fstat(fd)
        require(len(raw) == before.st_size and len(raw) <= MAX_BYTES[category],
                'R303_INPUT_SIZE_RACE')
        require((before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns,
                 before.st_ctime_ns) ==
                (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns,
                 after.st_ctime_ns), 'R303_INPUT_CHANGED')
        require(time.monotonic() < deadline, 'R303_TIMEOUT')
        return raw
    finally:
        os.close(fd)


def strict_canonical(raw, schema):
    try:
        obj = parity.parse_json(raw)
    except parity.Refused as exc:
        raise Blocked('R303_' + str(exc)) from exc
    require(type(obj) is dict and obj.get('schema') == schema, 'R303_UNKNOWN_SCHEMA')
    require(raw == canonical(obj), 'R303_NONCANONICAL_JSON')
    return obj


def review_contract(review):
    check_keys(review, ('schema', 'slice', 'source', 'legacy', 'core', 'golden'),
               'R303_REVIEW_UNKNOWN_FIELD')
    require(review['slice'] == 'scalar', 'R303_CW_INT_13_TEMPORAL_BLOCKED')
    source = check_keys(review['source'],
        ('commit', 'path', 'method', 'sha256', 'gitBlobSha1', 'methodToken',
         'signature', 'ownedClosureSha256'), 'R303_SOURCE_CONTRACT')
    for key, bits in (('commit', 160), ('gitBlobSha1', 160), ('sha256', 256),
                      ('ownedClosureSha256', 256)):
        hex_pin(source[key], 'R303_SOURCE_PIN_' + key, bits)
    require(type(source['path']) is str and 0 < len(source['path']) <= 512 and
            source['path'].endswith('.cs') and not source['path'].startswith('/') and
            all(t not in ('', '.', '..') for t in source['path'].split('/')) and
            not any(c in source['path'] for c in ('\\', ':', '\x00')),
            'R303_SOURCE_PATH')
    require(type(source['method']) is str and 3 <= len(source['method']) <= 512 and
            'AddWord' in source['method'] or False, 'R303_SOURCE_METHOD_UNREVIEWED')
    require(type(source['signature']) is str and 3 <= len(source['signature']) <= 1024 and
            source['method'].split('.')[-1] in source['signature'], 'R303_METHOD_SIGNATURE')
    require(type(source['methodToken']) is str and METHOD_TOKEN.fullmatch(source['methodToken']),
            'R303_METHOD_TOKEN')
    legacy = check_keys(review['legacy'], ('hwirSha256', 'observationSha256'),
                        'R303_LEGACY_CONTRACT')
    core = check_keys(review['core'],
        ('hwirSha256', 'observationSha256', 'sourceSha256', 'ownedClosureSha256',
         'compilerCommit', 'stageReceiptSha256', 'executableSha256', 'rule'),
        'R303_CORE_CONTRACT')
    golden = check_keys(review['golden'],
        ('observationSha256', 'sourceSha256', 'invocationSha256', 'executableSha256',
         'peSha256', 'pdbSha256', 'companionSha256'), 'R303_GOLDEN_CONTRACT')
    for part in (legacy, core, golden):
        for key, value in part.items():
            if key not in ('rule',):
                hex_pin(value, 'R303_INVALID_PIN_' + key, 160 if key == 'compilerCommit' else 256)
    require(core['sourceSha256'] == source['sha256'] and
            golden['sourceSha256'] == source['sha256'] and
            core['ownedClosureSha256'] == source['ownedClosureSha256'],
            'R303_CLOSURE_SOURCE_MISMATCH')
    require(core['rule'] == 'CW-INT-06/source-owned-generic', 'R303_NOT_GENERIC_CORE_RULE')
    require(core['hwirSha256'] != legacy['hwirSha256'], 'R303_LEGACY_AS_CORE_ALIAS')
    return source


def validate_golden(raw, review, expected_source, expected_vectors, ports):
    golden = strict_canonical(raw, GOLDEN_SCHEMA)
    check_keys(golden, ('schema', 'producer', 'source', 'methodToken', 'signature',
                        'ownedClosureSha256', 'execution', 'vectors'),
               'R303_GOLDEN_UNKNOWN_FIELD')
    require(golden['producer'] == 'GoldenObserved', 'R303_GOLDEN_ORIGIN_ROLE')
    require(golden['source'] == expected_source, 'R303_GOLDEN_SOURCE_MISMATCH')
    for key in ('methodToken', 'signature', 'ownedClosureSha256'):
        require(golden[key] == review['source'][key], 'R303_GOLDEN_METHOD_' + key)
    exec_pins = check_keys(golden['execution'],
        ('invocationSha256', 'executableSha256', 'peSha256', 'pdbSha256', 'companionSha256'),
        'R303_GOLDEN_EXECUTION_CONTRACT')
    for key, value in exec_pins.items():
        require(value == review['golden'][key], 'R303_GOLDEN_BINARY_' + key)
    wrapper = {'schema': parity.SCHEMA, 'producer': 'golden-observed',
               'source': expected_source, 'hwirSha256': review['legacy']['hwirSha256'],
               'vectors': golden['vectors']}
    try:
        checked = parity.check_vectors(wrapper, 'golden-observed', expected_source,
                                       review['legacy']['hwirSha256'], ports)
    except parity.Refused as exc:
        raise Blocked('R303_' + str(exc)) from exc
    require(checked == expected_vectors, 'R303_INDEPENDENT_GOLDEN_VECTOR_MISMATCH')
    return checked


def compare_bytes(review_raw, source_raw, legacy_hwir_raw, core_hwir_raw,
                  legacy_obs_raw, core_obs_raw, golden_obs_raw,
                  native_receipt_raw=None):
    review = strict_canonical(review_raw, REVIEW_SCHEMA)
    source = review_contract(review)
    require(sha256(source_raw) == source['sha256'], 'R303_SOURCE_SHA_MISMATCH')
    require(git_blob(source_raw) == source['gitBlobSha1'], 'R303_SOURCE_BLOB_MISMATCH')
    raw_map = ((legacy_hwir_raw, review['legacy']['hwirSha256'], 'LEGACY_HWIR'),
               (core_hwir_raw, review['core']['hwirSha256'], 'CORE_HWIR'),
               (legacy_obs_raw, review['legacy']['observationSha256'], 'LEGACY_OBS'),
               (core_obs_raw, review['core']['observationSha256'], 'CORE_OBS'),
               (golden_obs_raw, review['golden']['observationSha256'], 'GOLDEN_OBS'))
    for raw, pin, label in raw_map:
        require(sha256(raw) == pin, 'R303_STALE_' + label)
    require(legacy_hwir_raw != core_hwir_raw, 'R303_LEGACY_AS_CORE_ALIAS')
    expected = {key: source[key] for key in ('commit', 'sha256', 'path', 'method')}
    try:
        structural = parity.compare(source_raw, legacy_hwir_raw, core_hwir_raw,
                                    legacy_obs_raw, core_obs_raw, expected, 'scalar',
                                    native_receipt_raw)
        legacy_module = parity.top(parity.parse_json(legacy_hwir_raw), expected, 'scalar')
        contract = parity.typed_contract(legacy_module)
    except parity.Refused as exc:
        raise Blocked('R303_' + str(exc)) from exc
    require(contract['kind'] == 'Combinational' and contract['reset'] is None and
            contract['memory'] is None and not contract['clockDomains'] and
            contract['timing']['latencyCycles'] == 0,
            'R303_SCALAR_CLOCK_RESET_MEMORY_ROLE')
    legacy_obs = parity.parse_json(legacy_obs_raw)
    checked = validate_golden(golden_obs_raw, review, expected,
                              legacy_obs['vectors'], contract['ports'])
    return {'schema': REPORT_SCHEMA, 'status': 'ReportOnly',
            'structuralComparison': structural['comparison'],
            'vectorsCompared': len(checked),
            'sourceSha256': sha256(source_raw),
            'legacyHwirSha256': sha256(legacy_hwir_raw),
            'coreHwirSha256': sha256(core_hwir_raw),
            'independentGoldenProvenance': 'UnqualifiedExternalClaim',
            'coreSourceDerivedGenericParity': 'Blocked:CoreOwnerQualificationMissing',
            'semanticEquivalence': 'Blocked', 'processorTemporalParity': 'Blocked:CW-INT-13',
            'native': 'NotRun', 'independentRtlAcceptance': 'Blocked'}


def main(argv=None):
    p = argparse.ArgumentParser(description=__doc__)
    for arg in ('review', 'source', 'legacy-hwir', 'core-hwir', 'legacy-observations',
                'core-observations', 'golden-observations'):
        p.add_argument('--' + arg, required=True)
    p.add_argument('--native-receipt')
    p.add_argument('--timeout-seconds', type=float, default=15)
    a = p.parse_args(argv)
    try:
        require(0 < a.timeout_seconds <= 300, 'R303_INVALID_TIMEOUT')
        deadline = time.monotonic() + a.timeout_seconds
        inputs = [bounded_read(getattr(a, key), kind, deadline) for key, kind in (
            ('review', 'review'), ('source', 'source'), ('legacy_hwir', 'hwir'),
            ('core_hwir', 'hwir'), ('legacy_observations', 'observation'),
            ('core_observations', 'observation'), ('golden_observations', 'golden'))]
        native = bounded_read(a.native_receipt, 'native', deadline) if a.native_receipt else None
        report = compare_bytes(*inputs, native_receipt_raw=native)
        print(json.dumps(report, sort_keys=True, separators=(',', ':')))
        return 0
    except (Blocked, OSError, TypeError, ValueError, KeyError, RecursionError) as exc:
        print(json.dumps({'status': 'Blocked', 'reason': str(exc),
                          'independentRtlAcceptance': 'Blocked'}, sort_keys=True), file=sys.stderr)
        return 2


if __name__ == '__main__':
    sys.exit(main())
