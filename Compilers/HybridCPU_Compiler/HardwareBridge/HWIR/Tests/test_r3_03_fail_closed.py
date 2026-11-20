#!/usr/bin/env python3
"""R3-03 negative mutations. Copies go only to isolated temporary directories."""
import copy
import importlib.util
import json
from pathlib import Path
import shutil
import tempfile
import unittest

HWIR = Path(__file__).resolve().parents[1]
MIGRATION = HWIR / 'Migration'


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


gate = load('r3_03_inventory_gate', MIGRATION / 'r3_03_inventory_gate.py')
parity = load('r3_03_parity_guard', MIGRATION / 'parity_guard.py')


class R303InventoryMutations(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name) / 'HWIR'
        (self.root / 'Migration').mkdir(parents=True)
        for name in ('projection-inventory.v1.json', 'r3_03_projection-review-inventory.v2.json'):
            shutil.copyfile(MIGRATION / name, self.root / 'Migration' / name)
        for file in HWIR.glob('BridgeHwir*.cs'):
            shutil.copyfile(file, self.root / file.name)

    def matrix(self):
        return self.root / 'Migration' / 'r3_03_projection-review-inventory.v2.json'

    def edit(self, fn):
        v = json.loads(self.matrix().read_text('utf-8'))
        fn(v)
        self.matrix().write_text(json.dumps(v, indent=2) + '\n', encoding='utf-8')

    def test_fixture_inventory_cannot_promote_semantics(self):
        result = gate.audit(self.root)
        self.assertEqual(('Blocked', 'NotRun', False),
                         (result['semanticEquivalence'], result['native'],
                          result['historicalGitVerified']))

    def test_same_length_source_mutation_is_refused(self):
        f = self.root / 'BridgeHwirAddWordCompiler.cs'
        data = bytearray(f.read_bytes())
        data[-1] ^= 1
        f.write_bytes(data)
        with self.assertRaisesRegex(gate.Blocked, 'SOURCE_BLOB_DRIFT'):
            gate.audit(self.root)

    def test_stale_byte_length_is_refused(self):
        f = self.root / 'BridgeHwirAddWordCompiler.cs'
        f.write_bytes(f.read_bytes() + b' ')
        with self.assertRaisesRegex(gate.Blocked, 'SOURCE_SIZE_DRIFT'):
            gate.audit(self.root)

    def test_wrong_reviewed_sha_and_baseline_sha_refused(self):
        for loc, field, reason in [('reviewedHead', 'gitBlobSha1', 'SOURCE_BLOB_DRIFT'),
                                   ('historical', 'gitBlobSha1', 'BASELINE_PIN_DRIFT')]:
            with self.subTest(loc=loc):
                original = self.matrix().read_bytes()
                try:
                    self.edit(lambda m: m['entries'][0][loc].__setitem__(field, '0' * 40))
                    with self.assertRaisesRegex(gate.Blocked, reason):
                        gate.audit(self.root)
                finally:
                    self.matrix().write_bytes(original)

    def test_wrong_path_missing_file_and_count_refused(self):
        initial = self.matrix().read_bytes()
        self.edit(lambda m: m['entries'][0].__setitem__('file', '../BridgeHwirFake.cs'))
        with self.assertRaisesRegex(gate.Blocked, 'INVENTORY_SET'):
            gate.audit(self.root)
        self.matrix().write_bytes(initial)
        (self.root / 'BridgeHwirAddWordCompiler.cs').unlink()
        with self.assertRaisesRegex(gate.Blocked, 'INVENTORY_SET'):
            gate.audit(self.root)

    def test_unqualified_ownership_native_and_rtl_promotion_refused(self):
        for field, promoted in [('exactGoldenSourceIdentity', {'verified': True}),
                                ('semanticEquivalence', 'Verified'),
                                ('nativeReceipt', 'candidate-rtl-simulation-passed'),
                                ('processorAcceptance', 'AcceptedRTL')]:
            with self.subTest(field=field):
                prior = self.matrix().read_bytes()
                try:
                    self.edit(lambda m: m['entries'][0].__setitem__(field, promoted))
                    with self.assertRaisesRegex(gate.Blocked, 'UNQUALIFIED_SOURCE_PROMOTION'):
                        gate.audit(self.root)
                finally:
                    self.matrix().write_bytes(prior)

    def test_duplicate_json_property_refused(self):
        p = self.matrix()
        t = p.read_text('utf-8')
        p.write_text(t.replace('"schema":', '"schema":"forged","schema":', 1), 'utf-8')
        with self.assertRaisesRegex(gate.Blocked, 'DUPLICATE_JSON_FIELD'):
            gate.audit(self.root)

    def test_symlink_source_refused(self):
        f = self.root / 'BridgeHwirAddWordCompiler.cs'
        t = self.root / 'private_source.cs'
        f.rename(t)
        try:
            f.symlink_to(t)
        except (NotImplementedError, OSError):
            self.skipTest('symlink creation unavailable')
        with self.assertRaisesRegex(gate.Blocked, 'SOURCE_MISSING_OR_SYMLINK'):
            gate.audit(self.root)


