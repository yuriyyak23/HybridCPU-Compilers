"""Pure Python mutation-shape tests; Core judgments require paired .NET CI."""
import json
from pathlib import Path
import shutil
import tempfile
import unittest

from r2_02_core_mutations import MUTATIONS, canonical, digest, prepare_fixture_mutation


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
        self.assertEqual(len(MUTATIONS), 7)
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


if __name__ == '__main__':
    unittest.main()
