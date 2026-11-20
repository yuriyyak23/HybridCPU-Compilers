"""HB-INT-02 -> CW-INT-03 wire projection tests. Synthetic bytes are NOT Csc receipts."""
import json
from pathlib import Path
import shutil
import tempfile
import unittest
import sys

sys.path.insert(0, str(Path(__file__).parent))
import project_csc_to_core_v2 as p
import segmented_csc_capture_v2 as hb02
import test_segmented_csc_capture_v2 as fixture


class ProjectorTests(unittest.TestCase):
    def setUp(self):
        self.f = fixture.ClosureTests("test_positive_capture_and_byte_replay")
        self.f.setUp()
        self.capture, self.src_root = self.f.run_capture()
        self.roots = self.f.tmp / "roots.json"
        self.roots.write_bytes(p.encode([{"TypeName": "Fixture", "MethodName": "F",
                                          "MetadataToken": None, "TypeArguments": [],
                                          "MethodArguments": []}]))

    def tearDown(self):
        self.f.doCleanups()
        self.f.tearDown()

    def project(self, after_stage=None):
        output = self.f.out()
        root = p.project(self.capture, str(self.roots), output, after_stage=after_stage)
        self.addCleanup(lambda: shutil.rmtree(output, ignore_errors=True))
        self.assertEqual(p.verify_projection(output, self.capture), root)
        return Path(output), root

    def test_positive_replay_and_core_wire_shapes(self):
        output, root = self.project()
        index_bytes = (output / "index.json").read_bytes()
        index = json.loads(index_bytes)
        self.assertEqual(list(index), ["Schema", "Roots", "Chunks", "ClosureSha256"])
        self.assertEqual(index["Schema"], "circuitweave.bridge-source-closure-index/v2")
        self.assertEqual(root, index["ClosureSha256"])
        self.assertEqual(len(index["Chunks"]), 1)
        rows = (output / index["Chunks"][0]["Path"]).read_bytes().splitlines()
        self.assertEqual(len(rows), len(json.loads((output / "capture-crosswalk.json").read_bytes())["Inputs"]))
        self.assertTrue(all(list(json.loads(r)) == ["Path", "Role", "Sha256", "Bytes"] for r in rows))
        self.assertTrue(all(x["Role"] in ("source", "metadata-only") for x in map(json.loads, rows)))
        print("HB-INT-02/03 fixture-only Core closure=" + root +
              " coreIndexSha256=" + p.sha(index_bytes))

    def test_staged_bytes_mutated_during_projection(self):
        target = self.f.out()
        with self.assertRaisesRegex(ValueError, "staged output mutated"):
            p.project(self.capture, str(self.roots), target,
                      after_stage=lambda out: (out / "src/00002.cs").write_bytes(b"altered"))
        self.assertFalse(Path(target).exists())

    def test_stale_input_and_corrupted_chunk(self):
        Path(self.capture, "chunk-00000000.bin").write_bytes(b"broken")
        with self.assertRaises(ValueError):
            p.project(self.capture, str(self.roots), self.f.out())

    def test_invalid_roots_and_path(self):
        self.roots.write_bytes(p.encode([{"TypeName": "../forged", "MethodName": "Tick",
                                          "MetadataToken": None, "TypeArguments": [],
                                          "MethodArguments": []}]))
        with self.assertRaisesRegex(ValueError, "root identity"):
            p.project(self.capture, str(self.roots), self.f.out())

    def test_tampered_core_chunk_and_crosswalk(self):
        output, root = self.project()
        index = json.loads((output / "index.json").read_bytes())
        (output / index["Chunks"][0]["Path"]).write_bytes(b"tampered")
        with self.assertRaises(ValueError):
            p.verify_projection(output, self.capture)

    def test_deprecated_projection_refuses_owned_body_instead_of_promoting(self):
        # Legacy format used to encode owned-body as "source". A self-consistent
        # HB02 index must now fail explicitly rather than silently upgrading role.
        def change(index):
            index["commands"][0]["projectRole"] = "owned-body"
            for row in index["entries"]:
                if row["kind"] in p.SOURCE_KINDS:
                    row["role"] = "owned-body"
        self.f.recompute(self.capture, change)
        with self.assertRaisesRegex(ValueError, "DEPRECATED legacy projection refuses non-primary"):
            p.project(self.capture, str(self.roots), self.f.out())

    def test_deprecated_projection_refuses_metadata_only_source_kind(self):
        # The old writer also encoded source-kind + metadata-only as Core source.
        # Strictly preserve data-only role instead of promoting it by kind name.
        def change(index):
            index["commands"][0]["projectRole"] = "metadata-only"
            for row in index["entries"]:
                if row["kind"] in p.SOURCE_KINDS:
                    row["role"] = "metadata-only"
        self.f.recompute(self.capture, change)
        with self.assertRaisesRegex(ValueError, "DEPRECATED legacy projection refuses non-primary"):
            p.project(self.capture, str(self.roots), self.f.out())

    def test_producer_blocks_unqualified_body_promotion(self):
        output, root = self.project()
        link = output / "capture-crosswalk.json"
        cross = json.loads(link.read_bytes())
        self.assertTrue(any(x["Kind"] == "reference" and x["ClaimedProjectRole"] == "metadata-only"
                            for x in cross["Inputs"]))

    def test_metadata_crosswalk_role_promotion_is_refused(self):
        output, _ = self.project()
        cross_path = output / "capture-crosswalk.json"
        cross = json.loads(cross_path.read_bytes())
        ref = next(x for x in cross["Inputs"] if x["Kind"] == "reference")
        ref["ClaimedProjectRole"] = "owned-body"
        cross_path.write_bytes(p.encode(cross))
        with self.assertRaisesRegex(ValueError, "metadata to executable"):
            p.verify_projection(output, self.capture)

    def test_traversal_with_recomputed_index_and_crosswalk_root_is_refused(self):
        output, _ = self.project()
        index_path = output / "index.json"
        index = json.loads(index_path.read_bytes())
        chunk = output / index["Chunks"][0]["Path"]
        rows = [json.loads(line) for line in chunk.read_bytes().splitlines()]
        source = next(x for x in rows if x["Role"] == "source")
        source["Path"] = "src/../../stolen.cs"
        raw = b"".join(p.encode(row) + b"\n" for row in rows)
        chunk.write_bytes(raw)
        index["Chunks"][0]["Sha256"] = p.sha(raw)
        index["Chunks"][0]["Bytes"] = len(raw)
        index["ClosureSha256"] = p.sha(raw)
        index_path.write_bytes(p.encode(index))
        cross_path = output / "capture-crosswalk.json"
        cross = json.loads(cross_path.read_bytes())
        cross["CoreIndexSha256"] = p.sha(index_path.read_bytes())
        cross["CoreClosureSha256"] = index["ClosureSha256"]
        cross_path.write_bytes(p.encode(cross))
        with self.assertRaisesRegex(ValueError, "traversing"):
            p.verify_projection(output, self.capture)

    def test_symlinked_staged_source_is_refused(self):
        output, _ = self.project()
        source = next((output / "src").glob("*.cs"))
        original = output / "replacement.txt"
        source.rename(original)
        try:
            source.symlink_to(original)
        except (OSError, NotImplementedError):
            self.skipTest("symlinks unsupported on CI platform")
        with self.assertRaisesRegex(ValueError, "symlink"):
            p.verify_projection(output, self.capture)

    def test_duplicate_property_roots(self):
        self.roots.write_text('[{"TypeName":"Fixture","TypeName":"Forged","MethodName":"F",'
                              '"MetadataToken":null,"TypeArguments":[],"MethodArguments":[]}]')
        with self.assertRaisesRegex(ValueError, "duplicate JSON property"):
            p.project(self.capture, str(self.roots), self.f.out())

    def test_core_budget_refusal_source_over_2mib(self):
        huge = b"a" * (p.MAX_SOURCE + 1)
        self.f.original.write_bytes(huge)
        self.f.source.write_bytes(huge)
        self.f.observation["projects"][0]["sources"][0]["sha256"] = hb02.sha(huge)
        self.f.save()
        new_capture, _ = self.f.run_capture()
        with self.assertRaisesRegex(ValueError, "Core source per-file budget"):
            p.project(new_capture, str(self.roots), self.f.out())

    def test_1001_source_rows_core_contract(self):
        # Exercise actual HB-INT-02 capture; no compiler process launched.
        pr = self.f.observation["projects"][0]
        for index in range(1000):
            original = self.f.golden / f"B{index:04d}.cs"
            isolated = self.f.copy / original.name
            original.write_bytes(f"// source {index}\n".encode())
            isolated.write_bytes(original.read_bytes())
            self.f.graph["nodes"][0]["compileFiles"].append(str(original))
            pr["argv"].append(str(isolated))
            pr["sources"].append({"path": str(isolated), "logicalPath": "source/" + original.name,
                                  "sha256": hb02.sha(isolated.read_bytes()),
                                  "origin": "evaluated", "originalPath": str(original)})
        self.f.save()
        captured, _ = self.f.run_capture()
        output = self.f.out()
        root = p.project(captured, str(self.roots), output)
        self.addCleanup(lambda: shutil.rmtree(output, ignore_errors=True))
        index = json.loads((Path(output) / "index.json").read_bytes())
        self.assertEqual(p.verify_projection(output, captured), root)
        count = sum(x["Rows"] for x in index["Chunks"])
        self.assertEqual(count, len(json.loads((Path(output) / "capture-crosswalk.json").read_bytes())["Inputs"]))
        self.assertGreaterEqual(count, 1001)



    def test_deprecated_unreviewed_root_manifest_cannot_masquerade_as_list(self):
        self.roots.write_bytes(p.encode({"Schema": "hybridcpu.hardwarebridge.root-selectors/v1",
                                        "Roots": [{"TypeName": "Fixture", "MethodName": "F",
                                                   "MetadataToken": None, "TypeArguments": [],
                                                   "MethodArguments": []}]}))
        with self.assertRaisesRegex(ValueError, "explicitly selected roots"):
            p.project(self.capture, str(self.roots), self.f.out())

    def test_deprecated_ambiguous_untokened_root_overload_refused(self):
        roots = [{"TypeName": "Fixture", "MethodName": "F", "MetadataToken": None,
                  "TypeArguments": [], "MethodArguments": []},
                 {"TypeName": "Fixture", "MethodName": "F", "MetadataToken": 42,
                  "TypeArguments": [], "MethodArguments": []}]
        self.roots.write_bytes(p.encode(roots))
        with self.assertRaisesRegex(ValueError, "ambiguous untokened overload"):
            p.project(self.capture, str(self.roots), self.f.out())

    def test_deprecated_malformed_root_identifier_refused(self):
        self.roots.write_bytes(p.encode([{"TypeName": "9Invalid", "MethodName": "F",
                                          "MetadataToken": None, "TypeArguments": [],
                                          "MethodArguments": []}]))
        with self.assertRaisesRegex(ValueError, "root identity"):
            p.project(self.capture, str(self.roots), self.f.out())

if __name__ == "__main__":
    unittest.main()
