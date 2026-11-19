"""Transport-only mutation checks against the *real* HB-INT-02 capture module. No Csc execution."""
import copy
import hashlib
import json
from pathlib import Path
import shutil
import sys
import tempfile
import time
import unittest
import uuid

sys.path.insert(0, str(Path(__file__).parent))
import segmented_csc_capture_v2 as c
import stage_core_v2 as stage
from test_segmented_csc_capture_v2 import ClosureTests

class CoreV2StagingTests(unittest.TestCase):
    def setUp(self):
        self.fixture = ClosureTests("test_positive_capture_and_byte_replay")
        self.fixture.setUp()
        self.addCleanup(self.fixture.tearDown)
        self.bundle, self.pin = self.fixture.run_capture()
        self.addCleanup(lambda: shutil.rmtree(self.bundle, ignore_errors=True))
        self.source_sha = c.sha(self.fixture.original.read_bytes())
        self.roots = self.fixture.tmp / "roots.json"
        self.roots.write_bytes(c.canonical({"Schema": stage.ROOTS, "Roots": [{
            "TypeName": "A", "MethodName": "Step", "MetadataToken": None,
            "TypeArguments": [], "MethodArguments": []}]}))
        self.roots_sha = c.sha(self.roots.read_bytes())
        self.target = Path(tempfile.gettempdir()) / ("hb-int-0203-" + uuid.uuid4().hex)
        self.addCleanup(lambda: shutil.rmtree(self.target, ignore_errors=True))

    def run_stage(self, **kwargs):
        return stage.stage(self.bundle, self.pin, self.roots, self.roots_sha,
                           str(self.target), **kwargs)

    def test_positive_lossless_bytes_with_explicit_roots(self):
        result = self.run_stage()
        self.assertEqual(stage.verify(self.target, self.pin, self.roots_sha), result)
        index = json.loads((self.target / "bridge-index.json").read_bytes())
        self.assertEqual(index["Schema"], "circuitweave.bridge-source-closure-index/v2")
        mapping = json.loads((self.target / "capture-map.json").read_bytes())
        row = next(x for x in mapping["Rows"] if x["LogicalPath"] == "project/A.cs")
        self.assertEqual(row["SourceRole"], "primary")
        self.assertEqual(row["Sha256"], self.source_sha.upper())
        self.assertEqual((self.target / row["Path"]).read_bytes(), self.fixture.original.read_bytes())
        self.assertEqual(c.sha(self.fixture.original.read_bytes()), self.source_sha)
        self.assertTrue(all(r["SourceRole"] == "metadata-only" for r in mapping["Rows"]
                            if r["Kind"] == "reference"))
        print("HB-INT-02/03 synthetic source only Core closure SHA256=" + result)

    def test_wrong_independent_source_and_root_sha(self):
        with self.assertRaises(ValueError):
            stage.stage(self.bundle, "0" * 64, self.roots, self.roots_sha, self.target)
        with self.assertRaises(ValueError):
            stage.stage(self.bundle, self.pin, self.roots, "0" * 64, self.target)
        self.assertFalse(self.target.exists())

    def test_duplicate_and_unknown_root_rejected(self):
        data = json.loads(self.roots.read_bytes())
        data["Roots"].append(copy.deepcopy(data["Roots"][0]))
        self.roots.write_bytes(c.canonical(data))
        self.roots_sha = c.sha(self.roots.read_bytes())
        with self.assertRaisesRegex(ValueError, "duplicate root"):
            self.run_stage()
        self.roots.write_bytes(b'{"Schema":"x","Roots":[],"Roots":[]}')
        self.roots_sha = c.sha(self.roots.read_bytes())
        with self.assertRaisesRegex(ValueError, "duplicate JSON property"):
            self.run_stage()
        self.assertFalse(self.target.exists())

    def test_metadata_body_promotion_even_when_rehashed(self):
        self.fixture.recompute(self.bundle, lambda doc: next(x for x in doc["entries"]
            if x["kind"] == "reference").update(role="owned-body"))
        with self.assertRaises(ValueError):
            self.run_stage()
        self.assertFalse(self.target.exists())

    def test_missing_or_modified_captured_chunk_rejected(self):
        chunk = next(Path(self.bundle).glob("chunk-*.bin"))
        chunk.write_bytes(b"changed, still not an actual Csc executable")
        with self.assertRaises(ValueError):
            self.run_stage()
        self.assertFalse(self.target.exists())

    def test_output_bytes_mutation_rejected(self):
        self.run_stage()
        file = next((self.target / "inputs").glob("*.cs"))
        file.write_bytes(b"forged")
        with self.assertRaisesRegex(ValueError, "mutated output file"):
            stage.verify(self.target, self.pin, self.roots_sha)

    def test_root_manifest_mutation_after_staging_rejected(self):
        self.run_stage()
        (self.target / "roots-manifest.json").write_bytes(b"{}")
        with self.assertRaises(ValueError):
            stage.verify(self.target, self.pin, self.roots_sha)
        self.assertEqual(c.sha(self.fixture.original.read_bytes()), self.source_sha)

    def test_jsonl_chunk_corruption_refused(self):
        self.run_stage()
        chunk = next((self.target / "bridge-index").glob("*.jsonl"))
        chunk.write_bytes(chunk.read_bytes() + b"{}\n")
        with self.assertRaises(ValueError):
            stage.verify(self.target, self.pin, self.roots_sha)

    def test_timeout_and_interruption_remove_partial_staging(self):
        def expire():
            time.sleep(.03)
        with self.assertRaisesRegex(ValueError, "timeout"):
            self.run_stage(timeout_seconds=.001, after_copy=expire)
        self.assertFalse(self.target.exists())
        def cancel():
            raise KeyboardInterrupt()
        with self.assertRaises(KeyboardInterrupt):
            self.run_stage(after_copy=cancel)
        self.assertFalse(self.target.exists())

    def test_path_collision_symlink_and_added_files(self):
        self.target.mkdir()
        with self.assertRaisesRegex(ValueError, "new direct TEMP"):
            self.run_stage()
        self.target.rmdir()
        self.run_stage()
        (self.target / "extra.txt").write_bytes(b"unexpected")
        with self.assertRaisesRegex(ValueError, "extra/missing"):
            stage.verify(self.target, self.pin, self.roots_sha)

    def test_too_many_core_rows_are_blocked(self):
        doc = json.loads((Path(self.bundle) / "index.json").read_bytes())
        doc["entries"] = [copy.deepcopy(doc["entries"][0]) for _ in range(stage.MAX_ROWS + 1)]
        doc["closureRootSha256"] = c.sha(c.canonical({k:v for k,v in doc.items()
                                                    if k != "closureRootSha256"}))
        (Path(self.bundle)/"index.json").write_bytes(c.canonical(doc))
        self.pin = doc["closureRootSha256"]
        with self.assertRaises(ValueError):
            self.run_stage()

if __name__ == "__main__":
    unittest.main(verbosity=2)
