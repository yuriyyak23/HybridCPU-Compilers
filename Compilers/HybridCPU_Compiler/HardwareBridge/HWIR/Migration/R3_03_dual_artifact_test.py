#!/usr/bin/env python3
"""R3-03 synthetic negative tests; NO Golden execution / Core provenance claim."""
import copy
import importlib.util
import io
import json
from pathlib import Path
import contextlib
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('R3_03_dual_artifact_gate',
                                            Path(__file__).with_name('R3_03_dual_artifact_gate.py'))
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


def raw(o):
    return json.dumps(o, sort_keys=True, separators=(',', ':')).encode('utf-8')


class DualArtifactEvidenceMutations(unittest.TestCase):
    def setUp(self):
        self.source = b'// SYNTHETIC FORMAT ONLY, NOT REAL GOLDEN\nint AddWord(int a,int b) => unchecked(a+b);\n'
        self.source_identity = {'commit': 'a' * 40, 'path': 'Model/ScalarAluOps.cs',
                                'method': 'ScalarAluOps.AddWord', 'sha256': gate.sha256(self.source)}
        self.full_source = {**self.source_identity, 'gitBlobSha1': gate.git_blob(self.source),
                            'methodToken': '0x06000001',
                            'signature': 'int ScalarAluOps.AddWord(int,int)',
                            'ownedClosureSha256': 'c' * 64}
        self.ports = [
            {'name': name, 'direction': direction, 'kind': 'Data',
             'type': {'width': 32, 'signedness': 'Signed', 'fourState': False}}
            for name, direction in [('a', 'Input'), ('b', 'Input'), ('out', 'Output')]
        ]
        self.artifacts = []
        for top in ('legacy', 'core'):
            ports = copy.deepcopy(self.ports)
            module = {'id': top, 'origin': {'commit': 'a' * 40, 'path': 'Model/ScalarAluOps.cs',
                           'member': 'ScalarAluOps.AddWord', 'role': 'Primary'},
                      'ports': ports, 'clockDomains': [],
                      'contract': {'kind': 'Combinational', 'ports': copy.deepcopy(ports),
                                   'timing': {'latencyCycles': 0, 'initiationInterval': 1},
                                   'overflow': 'Wrap', 'reset': None,
                                   'memory': None, 'faults': 'None'}}
            self.artifacts.append({'domain': gate.parity.CANONICAL, 'schemaVersion': 'hwir/v0.1',
                                   'topModule': top, 'modules': [module]})
        self.vectors = [
            {'id': 'zero', 'inputs': {'a': '0', 'b': '0'}, 'outputs': {'out': '0'}},
            {'id': 'wrap32', 'inputs': {'a': '4294967295', 'b': '1'}, 'outputs': {'out': '0'}},
            {'id': 'sign-bit', 'inputs': {'a': '2147483647', 'b': '1'},
             'outputs': {'out': '2147483648'}}]
        self.obs = []
        for idx, producer in enumerate(('legacy-bridge', 'core-generic')):
            self.obs.append({'schema': gate.parity.SCHEMA, 'producer': producer,
                             'source': copy.deepcopy(self.source_identity),
                             'hwirSha256': gate.sha256(raw(self.artifacts[idx])),
                             'vectors': copy.deepcopy(self.vectors)})
        execution = {k: code * 64 for k, code in (
            ('invocationSha256', '1'), ('executableSha256', '2'),
            ('peSha256', '3'), ('pdbSha256', '4'), ('companionSha256', '5'))}
        self.golden = {'schema': gate.GOLDEN_SCHEMA, 'producer': 'GoldenObserved',
                       'source': copy.deepcopy(self.source_identity),
                       'methodToken': '0x06000001', 'signature': self.full_source['signature'],
                       'ownedClosureSha256': 'c' * 64, 'execution': execution,
                       'vectors': copy.deepcopy(self.vectors)}
        self.review = {'schema': gate.REVIEW_SCHEMA, 'slice': 'scalar',
            'source': copy.deepcopy(self.full_source),
            'legacy': {}, 'core': {'sourceSha256': gate.sha256(self.source),
                'ownedClosureSha256': 'c' * 64, 'compilerCommit': 'b' * 40,
                'stageReceiptSha256': '6' * 64, 'executableSha256': '7' * 64,
                'rule': 'CW-INT-06/source-owned-generic'},
            'golden': {'sourceSha256': gate.sha256(self.source), **execution}}
        self.repin()

    def repin(self):
        for idx, key in enumerate(('legacy', 'core')):
            self.obs[idx]['hwirSha256'] = gate.sha256(raw(self.artifacts[idx]))
            self.review[key]['hwirSha256'] = gate.sha256(raw(self.artifacts[idx]))
            self.review[key]['observationSha256'] = gate.sha256(raw(self.obs[idx]))
        self.review['golden']['observationSha256'] = gate.sha256(gate.canonical(self.golden))

    def compare(self, native=None):
        return gate.compare_bytes(gate.canonical(self.review), self.source,
                                  raw(self.artifacts[0]), raw(self.artifacts[1]),
                                  raw(self.obs[0]), raw(self.obs[1]),
                                  gate.canonical(self.golden), native)

    def refuses(self, code, native=None):
        with self.assertRaisesRegex(gate.Blocked, code):
            self.compare(native)

    def test_positive_synthetic_report_is_never_semantic_or_rtl_acceptance(self):
        r = self.compare()
        self.assertEqual('ReportOnly', r['status'])
        self.assertEqual('Blocked', r['semanticEquivalence'])
        self.assertEqual('UnqualifiedExternalClaim', r['independentGoldenProvenance'])
        self.assertEqual('Blocked', r['independentRtlAcceptance'])
        self.assertEqual('NotRun', r['native'])
        self.assertEqual(3, r['vectorsCompared'])

    def test_legacy_relabelled_as_core_is_refused(self):
        self.artifacts[1] = copy.deepcopy(self.artifacts[0])
        self.repin()
        self.refuses('R303_LEGACY_AS_CORE_ALIAS')

    def test_wrong_source_and_method_identity_refused(self):
        self.source += b'// tamper'
        self.refuses('R303_SOURCE_SHA_MISMATCH')
        self.source = self.source[:-9]
        self.artifacts[1]['modules'][0]['origin']['member'] = 'UnknownMethod'
        self.repin()
        self.refuses('R303_UNOWNED_SOURCE_METHOD')

    def test_wrong_method_token_signature_closure_refused(self):
        self.golden['methodToken'] = '0x06000002'
        self.review['golden']['observationSha256'] = gate.sha256(gate.canonical(self.golden))
        self.refuses('R303_GOLDEN_METHOD_methodToken')
        self.golden['methodToken'] = '0x06000001'
        self.review['core']['ownedClosureSha256'] = 'f' * 64
        self.refuses('R303_CLOSURE_SOURCE_MISMATCH')

    def test_width_sign_and_overflow_mutations(self):
        for key, v in [('width', 64), ('signedness', 'Unsigned')]:
            with self.subTest(mutation=key):
                self.setUp()
                p = self.artifacts[1]['modules'][0]['ports'][0]['type']
                c = self.artifacts[1]['modules'][0]['contract']['ports'][0]['type']
                p[key] = c[key] = v
                self.repin()
                self.refuses('R303_HWIR_WIDTH_SIGN_OVERFLOW_CLOCK_MEMORY_MISMATCH')
        self.setUp()
        self.artifacts[1]['modules'][0]['contract']['overflow'] = 'Saturate'
        self.repin()
        self.refuses('R303_HWIR_WIDTH_SIGN_OVERFLOW_CLOCK_MEMORY_MISMATCH')

    def test_clock_reset_memory_role_and_temporal_state_refused(self):
        for key, value in (('memory', {'role': 'Opaque'}), ('reset', 'sync')):
            with self.subTest(role=key):
                self.setUp()
                for artifact in self.artifacts:
                    artifact['modules'][0]['contract'][key] = value
                self.repin()
                self.refuses('R303_SCALAR_CLOCK_RESET_MEMORY_ROLE')
        self.setUp()
        self.review['slice'] = 'scheduler'
        self.refuses('R303_CW_INT_13_TEMPORAL_BLOCKED')

    def test_missing_explicit_reset_memory_or_overflow_role_refused(self):
        for role in ('reset', 'memory', 'overflow'):
            with self.subTest(role=role):
                self.setUp()
                for artifact in self.artifacts:
                    del artifact['modules'][0]['contract'][role]
                self.repin()
                self.refuses('R303_MISSING_EXPLICIT_CONTRACT_ROLE')

    def test_wrong_golden_input_output_order_and_binary_pins(self):
        self.golden['vectors'][0]['outputs']['out'] = '1'
        self.review['golden']['observationSha256'] = gate.sha256(gate.canonical(self.golden))
        self.refuses('R303_INDEPENDENT_GOLDEN_VECTOR_MISMATCH')
        self.setUp()
        self.golden['vectors'].reverse()
        self.review['golden']['observationSha256'] = gate.sha256(gate.canonical(self.golden))
        self.refuses('R303_INDEPENDENT_GOLDEN_VECTOR_MISMATCH')
        self.setUp()
        self.golden['execution']['peSha256'] = 'f' * 64
        self.review['golden']['observationSha256'] = gate.sha256(gate.canonical(self.golden))
        self.refuses('R303_GOLDEN_BINARY_peSha256')

    def test_stale_core_hwir_observation_golden_and_native_receipts(self):
        self.artifacts[1]['topModule'] = 'altered'
        self.refuses('R303_STALE_CORE_HWIR')
        self.setUp()
        self.obs[1]['vectors'].pop()
        self.refuses('R303_STALE_CORE_OBS')
        self.setUp()
        self.golden['vectors'].pop()
        self.refuses('R303_STALE_GOLDEN_OBS')
        self.setUp()
        native = {'status': 'candidate-rtl-simulation-passed', 'trust': 'StructuralOnly',
                  'model': {'hwirArtifactSha256': '0' * 64},
                  'commands': {'rtlExportExit': 0, 'compileExit': 0, 'simulationExit': 0},
                  'tools': {k: 'a' * 64 for k in
                            ('circtExecutableSha256', 'iverilogExecutableSha256', 'vvpExecutableSha256')}}
        self.refuses('R303_STALE_NATIVE_RECEIPT', raw(native))

    def test_unknown_schema_noncanonical_duplicate_json_and_role(self):
        bad = copy.deepcopy(self.review)
        bad['schema'] = 'unknown'
        with self.assertRaisesRegex(gate.Blocked, 'R303_UNKNOWN_SCHEMA'):
            gate.compare_bytes(gate.canonical(bad), self.source, b'', b'', b'', b'', b'')
        with self.assertRaisesRegex(gate.Blocked, 'R303_NONCANONICAL_JSON'):
            gate.compare_bytes(json.dumps(self.review, indent=2).encode(),
                               self.source, b'', b'', b'', b'', b'')
        dup = gate.canonical(self.review).replace(b'"schema":', b'"schema":"evil","schema":', 1)
        with self.assertRaisesRegex(gate.Blocked, 'R303_DUPLICATE_JSON_PROPERTY'):
            gate.compare_bytes(dup, self.source, b'', b'', b'', b'', b'')
        self.golden['producer'] = 'core-generic'
        self.review['golden']['observationSha256'] = gate.sha256(gate.canonical(self.golden))
        self.refuses('R303_GOLDEN_ORIGIN_ROLE')

    def test_source_path_and_generic_rule_refused(self):
        self.review['source']['path'] = '../escape.cs'
        self.refuses('R303_SOURCE_PATH')
        self.setUp()
        self.review['core']['rule'] = 'GoldenAddWordCandidateCompiler'
        self.refuses('R303_NOT_GENERIC_CORE_RULE')

    def test_cli_missing_core_and_symlink_refused(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            source = root / 'source.cs'
            source.write_bytes(self.source)
            review = root / 'review.json'
            review.write_bytes(gate.canonical(self.review))
            legacy = root / 'legacy.json'
            legacy.write_bytes(raw(self.artifacts[0]))
            args = ['--review', str(review), '--source', str(source),
                    '--legacy-hwir', str(legacy), '--core-hwir', str(root / 'missing.json'),
                    '--legacy-observations', str(root / 'lobs.json'),
                    '--core-observations', str(root / 'cobs.json'),
                    '--golden-observations', str(root / 'gobs.json')]
            stdout, stderr = io.StringIO(), io.StringIO()
            with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
                result = gate.main(args)
            self.assertEqual(2, result)
            self.assertEqual('', stdout.getvalue())
            self.assertIn('R303_MISSING_OR_UNSAFE_INPUT', stderr.getvalue())
            link = root / 'linked.cs'
            try:
                link.symlink_to(source)
            except (OSError, NotImplementedError):
                self.skipTest('symlinks unavailable')
            with self.assertRaisesRegex(gate.Blocked, 'R303_SYMLINK_PATH'):
                gate.bounded_read(link, 'source', float('inf'))


if __name__ == '__main__':
    unittest.main(verbosity=2)
