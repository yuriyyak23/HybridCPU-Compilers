#!/usr/bin/env python3
"""Format-only synthetic mutations: not Golden/native/Core proof or execution."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'Migration' / 'parity_guard.py'
spec = importlib.util.spec_from_file_location('parity_guard', SCRIPT)
p = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p)


def raw(data):
    return json.dumps(data, separators=(',', ':'), sort_keys=True).encode('utf8')


class ParityGuardRegression(unittest.TestCase):
    def setUp(self):
        self.source = b'public static int Add(int x, int y) => unchecked(x+y);\n'
        self.owner = {'commit': 'a'*40, 'sha256': hashlib.sha256(self.source).hexdigest(),
                      'path': 'Model/Scalar.cs', 'method': 'Add'}
        origin = {'commit': self.owner['commit'], 'path': self.owner['path'],
                  'member': self.owner['method'], 'role': 'Primary'}
        def port(n, d):
            return {'name': n, 'direction': d, 'type': {'width': 32, 'signedness': 'Signed',
                    'fourState': False}, 'kind': 'Data', 'clockDomain': None}
        ports = [port('a', 'Input'), port('b', 'Input'), port('out', 'Output')]
        self.hwir = {'domain': p.CANONICAL, 'schemaVersion': 'hwir/v0.1', 'topModule': 'scalar',
                     'modules': [{'id': 'scalar', 'origin': origin, 'ports': ports, 'clockDomains': [],
                     'contract': {'kind': 'Combinational', 'ports': copy.deepcopy(ports),
                        'timing': {'latencyCycles': 0, 'initiationInterval': 1},
                        'overflow': 'Wrap', 'reset': None, 'memory': None, 'faults': 'None'}}]}
        vec = {'id': 'zero', 'inputs': {'a': '0', 'b': '0'}, 'outputs': {'out': '0'}}
        self.obs = {'schema': p.SCHEMA, 'producer': 'legacy-bridge', 'source': self.owner,
                    'hwirSha256': p.sha(raw(self.hwir)), 'vectors': [vec]}

    def compare(self, hwir=None, core=None, lobs=None, cobs=None, owner=None, source=None, native=None, slice_name='scalar'):
        hwir = self.hwir if hwir is None else hwir
        core = self.hwir if core is None else core
        lobs = copy.deepcopy(self.obs) if lobs is None else lobs
        cobs = copy.deepcopy(self.obs) if cobs is None else cobs
        cobs['producer'] = 'core-generic'
        cobs['hwirSha256'] = p.sha(raw(core))
        return p.compare(self.source if source is None else source, raw(hwir), raw(core),
                         raw(lobs), raw(cobs), self.owner if owner is None else owner,
                         slice_name, raw(native) if native is not None else None)

    def test_matching_format_is_report_only_not_accepted(self):
        r = self.compare()
        self.assertEqual(('ReportOnly', 'Blocked', 'NotRun'),
                         (r['status'], r['independentRtlAcceptance'], r['native']))

    def test_sign_and_width_mutation(self):
        for field, changed in [('width', 64), ('signedness', 'Unsigned')]:
            with self.subTest(field=field):
                core = copy.deepcopy(self.hwir)
                core['modules'][0]['ports'][0]['type'][field] = changed
                core['modules'][0]['contract']['ports'][0]['type'][field] = changed
                with self.assertRaisesRegex(p.Refused, 'MISMATCH'):
                    self.compare(core=core)

    def test_overflow_clock_memory_reset_mutations(self):
        for key, value in [('overflow', 'Saturate'), ('memory', {'ports': 1}),
                           ('reset', 'synchronous')]:
            with self.subTest(key=key):
                core = copy.deepcopy(self.hwir)
                core['modules'][0]['contract'][key] = value
                with self.assertRaisesRegex(p.Refused, 'MISMATCH'):
                    self.compare(core=core)

    def test_stale_source_and_commit(self):
        with self.assertRaisesRegex(p.Refused, 'SOURCE_BYTES_MISMATCH'):
            self.compare(source=self.source + b' changed')
        core = copy.deepcopy(self.hwir)
        core['modules'][0]['origin']['commit'] = 'b'*40
        with self.assertRaisesRegex(p.Refused, 'UNOWNED_SOURCE_METHOD'):
            self.compare(core=core)

    def test_unowned_method_and_partial_cpu(self):
        core = copy.deepcopy(self.hwir)
        core['modules'][0]['origin']['member'] = 'Unknown'
        with self.assertRaisesRegex(p.Refused, 'UNOWNED_SOURCE_METHOD'):
            self.compare(core=core)
        with self.assertRaisesRegex(p.Refused, 'UNDECLARED_CLOCK'):
            clocked = copy.deepcopy(self.hwir)
            clocked['modules'][0]['contract']['kind'] = 'Clocked'
            self.compare(core=clocked)
        with self.assertRaisesRegex(p.Refused, 'UNKNOWN_SLICE'):
            self.compare(slice_name='processor')

    def test_rank4_lane6_blocked(self):
        for name, code in [('GetCreditRankedOrder', 'BLOCKED_RANK4'), ('HardPinnedLane6', 'BLOCKED_HARDPINNED_LANE6')]:
            with self.subTest(name=name):
                owner = dict(self.owner, method=name)
                with self.assertRaisesRegex(p.Refused, code):
                    self.compare(owner=owner)

    def test_mismatched_vector_and_unsigned_range(self):
        core = copy.deepcopy(self.obs)
        core['vectors'][0]['outputs']['out'] = '1'
        with self.assertRaisesRegex(p.Refused, 'VECTOR_MISMATCH'):
            self.compare(cobs=core)
        lobs = copy.deepcopy(self.obs)
        lobs['vectors'][0]['outputs']['out'] = '4294967296'
        with self.assertRaisesRegex(p.Refused, 'VECTOR_WIDTH_OR_SIGN'):
            self.compare(lobs=lobs)

    def test_stale_native_receipt_and_wrong_pins(self):
        native = {'status': 'candidate-rtl-simulation-passed', 'trust': 'StructuralOnly',
                  'model': {'hwirArtifactSha256': '0'*64},
                  'commands': {'rtlExportExit': 0, 'compileExit': 0, 'simulationExit': 0}}
        with self.assertRaisesRegex(p.Refused, 'STALE_NATIVE_RECEIPT'):
            self.compare(native=native)
        native['model']['hwirArtifactSha256'] = p.sha(raw(self.hwir))
        with self.assertRaisesRegex(p.Refused, 'UNPINNED_NATIVE_TOOLS'):
            self.compare(native=native)

    def test_symlink_and_timeout_and_duplicate_json(self):
        with tempfile.TemporaryDirectory() as d:
            original = Path(d) / 'original.txt'; original.write_bytes(self.source)
            linked = Path(d) / 'linked.txt'; linked.symlink_to(original)
            with self.assertRaisesRegex(p.Refused, 'SYMLINK_INPUT'):
                p.read_pinned(linked, 'source', float('inf'))
            with self.assertRaisesRegex(p.Refused, 'TIMEOUT'):
                p.read_pinned(original, 'source', 0)
        with self.assertRaisesRegex(p.Refused, 'DUPLICATE_JSON_PROPERTY'):
            p.parse_json(b'{"schema":"a","schema":"b"}')


if __name__ == '__main__':
    unittest.main(verbosity=2)
