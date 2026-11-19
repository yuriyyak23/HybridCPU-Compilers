"""Transport-only regression fixtures. These do NOT simulate a real Csc execution."""
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


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def jsonwrite(path, obj):
    Path(path).write_bytes(c.canonical(obj))


class ClosureTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="hb-int-02-fixture-"))
        self.golden = self.tmp / "golden"
        self.copy = self.tmp / "isolated"
        self.generator = self.copy / "generated"
        for d in (self.golden, self.generator):
            d.mkdir(parents=True)
        self.original = self.golden / "A.cs"
        self.source = self.copy / "A.cs"
        self.original.write_bytes(b"#if FEATURE\npublic class A {}\n#endif\n")
        self.source.write_bytes(self.original.read_bytes())
        self.project = self.golden / "P.csproj"
        self.project.write_bytes(b"<Project/>")
        self.reference = self.copy / "ref.dll"
        self.reference.write_bytes(b"metadata-only PE fixture, not real PE")
        self.compiler = self.copy / "csc.dll"
        self.compiler.write_bytes(b"test transport pin only, not csc executable")
        self.generated = self.generator / "Gen.cs"
        self.generated.write_bytes(b"// fixture generator output, NOT runtime execution")
        self.graph_path = self.tmp / "graph.json"
        self.observation_path = self.tmp / "observation.json"
        self.graph = {"schema": c.GRAPH_SCHEMA, "root": str(self.project), "nodes": [{
            "id": "P|Configuration=Debug", "project": str(self.project), "compileFiles": [str(self.original)],
            "globalProperties": {"Configuration": "Debug"}, "projectAssets": None}]}
        self.observation = {"schema": c.OBS_SCHEMA, "graphSha256": "", "projects": [{
            "nodeId": "P|Configuration=Debug", "role": "primary", "workingDirectory": str(self.copy),
            "compiler": {"path": str(self.compiler), "sha256": digest(self.compiler)},
            "projectSha256": digest(self.project),
            "filePins": [{"kind": "reference", "path": str(self.reference), "sha256": digest(self.reference)}],
            "argv": [], "argvSha256": "", "sources": [{"path": str(self.source),
                "logicalPath": "project/A.cs", "sha256": digest(self.source), "origin": "evaluated", "originalPath": str(self.original)}],
            "generatorSources": [{"path": str(self.generated), "logicalPath": "generated/Gen.cs", "sha256": digest(self.generated)}]}]}
        self.observation["projects"][0]["argv"] = [
            "/target:library", "/langversion:preview", "/define:FEATURE", "/nullable:enable",
            "/checked-", "/out:obj/P.dll", "/generatedfilesout:" + str(self.generator),
            "/reference:" + str(self.reference), str(self.source)]
        self.save()

    def tearDown(self):
        shutil.rmtree(self.tmp)

    def save(self):
        jsonwrite(self.graph_path, self.graph)
        self.observation["graphSha256"] = digest(self.graph_path)
        for p in self.observation["projects"]:
            p["argvSha256"] = c.sha(c.canonical(p["argv"]))
        jsonwrite(self.observation_path, self.observation)

    def out(self):
        return str(Path(tempfile.gettempdir()) / ("hb-int-02-result-" + uuid.uuid4().hex))

    def run_capture(self, after_copy=None):
        output = self.out()
        value = c.capture(str(self.graph_path), str(self.observation_path), str(self.golden), output, after_copy=after_copy)
        self.assertEqual(c.verify(output), value)
        self.addCleanup(lambda: shutil.rmtree(output, ignore_errors=True))
        return output, value

    def recompute(self, base, change):
        index_path = Path(base) / "index.json"
        index = json.loads(index_path.read_bytes())
        change(index)
        index["closureRootSha256"] = c.sha(c.canonical({k:v for k,v in index.items() if k != "closureRootSha256"}))
        jsonwrite(index_path, index)

    def test_positive_capture_and_byte_replay(self):
        base, root = self.run_capture()
        index = json.loads((Path(base)/"index.json").read_bytes())
        self.assertEqual(index["disposition"], "unqualified-software-observation")
        self.assertEqual(len(index["commands"]), 1)
        self.assertTrue(any(x["kind"] == "generator-source" for x in index["entries"]))
        self.assertTrue(all(x["role"] == "metadata-only" for x in index["entries"] if x["kind"] == "reference"))
        self.assertEqual(digest(self.original), digest(self.source))
        print("HB-INT-02 fixture-only closureRootSha256=" + root + " indexSha256=" + digest(Path(base) / "index.json"))

    def test_debug_release_and_changed_option(self):
        _, debug = self.run_capture()
        p = self.observation["projects"][0]
        p["argv"][2] = "/define:RELEASE"
        jsonwrite(self.observation_path, self.observation)  # stale argvSha256 must fail
        with self.assertRaisesRegex(ValueError, "changed Csc option"):
            c.capture(str(self.graph_path), str(self.observation_path), str(self.golden), self.out())
        self.save()
        _, release = self.run_capture()
        self.assertNotEqual(debug, release)

    def test_mutated_golden_during_capture(self):
        with self.assertRaisesRegex(ValueError, "Golden source mutated"):
            c.capture(str(self.graph_path), str(self.observation_path), str(self.golden), self.out(),
                      after_copy=lambda: self.original.write_bytes(b"mutated"))

    def test_copied_source_or_reference_mutation(self):
        self.source.write_bytes(b"mutated")
        with self.assertRaisesRegex(ValueError, "changed/stale"):
            c.build_inputs(str(self.graph_path), str(self.observation_path), str(self.golden))
        self.source.write_bytes(self.original.read_bytes())
        self.reference.write_bytes(b"changed reference")
        with self.assertRaisesRegex(ValueError, "changed/stale"):
            c.build_inputs(str(self.graph_path), str(self.observation_path), str(self.golden))

    def test_missing_generator_output(self):
        self.generated.unlink()
        with self.assertRaisesRegex(ValueError, "missing/extra emitted"):
            c.build_inputs(str(self.graph_path), str(self.observation_path), str(self.golden))

    def test_unqualified_metadata_cannot_be_promoted(self):
        base, _ = self.run_capture()
        self.recompute(base, lambda index: next(x for x in index["entries"] if x["kind"] == "reference").update(role="owned-body"))
        with self.assertRaisesRegex(ValueError, "metadata promoted"):
            c.verify(base)

    def test_chunk_missing_reordered_and_traversal(self):
        base, _ = self.run_capture()
        chunk = next(Path(base).glob("chunk-*.bin"))
        chunk.unlink()
        with self.assertRaises(ValueError):
            c.verify(base)
        base2, _ = self.run_capture()
        self.recompute(base2, lambda i: i["entries"][0]["chunks"][0].update(name="chunk-99999999.bin"))
        with self.assertRaisesRegex(ValueError, "chunk"):
            c.verify(base2)
        base3, _ = self.run_capture()
        self.recompute(base3, lambda i: i["entries"][0].update(logicalPath="../escape"))
        with self.assertRaisesRegex(ValueError, "logical path"):
            c.verify(base3)

    def test_duplicate_identity_and_row_reorder(self):
        base, _ = self.run_capture()
        self.recompute(base, lambda i: i["entries"].append(copy.deepcopy(i["entries"][0])))
        with self.assertRaisesRegex(ValueError, "duplicate/reordered"):
            c.verify(base)
        base2, _ = self.run_capture()
        self.recompute(base2, lambda i: i["entries"].reverse())
        with self.assertRaisesRegex(ValueError, "duplicate/reordered"):
            c.verify(base2)

    def test_symlink_source_refused(self):
        real = self.copy / "Real.cs"
        self.source.rename(real)
        try:
            self.source.symlink_to(real)
        except (OSError, NotImplementedError):
            self.skipTest("symlinks unavailable")
        with self.assertRaisesRegex(ValueError, "symlink"):
            c.build_inputs(str(self.graph_path), str(self.observation_path), str(self.golden))

    def test_wrong_compiler_pin(self):
        self.observation["projects"][0]["compiler"]["sha256"] = "0" * 64
        self.save()
        with self.assertRaisesRegex(ValueError, "changed/stale"):
            c.build_inputs(str(self.graph_path), str(self.observation_path), str(self.golden))

    def test_timeout_and_keyboard_cancellation_remove_partial_output(self):
        deadline_output = self.out()
        with self.assertRaisesRegex(ValueError, "capture timeout"):
            c.capture(str(self.graph_path), str(self.observation_path), str(self.golden),
                      deadline_output, after_copy=lambda: time.sleep(0.025), timeout_seconds=0.001)
        self.assertFalse(Path(deadline_output).exists())
        cancel_output = self.out()
        def interrupt():
            raise KeyboardInterrupt()
        with self.assertRaises(KeyboardInterrupt):
            c.capture(str(self.graph_path), str(self.observation_path), str(self.golden),
                      cancel_output, after_copy=interrupt)
        self.assertFalse(Path(cancel_output).exists())

    def test_1001_sources_bounded_index(self):
        p = self.observation["projects"][0]
        for index in range(1000):
            original = self.golden / ("F%04d.cs" % index)
            source = self.copy / original.name
            original.write_bytes(("// file %d\n" % index).encode())
            source.write_bytes(original.read_bytes())
            self.graph["nodes"][0]["compileFiles"].append(str(original))
            p["argv"].append(str(source))
            p["sources"].append({"path": str(source), "logicalPath": "project/"+source.name,
                                 "origin": "evaluated", "originalPath": str(original), "sha256": digest(source)})
        self.save()
        base, _ = self.run_capture()
        index = json.loads((Path(base)/"index.json").read_bytes())
        self.assertEqual(len([r for r in index["entries"] if r["kind"] == "csc-source"]), 1001)
        self.assertLess((Path(base)/"index.json").stat().st_size, c.MAX_INDEX)


if __name__ == "__main__":
    unittest.main()
