"""HB-INT-02/03 Core v2 byte-transport tests. All inputs are synthetic software facts.

The tests intentionally do not claim a real executed Csc task, PE/PDB, Golden
oracle or RTL/physical evidence. No fixture success substitutes for Core import.
"""
import hashlib
import json
from pathlib import Path
import shutil
import sys
import tempfile
import time
import unittest
import uuid

import csc_to_core_v2 as transport
import segmented_csc_capture_v2 as c
from test_segmented_csc_capture_v2 import ClosureTests


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


class ConversionTests(unittest.TestCase):
    def setUp(self):
        self.fixture = ClosureTests("test_positive_capture_and_byte_replay")
        self.fixture.setUp()
        self.addCleanup(self.fixture.tearDown)
        self.addCleanup(self.fixture.doCleanups)
        self.roots = [{"TypeName": "Fixture", "MethodName": "AddWord",
                       "MetadataToken": None, "TypeArguments": [], "MethodArguments": []}]
        self.capture, self.root = self.fixture.run_capture()
        self.output = self.next_output()

    def next_output(self):
        return str(Path(tempfile.gettempdir()) / ("hb-int-02-core-transport-" + uuid.uuid4().hex))

    def export(self, **options):
        result = transport.export(self.capture, self.root, self.roots, self.output, **options)
        self.addCleanup(lambda: shutil.rmtree(self.output, ignore_errors=True))
        self.assertEqual(transport.validate_emitted(self.capture, self.root, self.roots, self.output), result)
        return Path(self.output), result

    def test_positive_byte_materialization_and_stable_core_index(self):
        golden_before = sha(self.fixture.original)
        output, expected = self.export()
        index_bytes = (output / "bridge-index.json").read_bytes()
        index = json.loads(index_bytes)
        receipt = json.loads((output / "bridge-export-receipt.json").read_bytes())
        self.assertEqual(index["Schema"], transport.SCHEMA)
        self.assertEqual(index["ClosureSha256"], expected)
        self.assertEqual(receipt["Disposition"], "unqualified-software-byte-transport")
        self.assertEqual(receipt["HbCaptureRootSha256"], self.root)
        self.assertEqual(receipt["CoreIndexSha256"], hashlib.sha256(index_bytes).hexdigest().upper())
        self.assertEqual(receipt["Roots"], self.roots)
        self.assertEqual(sha(self.fixture.original), golden_before)
        self.assertEqual([x["Role"] for x in receipt["Inputs"] if x["HbKind"] == "reference"],
                         ["metadata-only"])
        self.assertTrue(any(x["Role"] == "source" for x in receipt["Inputs"]))
        self.assertTrue(all(x["Path"].startswith("inputs/") and ".." not in x["Path"]
                            for x in receipt["Inputs"]))
        for descriptor in index["Chunks"]:
            chunk = (output / descriptor["Path"]).read_bytes()
            self.assertEqual(hashlib.sha256(chunk).hexdigest().upper(), descriptor["Sha256"])
            self.assertEqual(len(chunk), descriptor["Bytes"])
        self.assertEqual(transport.raw_json(index), index_bytes)
        output2 = self.next_output()
        self.addCleanup(lambda: shutil.rmtree(output2, ignore_errors=True))
        self.assertEqual(transport.export(self.capture, self.root, self.roots, output2), expected)
        self.assertEqual((Path(output2) / "bridge-index.json").read_bytes(), index_bytes)
        self.assertEqual((Path(output2) / "bridge-export-receipt.json").read_bytes(),
                         (output / "bridge-export-receipt.json").read_bytes())
        print("PASS synthetic HB->Core v2 indexSHA256=" + hashlib.sha256(index_bytes).hexdigest().upper() +
              " closureSHA256=" + expected + " GoldenSHA256=" + golden_before)

    def test_wrong_pinned_capture_root(self):
        with self.assertRaisesRegex(ValueError, "stale external"):
            transport.export(self.capture, "0" * 64, self.roots, self.output)
        self.assertFalse(Path(self.output).exists())

    def test_duplicate_root_and_unknown_identity(self):
        for roots in (self.roots * 2,
                      [{**self.roots[0], "Extra": "Verified"}],
                      [{**self.roots[0], "TypeName": "../Model"}],
                      [{**self.roots[0], "TypeArguments": ["int"]}]):
            with self.subTest(roots=roots), self.assertRaises(ValueError):
                transport.export(self.capture, self.root, roots, self.output)
            self.assertFalse(Path(self.output).exists())

    def test_reject_output_overwrite_or_golden_output(self):
        Path(self.output).mkdir()
        self.addCleanup(lambda: shutil.rmtree(self.output, ignore_errors=True))
        with self.assertRaisesRegex(ValueError, "NEW direct"):
            transport.export(self.capture, self.root, self.roots, self.output)
        with self.assertRaisesRegex(ValueError, "NEW direct"):
            transport.export(self.capture, self.root, self.roots, str(self.fixture.golden / "oops"))

    def test_reject_recalculated_unknown_kind_and_promoted_reference(self):
        for edit in (
            lambda i: i["entries"].__getitem__(0).__setitem__("kind", "not-an-input"),
            lambda i: next(x for x in i["entries"] if x["kind"] == "reference").__setitem__("role", "primary")
        ):
            with self.subTest(edit=edit):
                self.fixture.recompute(self.capture, edit)
                newroot = json.loads((Path(self.capture) / "index.json").read_bytes())["closureRootSha256"]
                with self.assertRaises(ValueError):
                    transport.export(self.capture, newroot, self.roots, self.output)
                self.assertFalse(Path(self.output).exists())
                # restore exact original index from an independent fixture capture
                self.capture, self.root = self.fixture.run_capture()

    def test_tampered_hb_chunk_and_missing_chunk(self):
        index = json.loads((Path(self.capture) / "index.json").read_bytes())
        first = index["entries"][0]["chunks"][0]["name"]
        file = Path(self.capture) / first
        raw = file.read_bytes()
        file.write_bytes(raw + b"x")
        with self.assertRaises(ValueError):
            transport.export(self.capture, self.root, self.roots, self.output)
        file.write_bytes(raw)
        file.unlink()
        with self.assertRaises(ValueError):
            transport.export(self.capture, self.root, self.roots, self.output)
        self.assertFalse(Path(self.output).exists())

    def test_mutated_core_staged_file_or_index(self):
        out, _ = self.export()
        receipt = json.loads((out / "bridge-export-receipt.json").read_bytes())
        path = out / receipt["Inputs"][0]["Path"]
        original = path.read_bytes()
        path.write_bytes(original + b"x")
        with self.assertRaises(ValueError):
            transport.validate_emitted(self.capture, self.root, self.roots, out)
        path.write_bytes(original)
        index_path = out / "bridge-index.json"
        index = index_path.read_bytes()
        index_path.write_bytes(index + b" ")
        with self.assertRaises(ValueError):
            transport.validate_emitted(self.capture, self.root, self.roots, out)
        index_path.write_bytes(index)
        (out / "untracked.txt").write_bytes(b"extra")
        with self.assertRaisesRegex(ValueError, "missing/extra"):
            transport.validate_emitted(self.capture, self.root, self.roots, out)

    def test_missing_or_too_many_sources(self):
        self.fixture.source.write_bytes(b"\xffinvalid-utf8")
        self.fixture.original.write_bytes(self.fixture.source.read_bytes())
        self.fixture.observation["projects"][0]["sources"][0]["sha256"] = sha(self.fixture.source)
        self.fixture.save()
        self.capture, self.root = self.fixture.run_capture()
        with self.assertRaisesRegex(ValueError, "UTF-8"):
            transport.export(self.capture, self.root, self.roots, self.output)
        self.assertFalse(Path(self.output).exists())
        # Valid HB-index source still cannot be made metadata-only at Core.
        self.fixture.recompute(self.capture,
            lambda i: [x.__setitem__("role", "metadata-only")
                       for x in i["entries"] if x["kind"] in transport.SOURCE_KINDS])
        root = json.loads((Path(self.capture) / "index.json").read_bytes())["closureRootSha256"]
        with self.assertRaises(ValueError):
            transport.export(self.capture, root, self.roots, self.output)

    def test_timeout_cancels_and_removes_partial_output(self):
        def slow():
            time.sleep(0.02)
        with self.assertRaisesRegex(ValueError, "timed out"):
            transport.export(self.capture, self.root, self.roots, self.output,
                             timeout_seconds=0.001, after_copy=slow)
        self.assertFalse(Path(self.output).exists())

    def test_source_tree_link_is_never_followed(self):
        linked = Path(self.capture) / "chunk-99999999.bin"
        try:
            linked.symlink_to(Path(self.capture) / "index.json")
        except (OSError, NotImplementedError):
            self.skipTest("host cannot create symlinks (NotRun; not a pass)")
        self.addCleanup(lambda: linked.unlink(missing_ok=True))
        with self.assertRaises(ValueError):
            transport.export(self.capture, self.root, self.roots, self.output)
        self.assertFalse(Path(self.output).exists())


def emit_parity_fixture(destination):
    """Test-only synthetic observed Csc capture. Not a real compiler execution receipt."""
    fixture = ClosureTests("test_positive_capture_and_byte_replay")
    fixture.setUp()
    try:
        original = sha(fixture.original)
        capture, expected = fixture.run_capture()
        selectors = [{"TypeName": "Fixture", "MethodName": "AddWord",
                      "MetadataToken": None, "TypeArguments": [], "MethodArguments": []}]
        root = transport.export(capture, expected, selectors, destination)
        if sha(fixture.original) != original:
            raise ValueError("Golden fixture input bytes changed")
        print("SYNTHETIC-ONLY HB->Core transport fixture=" + destination +
              " ClosureSha256=" + root + " GoldenFixtureSha256=" + original)
    finally:
        fixture.doCleanups()
        fixture.tearDown()


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "--emit-parity-fixture":
        emit_parity_fixture(sys.argv[2])
    else:
        unittest.main()
