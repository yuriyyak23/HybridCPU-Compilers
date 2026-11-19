#!/usr/bin/env python3
"""HB-INT-05 opt-in, report-only parity of independently supplied Core/legacy artifacts.

This script does NOT run lowering, trust a producer label, execute native tools, or
accept RTL. It refuses mismatched captured bytes, contracts and vector outputs.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import sys
import time

SCHEMA = 'hybridcpu.hardwarebridge.hb-int-05.observation/v1'
CANONICAL = 'CircuitWeave/HWIR/canonical/v1'
HEX40 = re.compile(r'[0-9a-f]{40}\Z')
HEX64 = re.compile(r'[0-9a-f]{64}\Z')
MAX_BYTES = {'source': 8 * 1024 * 1024, 'hwir': 16 * 1024 * 1024, 'observation': 4 * 1024 * 1024, 'native': 1 * 1024 * 1024}


class Refused(ValueError):
    pass


def ensure(condition, explanation):
    if not condition:
        raise Refused(explanation)


def read_pinned(path, kind, deadline):
    ensure(time.monotonic() < deadline, 'TIMEOUT')
    p = Path(path).absolute()
    # Paths beneath symbolic-link directories may point back into Golden/model trees.
    ensure(not any(parent.is_symlink() for parent in (p, *p.parents)), 'SYMLINK_INPUT')
    ensure(p.is_file(), 'MISSING_INPUT')
    before = p.stat()
    ensure(before.st_size <= MAX_BYTES[kind], 'INPUT_BUDGET')
    with p.open('rb') as stream:
        raw = stream.read(MAX_BYTES[kind] + 1)
    after = p.stat()
    ensure(len(raw) <= MAX_BYTES[kind], 'INPUT_BUDGET')
    ensure((before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns) ==
           (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns), 'INPUT_CHANGED')
    ensure(time.monotonic() < deadline, 'TIMEOUT')
    return raw


def parse_json(raw):
    def unique(pairs):
        output = {}
        for key, value in pairs:
            ensure(key not in output, 'DUPLICATE_JSON_PROPERTY')
            output[key] = value
        return output
    try:
        return json.loads(raw.decode('utf-8-sig'), object_pairs_hook=unique,
                          parse_constant=lambda _: (_ for _ in ()).throw(Refused('NONFINITE_JSON')))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise Refused('INVALID_JSON') from exc


def sha(raw):
    return hashlib.sha256(raw).hexdigest()


def top(design, source, slice_name):
    ensure(isinstance(design, dict) and design.get('domain') == CANONICAL and
           design.get('schemaVersion') == 'hwir/v0.1', 'UNKNOWN_HWIR_SCHEMA')
    modules = design.get('modules')
    ensure(isinstance(modules, list) and 1 <= len(modules) <= 256, 'INVALID_MODULES')
    ensure(len({m.get('id') for m in modules if isinstance(m, dict)}) == len(modules), 'DUPLICATE_MODULE')
    matches = [m for m in modules if isinstance(m, dict) and m.get('id') == design.get('topModule')]
    ensure(len(matches) == 1, 'MISSING_TOP_MODULE')
    m = matches[0]
    contract = m.get('contract')
    ensure(isinstance(contract, dict) and contract.get('kind') in ('Combinational', 'Clocked'), 'UNKNOWN_CONTRACT')
    ensure(slice_name == 'scheduler' or contract['kind'] == 'Combinational', 'UNDECLARED_CLOCK')
    ensure(isinstance(m.get('ports'), list) and m['ports'], 'MISSING_PORTS')
    ensure(m['ports'] == contract.get('ports'), 'PORT_CONTRACT_DIVERGENCE')
    # Producer-supplied origin is diagnostic, not a cryptographic signature. Bind
    # it to the *independently supplied* source arguments before any comparison.
    origin = m.get('origin')
    ensure(isinstance(origin, dict) and origin.get('commit') == source['commit'] and
           origin.get('path') == source['path'] and origin.get('member') == source['method'] and
           origin.get('role') == 'Primary', 'UNOWNED_SOURCE_METHOD')
    ensure(isinstance(m.get('clockDomains'), list), 'MISSING_CLOCK_DOMAINS')
    return m


def typed_contract(module):
    contract = module['contract']
    timing = contract.get('timing')
    ensure(isinstance(timing, dict) and isinstance(timing.get('latencyCycles'), int) and
           isinstance(timing.get('initiationInterval'), int), 'MISSING_TIMING')
    ports = {}
    for p in module['ports']:
        ensure(isinstance(p, dict) and isinstance(p.get('name'), str), 'INVALID_PORT')
        name = p['name']
        ensure(name not in ports, 'DUPLICATE_PORT')
        t = p.get('type')
        ensure(p.get('direction') in ('Input', 'Output') and isinstance(t, dict) and
               type(t.get('width')) is int and 1 <= t['width'] <= 4096 and
               t.get('signedness') in ('Signed', 'Unsigned') and
               type(t.get('fourState')) is bool, 'INVALID_PORT_TYPE')
        ports[name] = p
    return {'ports': ports, 'kind': contract['kind'], 'timing': timing,
            'reset': contract.get('reset'), 'memory': contract.get('memory'),
            'overflow': contract.get('overflow'), 'faults': contract.get('faults'),
            'clockDomains': module['clockDomains']}


def check_vectors(observation, expected_producer, source, artifact_sha, ports):
    ensure(isinstance(observation, dict) and observation.get('schema') == SCHEMA and
           observation.get('producer') == expected_producer and
           observation.get('source') == source and
           observation.get('hwirSha256') == artifact_sha, 'STALE_OBSERVATION')
    vectors = observation.get('vectors')
    ensure(isinstance(vectors, list) and 1 <= len(vectors) <= 10000, 'MISSING_VECTORS')
    by_id = {}
    for v in vectors:
        ensure(isinstance(v, dict) and set(v) == {'id', 'inputs', 'outputs'} and
               isinstance(v['id'], str) and 0 < len(v['id']) <= 100 and v['id'] not in by_id,
               'INVALID_VECTOR_ID')
        for key, direction in (('inputs', 'Input'), ('outputs', 'Output')):
            bits = v[key]
            expected = {name for name, p in ports.items() if p['direction'] == direction and p.get('kind') != 'Clock'}
            ensure(isinstance(bits, dict) and set(bits) == expected, 'VECTOR_PORT_SET')
            for name, word in bits.items():
                ensure(isinstance(word, str) and re.fullmatch(r'0|[1-9][0-9]*', word) and
                       len(word) <= 1235 and int(word) < (1 << ports[name]['type']['width']),
                       'VECTOR_WIDTH_OR_SIGN')
        by_id[v['id']] = v
    return by_id


def inspect_native(raw, legacy_hwir_sha):
    # Historical native receipts are not automatically accepted: a fresh receipt
    # must name the exact artifact SHA and all tool pins. Even then ReportOnly.
    receipt = parse_json(raw)
    ensure(receipt.get('status') == 'candidate-rtl-simulation-passed' and
           receipt.get('trust') == 'StructuralOnly', 'NATIVE_SCOPE_UNSUPPORTED')
    model = receipt.get('model') or receipt.get('source') or {}
    ensure(model.get('hwirArtifactSha256') == legacy_hwir_sha, 'STALE_NATIVE_RECEIPT')
    cmds = receipt.get('commands') or {}
    ensure(all(cmds.get(x) == 0 for x in ('rtlExportExit', 'compileExit', 'simulationExit')), 'NATIVE_FAILED')
    tools = receipt.get('tools') or {}
    ensure(all(HEX64.fullmatch(tools.get(x, '')) for x in
               ('circtExecutableSha256', 'iverilogExecutableSha256', 'vvpExecutableSha256')),
           'UNPINNED_NATIVE_TOOLS')
    return 'historical-candidate-receipt-linked; no native execution by this gate'


def compare(source_bytes, legacy_hwir_bytes, core_hwir_bytes, legacy_observation_bytes,
            core_observation_bytes, expected, slice_name, native_receipt_bytes=None):
    ensure(slice_name in ('scalar', 'scheduler', 'csr'), 'UNKNOWN_SLICE')
    ensure(HEX40.fullmatch(expected['commit']) and HEX64.fullmatch(expected['sha256']) and
           expected['path'] and expected['method'], 'INVALID_SOURCE_IDENTITY')
    ensure(sha(source_bytes) == expected['sha256'], 'SOURCE_BYTES_MISMATCH')
    ensure('rank' not in expected['method'].lower(), 'BLOCKED_RANK4')
    ensure('lane' not in expected['method'].lower(), 'BLOCKED_HARDPINNED_LANE6')
    legacy = top(parse_json(legacy_hwir_bytes), expected, slice_name)
    core = top(parse_json(core_hwir_bytes), expected, slice_name)
    left = typed_contract(legacy)
    right = typed_contract(core)
    ensure(left == right, 'HWIR_WIDTH_SIGN_OVERFLOW_CLOCK_MEMORY_MISMATCH')
    lvec = check_vectors(parse_json(legacy_observation_bytes), 'legacy-bridge', expected,
                         sha(legacy_hwir_bytes), left['ports'])
    rvec = check_vectors(parse_json(core_observation_bytes), 'core-generic', expected,
                         sha(core_hwir_bytes), right['ports'])
    ensure(lvec == rvec, 'VECTOR_MISMATCH')
    # Neither row counts nor a matching checksum proves independent execution.
    native_status = 'NotRun'
    if native_receipt_bytes is not None:
        native_status = inspect_native(native_receipt_bytes, sha(legacy_hwir_bytes))
    return {'schema': 'hybridcpu.hardwarebridge.hb-int-05.parity-report/v1',
            'status': 'ReportOnly', 'comparison': 'matching', 'slice': slice_name,
            'vectorsCompared': len(lvec), 'sourceSha256': sha(source_bytes),
            'legacyHwirSha256': sha(legacy_hwir_bytes), 'coreHwirSha256': sha(core_hwir_bytes),
            'native': native_status, 'independentRtlAcceptance': 'Blocked',
            'coreGenericLoweringProvenance': 'Unqualified', 'processor': 'INCOMPLETE'}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('source', 'legacy-hwir', 'core-hwir', 'legacy-observations', 'core-observations',
                 'source-commit', 'source-sha256', 'source-path', 'source-method'):
        parser.add_argument('--' + name, required=True)
    parser.add_argument('--slice', choices=['scalar', 'scheduler', 'csr'], required=True)
    parser.add_argument('--native-receipt')
    parser.add_argument('--timeout-seconds', type=float, default=15.0)
    a = parser.parse_args(argv)
    try:
        ensure(0 < a.timeout_seconds <= 300, 'INVALID_TIMEOUT')
        deadline = time.monotonic() + a.timeout_seconds
        expected = {'commit': a.source_commit, 'sha256': a.source_sha256,
                    'path': a.source_path, 'method': a.source_method}
        result = compare(read_pinned(a.source, 'source', deadline),
                         read_pinned(a.legacy_hwir, 'hwir', deadline),
                         read_pinned(a.core_hwir, 'hwir', deadline),
                         read_pinned(a.legacy_observations, 'observation', deadline),
                         read_pinned(a.core_observations, 'observation', deadline),
                         expected, a.slice, read_pinned(a.native_receipt, 'native', deadline)
                         if a.native_receipt else None)
        print(json.dumps(result, sort_keys=True, separators=(',', ':')))
        return 0
    except (Refused, KeyError, TypeError, OSError, RecursionError) as exc:
        print(json.dumps({'status': 'Blocked', 'reason': str(exc),
                          'independentRtlAcceptance': 'Blocked'}, sort_keys=True), file=sys.stderr)
        return 2


if __name__ == '__main__':
    sys.exit(main())
