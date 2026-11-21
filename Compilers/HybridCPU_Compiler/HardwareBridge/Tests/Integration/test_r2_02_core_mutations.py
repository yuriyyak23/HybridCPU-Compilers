"""Pure Python mutation-shape tests; Core judgments require paired .NET CI."""
import copy
import json
from pathlib import Path
import shutil
import tempfile
import unittest

from r2_02_core_mutations import MUTATIONS, canonical, digest, prepare_fixture_mutation
from R2_02_ReceiptGate import EXPECTED, Refusal, validate, read_receipt


class R202MutationShapeTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix='hb-r2-02-mutate-'))
        self.addCleanup(lambda: shutil.rmtree(self.tmp, ignore_errors=True))
        self.src = self.tmp / 'inputs' / '00000.cs'
        self.src.parent.mkdir()
        self.src.write_bytes(b'class A {}\n')
        self.chunk = self.tmp / 'bridge-index' / 'chunk-0000.jsonl'
        self.chunk.parent.mkdir()
        self.chunk.write_bytes(canonical({'Path': 'inputs/00000.cs', 'Role': 'source',
            'Sha256': digest(self.src.read_bytes()), 'Bytes': self.src.stat().st_size}) + b'\n')
        self.index = self.tmp / 'bridge-index.json'
        self.index.write_bytes(canonical({
            'Schema': 'circuitweave.bridge-source-closure-index/v2',
            'Roots': [{'TypeName':'A','MethodName':'F','MetadataToken':None,
                       'TypeArguments':[],'MethodArguments':[]}],
            'Chunks': [{'Path': 'bridge-index/chunk-0000.jsonl',
                        'Sha256': digest(self.chunk.read_bytes()),
                        'Bytes': self.chunk.stat().st_size, 'Rows': 1}],
            'ClosureSha256': digest(self.chunk.read_bytes())}))
        self.original = self.index.read_bytes()
        self.original_chunk = self.chunk.read_bytes()
        self.original_source = self.src.read_bytes()

    def test_all_cases_are_named_and_byte_mutating(self):
        self.assertEqual(len(MUTATIONS), 8)
        self.assertEqual(MUTATIONS, EXPECTED)
        for kind, code in MUTATIONS.items():
            with self.subTest(kind=kind):
                root = self.tmp / kind
                shutil.copytree(self.tmp, root, ignore=lambda d,n: set(MUTATIONS) & set(n))
                old_index = (root / 'bridge-index.json').read_bytes()
                old_chunk = (root / 'bridge-index' / 'chunk-0000.jsonl').read_bytes()
                old_input = (root / 'inputs' / '00000.cs').read_bytes()
                index_pin, closure_pin, diagnostic = prepare_fixture_mutation(root, kind)
                self.assertEqual(diagnostic, code)
                self.assertEqual(len(index_pin), 64)
                self.assertEqual(len(closure_pin), 64)
                if kind == 'wrong-index-pin':
                    self.assertNotEqual(index_pin, digest(old_index))
                elif kind == 'omitted-chunk':
                    self.assertFalse((root / 'bridge-index' / 'chunk-0000.jsonl').exists())
                elif kind == 'input-bytes':
                    self.assertNotEqual((root / 'inputs' / '00000.cs').read_bytes(), old_input)
                elif kind == 'chunk-bytes':
                    self.assertNotEqual((root / 'bridge-index' / 'chunk-0000.jsonl').read_bytes(), old_chunk)
                else:
                    self.assertNotEqual((root / 'bridge-index.json').read_bytes(), old_index)
                if kind == 'duplicate-chunk':
                    chunks = json.loads((root / 'bridge-index.json').read_bytes())['Chunks']
                    self.assertEqual(len(chunks), 2)
                    self.assertEqual(chunks[0], chunks[1])
                if kind == 'wrong-role':
                    self.assertEqual(digest((root / 'bridge-index' / 'chunk-0000.jsonl').read_bytes()),
                                     json.loads((root / 'bridge-index.json').read_bytes())['Chunks'][0]['Sha256'])

    def test_unknown_case_fail_closed(self):
        with self.assertRaisesRegex(ValueError, 'unknown'):
            prepare_fixture_mutation(self.tmp, 'invented-pass')

    def test_duplicate_original_json_refused(self):
        self.index.write_bytes(b'{"Schema":"x","Schema":"x","Roots":[],"Chunks":[]}')
        with self.assertRaisesRegex(ValueError, 'duplicate'):
            prepare_fixture_mutation(self.tmp, 'wrong-index-pin')

    def test_source_workspace_is_not_changed_by_copy_case(self):
        cloned = self.tmp / 'cloned'
        shutil.copytree(self.tmp, cloned)
        prepare_fixture_mutation(cloned, 'input-bytes')
        self.assertEqual(self.index.read_bytes(), self.original)
        self.assertEqual(self.chunk.read_bytes(), self.original_chunk)
        self.assertEqual(self.src.read_bytes(), self.original_source)