class R303ParityRefusals(unittest.TestCase):
    def test_scalar_missing_independent_core_fixture_is_explicitly_blocked(self):
        fixture = gate.strict_json(MIGRATION / 'fixtures' / 'R3_03_scalar_dependency.v1.json')
        self.assertEqual('scalar', fixture['slice'])
        self.assertTrue(fixture['status'].startswith('Blocked:'))
        self.assertIsNone(fixture['source']['currentSourceSha256'])
        self.assertIsNone(fixture['independentCore']['actualGenericHwirSha256'])
        self.assertIsNone(fixture['evidence']['orderedIndependentVectorCount'])
        self.assertEqual('NotRun', fixture['evidence']['native'])
        self.assertEqual('Blocked', fixture['evidence']['independentRtlAcceptance'])

    def test_stateful_scheduler_and_csr_are_unconditionally_blocked_without_core(self):
        for selected in ('scheduler', 'csr'):
            with self.subTest(slice=selected), self.assertRaisesRegex(parity.Refused, 'CW_INT_13'):
                parity.compare(b'', b'{}', b'{}', b'{}', b'{}', {}, selected)

    def test_processor_and_source_path_refusal(self):
        owner = {'commit': 'a'*40, 'sha256': parity.sha(b'test'), 'path': '../escape.cs', 'method': 'AddWord'}
        with self.assertRaisesRegex(parity.Refused, 'INVALID_SOURCE_PATH'):
            parity.compare(b'test', b'{}', b'{}', b'{}', b'{}', owner, 'scalar')
        owner['path'] = 'Golden/Source.cs'
        owner['method'] = 'Processor.Tick'
        with self.assertRaisesRegex(parity.Refused, 'BLOCKED_INFERRED_PROCESSOR'):
            parity.compare(b'test', b'{}', b'{}', b'{}', b'{}', owner, 'scalar')

    def test_ordered_vector_comparison_rejects_permutation(self):
        def port(name, direction):
            return {'name': name, 'direction': direction, 'kind': 'Data',
                    'type': {'width': 8, 'signedness': 'Unsigned', 'fourState': False}}
        ports = {'a': port('a', 'Input'), 'out': port('out', 'Output')}
        obs = {'schema': parity.SCHEMA, 'producer': 'legacy-bridge',
               'source': {'commit': 'a'*40, 'sha256': 'b'*64, 'path': 'x', 'method': 'y'},
               'hwirSha256': 'c'*64,
               'vectors': [{'id': 'first', 'inputs': {'a': '0'}, 'outputs': {'out': '1'}},
                           {'id': 'second', 'inputs': {'a': '1'}, 'outputs': {'out': '2'}}]}
        first = parity.check_vectors(obs, 'legacy-bridge', obs['source'], 'c'*64, ports)
        obs['vectors'].reverse()
        second = parity.check_vectors(obs, 'legacy-bridge', obs['source'], 'c'*64, ports)
        self.assertNotEqual(first, second)
        obs['vectors'][0]['outputs']['out'] = '256'
        with self.assertRaisesRegex(parity.Refused, 'VECTOR_WIDTH_OR_SIGN'):
            parity.check_vectors(obs, 'legacy-bridge', obs['source'], 'c'*64, ports)
        obs['vectors'][0]['outputs']['out'] = '2'
        obs['vectors'].pop()
        self.assertNotEqual(len(first), len(parity.check_vectors(obs, 'legacy-bridge', obs['source'], 'c'*64, ports)))

    def test_forged_pinned_native_is_still_notrun(self):
        digest = 'a'*64
        receipt = {'status': 'candidate-rtl-simulation-passed', 'trust': 'StructuralOnly',
                   'model': {'hwirArtifactSha256': digest},
                   'commands': {'rtlExportExit': 0, 'compileExit': 0, 'simulationExit': 0},
                   'tools': {x: 'b'*64 for x in
                             ('circtExecutableSha256', 'iverilogExecutableSha256', 'vvpExecutableSha256')}}
        raw = json.dumps(receipt).encode('utf-8')
        self.assertEqual('NotRun', parity.inspect_native(raw, digest))
        receipt['tools']['vvpExecutableSha256'] = '0'
        with self.assertRaisesRegex(parity.Refused, 'UNPINNED_NATIVE_TOOLS'):
            parity.inspect_native(json.dumps(receipt).encode('utf-8'), digest)
        receipt['tools']['vvpExecutableSha256'] = 'b'*64
        with self.assertRaisesRegex(parity.Refused, 'STALE_NATIVE_RECEIPT'):
            parity.inspect_native(json.dumps(receipt).encode('utf-8'), 'c'*64)

    def test_timing_boolean_injection_is_refused(self):
        module = {'contract': {'kind': 'Combinational',
                               'timing': {'latencyCycles': True, 'initiationInterval': 1}},
                  'ports': [], 'clockDomains': []}
        with self.assertRaisesRegex(parity.Refused, 'MISSING_TIMING'):
            parity.typed_contract(module)


if __name__ == '__main__':
    unittest.main(verbosity=2)
