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
        self.pin = json.loads((Path(self.bundle) / "index.json").read_bytes())["closureRootSha256"]
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

    def test_recomputed_noncanonical_jsonl_is_rejected(self):
        self.run_stage()
        index_path = self.target / "bridge-index.json"
        index = json.loads(index_path.read_bytes())
        chunk_path = self.target / index["Chunks"][0]["Path"]
        original = chunk_path.read_bytes().splitlines(keepends=True)
        row = json.loads(original[0])
        original[0] = json.dumps(row, separators=(", ", ": "), ensure_ascii=True).encode() + b"\n"
        chunk_path.write_bytes(b"".join(original))
        index["Chunks"][0]["Bytes"] = chunk_path.stat().st_size
        index["Chunks"][0]["Sha256"] = stage.sha(chunk_path.read_bytes())
        full = b"".join((self.target / d["Path"]).read_bytes() for d in index["Chunks"])
        index["ClosureSha256"] = stage.sha(full)
        index_path.write_bytes(stage.json_bytes(index))
        mapping_path = self.target / "capture-map.json"
        mapping = json.loads(mapping_path.read_bytes())
        mapping["CoreClosureSha256"] = index["ClosureSha256"]
        mapping_path.write_bytes(stage.json_bytes(mapping))
        with self.assertRaisesRegex(ValueError, "noncanonical Core JSONL"):
            stage.verify(self.target, self.pin, self.roots_sha)

    def test_invalid_utf8_source_is_not_promoted_to_core(self):
        self.fixture.original.write_bytes(b"\xff\n")
        self.fixture.source.write_bytes(b"\xff\n")
        self.fixture.observation["projects"][0]["sources"][0]["sha256"] = c.sha(b"\xff\n")
        self.fixture.save()
        self.bundle, self.pin = self.fixture.run_capture()
        self.addCleanup(lambda: shutil.rmtree(self.bundle, ignore_errors=True))
        with self.assertRaisesRegex(ValueError, "Core source must be UTF-8"):
            self.run_stage()
        self.assertFalse(self.target.exists())

    def test_too_many_core_rows_are_blocked(self):
        doc = json.loads((Path(self.bundle) / "index.json").read_bytes())
        doc["entries"] = [copy.deepcopy(doc["entries"][0]) for _ in range(stage.MAX_ROWS + 1)]
        doc["closureRootSha256"] = c.sha(c.canonical({k:v for k,v in doc.items()
                                                    if k != "closureRootSha256"}))
        (Path(self.bundle)/"index.json").write_bytes(c.canonical(doc))
        self.pin = doc["closureRootSha256"]
        with self.assertRaises(ValueError):
            self.run_stage()

    def test_release_stager_1_64_65_and_1001_actual_captured_sources(self):
        # The HB02 capture includes mandatory compiler/reference/project metadata
        # in addition to the counted Csc source rows. No real Csc is invoked.
        self.fixture.generated.unlink()
        self.fixture.observation["projects"][0]["generatorSources"] = []
        observed = 1
        for count in (1, 64, 65, 1001):
            while observed < count:
                i = observed
                original = self.fixture.golden / f"R{i:04d}.cs"
                copied = self.fixture.copy / original.name
                original.write_bytes(f"// exact captured source {i}\\n".encode())
                copied.write_bytes(original.read_bytes())
                self.fixture.graph["nodes"][0]["compileFiles"].append(str(original))
                self.fixture.observation["projects"][0]["argv"].append(str(copied))
                self.fixture.observation["projects"][0]["sources"].append({
                    "path": str(copied), "logicalPath": "project/" + copied.name,
                    "sha256": c.sha(copied.read_bytes()), "origin": "evaluated",
                    "originalPath": str(original)})
                observed += 1
            self.fixture.save()
            captured, pin = self.fixture.run_capture()
            target = Path(tempfile.gettempdir()) / ("hb-core-boundary-" + uuid.uuid4().hex)
            self.addCleanup(lambda x=target: shutil.rmtree(x, ignore_errors=True))
            digest = stage.stage(captured, pin, self.roots, self.roots_sha, target)
            self.assertEqual(stage.verify(target, pin, self.roots_sha), digest)
            mapping = json.loads((target / "capture-map.json").read_bytes())
            self.assertEqual(sum(x["Kind"] == "csc-source" for x in mapping["Rows"]), count)
            self.assertTrue(all(x["SourceRole"] == "primary"
                                for x in mapping["Rows"] if x["Kind"] == "csc-source"))
            index = json.loads((target / "bridge-index.json").read_bytes())
            self.assertEqual(sum(x["Rows"] for x in index["Chunks"]), len(mapping["Rows"]))
            self.assertEqual(len(index["Chunks"]), (len(mapping["Rows"]) + 63) // 64)
            print(f"RELEASE SYNTHETIC count={count} indexSHA={stage.sha((target / 'bridge-index.json').read_bytes())} closure={digest}")

    def test_release_stager_exact_2mib_source_and_overflow(self):
        limit = 2097152
        for size in (limit, limit + 1):
            payload = b"x" * size
            self.fixture.original.write_bytes(payload)
            self.fixture.source.write_bytes(payload)
            self.fixture.observation["projects"][0]["sources"][0]["sha256"] = c.sha(payload)
            self.fixture.save()
            captured, pin = self.fixture.run_capture()
            output = Path(tempfile.gettempdir()) / ("hb-source-limit-" + uuid.uuid4().hex)
            self.addCleanup(lambda x=output: shutil.rmtree(x, ignore_errors=True))
            if size == limit:
                digest = stage.stage(captured, pin, self.roots, self.roots_sha, output)
                self.assertEqual(stage.verify(output, pin, self.roots_sha), digest)
            else:
                with self.assertRaisesRegex(ValueError, "per-file byte budget"):
                    stage.stage(captured, pin, self.roots, self.roots_sha, output)
                self.assertFalse(output.exists())

    def test_release_stager_32mib_metadata_and_overflow(self):
        limit = 33554432
        for size in (limit, limit + 1):
            payload = b"m" * size
            self.fixture.reference.write_bytes(payload)
            self.fixture.observation["projects"][0]["filePins"][0]["sha256"] = c.sha(payload)
            self.fixture.observation["projects"][0]["argv"] = [
                x for x in self.fixture.observation["projects"][0]["argv"]]
            self.fixture.save()
            captured, pin = self.fixture.run_capture()
            output = Path(tempfile.gettempdir()) / ("hb-metadata-limit-" + uuid.uuid4().hex)
            self.addCleanup(lambda x=output: shutil.rmtree(x, ignore_errors=True))
            if size == limit:
                digest = stage.stage(captured, pin, self.roots, self.roots_sha, output)
                self.assertEqual(stage.verify(output, pin, self.roots_sha), digest)
            else:
                with self.assertRaisesRegex(ValueError, "per-file byte budget"):
                    stage.stage(captured, pin, self.roots, self.roots_sha, output)
                self.assertFalse(output.exists())

    def test_release_rejects_257_chunk_descriptors_and_case_alias(self):
        self.run_stage()
        index_path = self.target / "bridge-index.json"
        index = json.loads(index_path.read_bytes())
        index["Chunks"] = [copy.deepcopy(index["Chunks"][0]) for _ in range(257)]
        index_path.write_bytes(stage.json_bytes(index))
        with self.assertRaisesRegex(ValueError, "invalid chunk list"):
            stage.verify(self.target, self.pin, self.roots_sha)
        shutil.rmtree(self.target)
        self.run_stage()
        if sys.platform == "win32":
            return  # Windows aliases INPUTS to inputs; no distinct case-collision path
        alias = self.target / "INPUTS" / "00000.bin"
        alias.parent.mkdir()
        alias.write_bytes(b"alias")
        with self.assertRaisesRegex(ValueError, "extra/missing"):
            stage.verify(self.target, self.pin, self.roots_sha)

    def test_release_refuses_rehashed_ownership_change(self):
        self.run_stage()
        map_path = self.target / "capture-map.json"
        mapping = json.loads(map_path.read_bytes())
        original = next(x for x in mapping["Rows"] if x["SourceRole"] == "primary")
        original["SourceRole"] = "metadata-only"
        map_path.write_bytes(stage.json_bytes(mapping))
        with self.assertRaises(ValueError):
            stage.verify(self.target, self.pin, self.roots_sha)


    def test_r2_atomic_output_is_invisible_until_complete(self):
        observed = []
        def during_copy():
            observed.append(self.target.exists() or self.target.is_symlink())
        self.run_stage(after_copy=during_copy)
        self.assertEqual(observed, [False])
        self.assertTrue((self.target / "bridge-index.json").is_file())
        self.assertEqual(stage.verify(self.target, self.pin, self.roots_sha, self.bundle),
                         json.loads((self.target / "bridge-index.json").read_bytes())["ClosureSha256"])

    def test_r2_concurrent_destination_creation_never_overwrites(self):
        def race():
            self.target.mkdir()
            (self.target / "must-stay.txt").write_text("unrelated", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "destination created"):
            self.run_stage(after_copy=race)
        self.assertEqual((self.target / "must-stay.txt").read_text(encoding="utf-8"), "unrelated")
        self.assertFalse((self.target / "bridge-index.json").exists())

    def test_r2_approved_root_mutated_after_copy_refused(self):
        original = self.roots.read_bytes()
        def mutate_roots():
            self.roots.write_bytes(original + b" ")
        with self.assertRaisesRegex(ValueError, "reviewed root manifest SHA"):
            self.run_stage(after_copy=mutate_roots)
        self.assertFalse(self.target.exists())

    def test_r2_capture_mutated_after_copy_refused_and_rollback(self):
        chunk = next(Path(self.bundle).glob("chunk-*.bin"))
        old = chunk.read_bytes()
        def mutate_capture():
            chunk.write_bytes(b"forged")
        with self.assertRaises(ValueError):
            self.run_stage(after_copy=mutate_capture)
        self.assertFalse(self.target.exists())
        self.assertNotEqual(chunk.read_bytes(), old)

    def test_r2_recomputed_noncanonical_chunk_partition_rejected(self):
        self.run_stage()
        index_path = self.target / "bridge-index.json"
        index = json.loads(index_path.read_bytes())
        first = self.target / index["Chunks"][0]["Path"]
        rows = first.read_bytes().splitlines(keepends=True)
        self.assertGreater(len(rows), 1)
        first.write_bytes(rows[0])
        second = self.target / "bridge-index/chunk-0001.jsonl"
        second.write_bytes(b"".join(rows[1:]))
        index["Chunks"] = [
            {"Path": "bridge-index/chunk-0000.jsonl", "Sha256": stage.sha(rows[0]),
             "Bytes": len(rows[0]), "Rows": 1},
            {"Path": "bridge-index/chunk-0001.jsonl", "Sha256": stage.sha(second.read_bytes()),
             "Bytes": second.stat().st_size, "Rows": len(rows) - 1}]
        index_path.write_bytes(stage.json_bytes(index))
        with self.assertRaisesRegex(ValueError, "noncanonical 64-row"):
            stage.verify(self.target, self.pin, self.roots_sha)

    def test_r2_crosswalk_requires_exact_reviewed_inventory_when_supplied(self):
        self.run_stage()
        path = self.target / "capture-map.json"
        m = json.loads(path.read_bytes())
        m["Rows"][0]["LogicalPath"] += "-forged"
        path.write_bytes(stage.json_bytes(m))
        # Producer map is not Core authority; optional reviewed capture replay catches it.
        with self.assertRaisesRegex(ValueError, "capture-map differs"):
            stage.verify(self.target, self.pin, self.roots_sha, self.bundle)

    def test_r2_duplicate_mapping_json_refused(self):
        self.run_stage()
        p = self.target / "capture-map.json"
        raw = p.read_bytes()
        self.assertIn(b'"Schema":', raw)
        p.write_bytes(raw.replace(b'"Schema":', b'"Schema":"forged","Schema":', 1))
        with self.assertRaisesRegex(ValueError, "duplicate JSON property"):
            stage.verify(self.target, self.pin, self.roots_sha)

    def test_r2_fixture_no_write_inventory(self):
        before = {p.relative_to(self.fixture.golden).as_posix(): c.sha(p.read_bytes())
                  for p in self.fixture.golden.rglob("*") if p.is_file()}
        self.run_stage()
        after = {p.relative_to(self.fixture.golden).as_posix(): c.sha(p.read_bytes())
                 for p in self.fixture.golden.rglob("*") if p.is_file()}
        self.assertEqual(before, after, "synthetic fixture source changed")
        self.assertEqual(stage.verify(self.target, self.pin, self.roots_sha, self.bundle),
                         json.loads((self.target / "bridge-index.json").read_bytes())["ClosureSha256"])

    def test_r2_immutable_three_role_cross_language_bytes(self):
        # Standalone byte contract; owned synthetic data, never Golden or Core proof.
        fixture = json.loads((Path(__file__).parent / "r2_01_wire_contract.json").read_bytes())
        self.assertEqual(fixture["FixtureSchema"], "hybridcpu.hardwarebridge.r2-01-byte-fixture/v1")
        rows = []
        for item in fixture["Inputs"]:
            raw = bytes.fromhex(item["Hex"])
            self.assertEqual(len(raw), item["Bytes"])
            self.assertEqual(stage.sha(raw), item["Sha256"])
            rows.append({"Path": item["Path"], "Role": item["Role"],
                         "Sha256": item["Sha256"], "Bytes": item["Bytes"]})
        chunk = b"".join(stage.json_bytes(x) + b"\\n" for x in rows)
        self.assertEqual(stage.sha(chunk), fixture["ExpectedChunkSha256"])
        index = stage.json_bytes({"Schema": stage.CORE, "Roots": fixture["Roots"],
            "Chunks": [{"Path": "bridge-index/chunk-0000.jsonl",
                         "Sha256": stage.sha(chunk), "Bytes": len(chunk), "Rows": len(rows)}],
            "ClosureSha256": stage.sha(chunk)})
        self.assertEqual(stage.sha(index), fixture["ExpectedIndexSha256"])
        self.assertEqual(len(index), fixture["ExpectedIndexBytes"])
        self.assertEqual(len(chunk), fixture["ExpectedChunkBytes"])

if __name__ == "__main__":
    unittest.main(verbosity=2)