class R202ReceiptGateTests(unittest.TestCase):
    """Synthetic receipt-shape guards only; do NOT execute or impersonate Core."""

    BRIDGE = '1' * 40
    CORE = '2' * 40
    SDK = '11.0.100-rc.2.26504.105'

    def sample(self):
        sha = 'a' * 64
        index_sha = 'A' * 64
        stages = {
            'R1-01-observer': 'NotRun',
            'R1-02-capture': 'Passed',
            'R1-03-pe-pdb': 'NotRun',
            'R2-01-stage': 'Passed',
            'R2-02-core-reader': 'Passed',
        }
        command_names = ['dotnet-version', 'R1-02-capture', 'R2-01-stage',
                         'R2-02-build-core-reader', 'R2-02-core-reader',
                         *('R2-02-negative-' + n for n in EXPECTED)]
        commands = [{
            'label': n, 'exit': 1 if n.startswith('R2-02-negative-') else 0,
            'status': 'Failed' if n.startswith('R2-02-negative-') else 'Passed',
            'argv': ['/pinned/dotnet', 'isolated-host'],
            'executableSha256': sha, 'stdoutSha256': sha, 'stderrSha256': sha,
        } for n in command_names]
        matrix = [{
            'mutation': name, 'expectedCode': code, 'actualCode': code,
            'actualState': 'Blocked' if code == 'CWINT03_V2_IO' else 'Rejected',
            'expectedRefusal': 'Passed', 'coreExecutableSha256': sha,
        } for name, code in EXPECTED.items()]
        return {
            'schema': 'hybridcpu.hardwarebridge.cross-repo-replay/v1',
            'scope': 'fixture', 'disposition': 'FixtureTransportOnly',
            'bridgeSha': self.BRIDGE, 'coreSha': self.CORE, 'sdkPin': self.SDK,
            'blocker': None, 'sourceUnchanged': True,
            'goldenBefore': {'inventorySha256': sha},
            'goldenAfter': {'inventorySha256': sha},
            'bridgeBefore': {'inventorySha256': sha},
            'bridgeAfter': {'inventorySha256': sha},
            'coreBefore': {'inventorySha256': sha},
            'coreAfter': {'inventorySha256': sha},
            'stagedBefore': {'inventorySha256': sha},
            'stagedAfter': {'inventorySha256': sha},
            'stages': stages,
            'captureIndexSha256': sha, 'captureSourceRows': 1001,
            'coreReaderAssemblySha256': sha,
            'coreClosureSha256': index_sha, 'coreIndexSha256': index_sha,
            'coreReader': {
                'state': 'Candidate', 'code': 'CWINT03_V2_UNQUALIFIED_CLOSURE',
                'rows': 1001, 'sourceRows': 1001,
                'indexSha256': index_sha, 'closureSha256': index_sha,
            },
            'negativeCoreMatrix': matrix, 'commands': commands,
        }

    def check(self, value):
        return validate(value, bridge_sha=self.BRIDGE, core_sha=self.CORE, sdk=self.SDK)

    def test_complete_synthetic_receipt_shape_only(self):
        result = self.check(self.sample())
        self.assertEqual(result['coreMutationRefusals'], 8)
        self.assertEqual(result['golden'], 'NotRun')
        self.assertEqual(result['rtl'], 'NotRun')

    def test_skip_failure_or_dishonest_partial_receipt_never_passes(self):
        cases = {
            'skipped-paired-core': lambda d: d['stages'].update({'R2-02-core-reader': 'NotRun'}),
            'wrong-core-pin': lambda d: d.update({'coreSha': '0' * 40}),
            'wrong-sdk': lambda d: d.update({'sdkPin': '11.0.100'}),
            'misleading-verification': lambda d: d.update({'disposition': 'Verified'}),
            'stale-core-result': lambda d: d['coreReader'].update({'rows': 1000}),
            'metadata-inflated-capture': lambda d: d.update({'captureSourceRows': 1000}),
            'metadata-inflated-reader': lambda d: d['coreReader'].update({'sourceRows': 1000}),
            'stale-compiled-host': lambda d: d['negativeCoreMatrix'][0].update({'coreExecutableSha256': '0' * 64}),
            'missing-mutation': lambda d: d['negativeCoreMatrix'].pop(),
            'wrong-mutated-diagnostic': lambda d: d['negativeCoreMatrix'][0].update({'actualCode': 'CWINT03_V2_MALFORMED'}),
            'negative-process-passed': lambda d: d['commands'][-1].update({'exit': 0, 'status': 'Passed'}),
            'no-source-inventory': lambda d: d.update({'sourceUnchanged': False}),
            'core-source-changed': lambda d: d['coreAfter'].update({'inventorySha256': 'b' * 64}),
            'missing-command-hash': lambda d: d['commands'][0].pop('executableSha256'),
        }
        for name, mutation in cases.items():
            with self.subTest(name=name):
                d = copy.deepcopy(self.sample())
                mutation(d)
                with self.assertRaises(Refusal):
                    self.check(d)

    def test_duplicate_receipt_json_field_is_fail_closed(self):
        with tempfile.TemporaryDirectory() as work:
            receipt = Path(work) / 'receipt.json'
            receipt.write_bytes(b'{"scope":"fixture","scope":"golden"}')
            with self.assertRaisesRegex(Refusal, 'DUPLICATE_JSON'):
                read_receipt(receipt)


if __name__ == '__main__':
    unittest.main()
