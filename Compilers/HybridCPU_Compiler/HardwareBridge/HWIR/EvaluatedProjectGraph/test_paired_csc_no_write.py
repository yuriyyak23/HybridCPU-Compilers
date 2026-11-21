"""Synthetic-only mutation/scale tests for paired receipt; no Golden or Csc execution."""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
import uuid

sys.path.insert(0, str(Path(__file__).parent))
import segmented_csc_capture_v2 as hb
import paired_csc_no_write as paired
from test_segmented_csc_capture_v2 import ClosureTests


class PairTests(unittest.TestCase):
    def setUp(self):
        self.clean = ClosureTests()
        self.dirty = ClosureTests()
        self.clean.setUp()
        self.dirty.setUp()
        self.outputs = []

    def tearDown(self):
        self.clean.tearDown()
        self.dirty.tearDown()
        for output in self.outputs:
            shutil.rmtree(output, ignore_errors=True)

    def modify_dirty(self):
        body = self.dirty.original.read_bytes() + b"// modified overlay\n"
        self.dirty.original.write_bytes(body)
        self.dirty.source.write_bytes(body)
        source = self.dirty.observation["projects"][0]["sources"][0]
        source["sha256"] = hb.sha(body)
        self.dirty.save()

    def outputs3(self):
        paths = [str(Path(tempfile.gettempdir()) / ("hb02-pair-" + uuid.uuid4().hex)) for _ in range(3)]
        self.outputs.extend(paths)
        return paths

    def run_pair(self, **overrides):
        outs = self.outputs3()
        params = dict(clean_root=str(self.clean.golden), dirty_root=str(self.dirty.golden),
                      clean_graph=str(self.clean.graph_path), dirty_graph=str(self.dirty.graph_path),
                      clean_observation=str(self.clean.observation_path),
                      dirty_observation=str(self.dirty.observation_path),
                      clean_output=outs[0], dirty_output=outs[1], receipt_output=outs[2])
        params.update(overrides)
        return paired.pair(**params), outs

    def assert_refused(self, needle, **overrides):
        with self.assertRaisesRegex(ValueError, needle):
            self.run_pair(**overrides)

    def test_distinct_clean_dirty_and_chunk_replay(self):
        self.modify_dirty()
        receipt, outs = self.run_pair()
        self.assertEqual(receipt["schema"], paired.SCHEMA)
        self.assertNotEqual(receipt["clean"]["inventorySha256"], receipt["dirty"]["inventorySha256"])
        self.assertNotEqual(receipt["clean"]["closureRootSha256"], receipt["dirty"]["closureRootSha256"])
        self.assertEqual(receipt["clean"]["gitCommit"], None)  # synthetic has no git
        self.assertIn("Blocked", receipt["executionGate"])
        self.assertEqual(hb.verify(outs[0]), receipt["clean"]["closureRootSha256"])
        self.assertEqual(hb.verify(outs[1]), receipt["dirty"]["closureRootSha256"])
        self.assertEqual(json.loads((Path(outs[2]) / "receipt.json").read_bytes())["clean"]["chunkCount"],
                         receipt["clean"]["chunkCount"])
        index = json.loads((Path(outs[0]) / "index.json").read_bytes())
        index["entries"][0]["chunks"][0]["name"] = "chunk-88888888.bin"
        index["closureRootSha256"] = hb.sha(hb.canonical({k:v for k,v in index.items() if k != "closureRootSha256"}))
        (Path(outs[0]) / "index.json").write_bytes(hb.canonical(index))
        with self.assertRaisesRegex(ValueError, "chunk"):
            hb.verify(outs[0])

    def test_identical_bytes_are_not_a_dirty_overlay(self):
        self.assert_refused("inventories identical")

    def test_changed_reference_is_blocked(self):
        self.modify_dirty()
        self.dirty.reference.write_bytes(b"changed ref after observation")
        self.assert_refused("changed/stale")

    def test_missing_generator_is_blocked(self):
        self.modify_dirty()
        self.dirty.generated.unlink()
        self.assert_refused("missing/extra emitted")

    def test_unknown_role_and_original_path(self):
        self.modify_dirty()
        self.dirty.observation["projects"][0]["role"] = "Verified"
        self.dirty.save()
        self.assert_refused("role")
        self.dirty.observation["projects"][0]["role"] = "primary"
        self.dirty.observation["projects"][0]["sources"][0]["originalPath"] = str(self.clean.original)
        self.dirty.save()
        self.assert_refused("unexpected/duplicate evaluated source")

    def test_symlink_in_golden_snapshot_is_blocked(self):
        self.modify_dirty()
        link = self.clean.golden / "linked.cs"
        try:
            link.symlink_to(self.clean.original)
        except (OSError, NotImplementedError):
            self.skipTest("symlinks unavailable")
        self.assert_refused("symlink/reparse")

    def test_source_drift_and_cancel_cleanup(self):
        self.modify_dirty()
        outs = self.outputs3()
        with self.assertRaisesRegex(ValueError, "source drift"):
            paired.pair(clean_root=str(self.clean.golden), dirty_root=str(self.dirty.golden),
                        clean_graph=str(self.clean.graph_path), dirty_graph=str(self.dirty.graph_path),
                        clean_observation=str(self.clean.observation_path),
                        dirty_observation=str(self.dirty.observation_path),
                        clean_output=outs[0], dirty_output=outs[1], receipt_output=outs[2],
                        after_publish=lambda: self.clean.original.write_bytes(b"concurrent drift"))
        self.assertFalse(any(Path(p).exists() for p in outs))
        self.clean.original.write_bytes(self.clean.source.read_bytes())
        outs2 = self.outputs3()
        def cancelled():
            raise KeyboardInterrupt()
        with self.assertRaises(KeyboardInterrupt):
            paired.pair(clean_root=str(self.clean.golden), dirty_root=str(self.dirty.golden),
                        clean_graph=str(self.clean.graph_path), dirty_graph=str(self.dirty.graph_path),
                        clean_observation=str(self.clean.observation_path),
                        dirty_observation=str(self.dirty.observation_path),
                        clean_output=outs2[0], dirty_output=outs2[1], receipt_output=outs2[2],
                        after_clean=cancelled)
        self.assertFalse(any(Path(p).exists() for p in outs2))

    def test_core_8192_limit_is_refusal_not_truncation(self):
        self.modify_dirty()
        with mock.patch.object(paired, "CORE_MAX_ROWS", 1):
            self.assert_refused("Core transition exceeds")

    def test_r1_02_empty_directory_fanout_is_bounded(self):
        self.modify_dirty()
        (self.clean.golden / "EmptyDirectoryOnly").mkdir()
        with mock.patch.object(paired, "INVENTORY_MAX_DIRS", 1):
            self.assert_refused("bounded directory budget")

    def test_r1_02_inventory_deadline_is_fail_closed(self):
        self.modify_dirty()
        with mock.patch.object(paired, "INVENTORY_MAX_SECONDS", -1):
            self.assert_refused("inventory time budget exceeded")

    def test_8gib_source_inventory_budget_is_typed_refusal(self):
        self.modify_dirty()
        # Simulate a tiny ceiling rather than allocating 8 GiB in CI.
        with mock.patch.object(hb, "MAX_BYTES", 1):
            self.assert_refused("snapshot exceeds bounded")

    def test_duplicate_logical_source_identity_is_refused(self):
        self.modify_dirty()
        extra = self.dirty.copy / "Extra.cs"
        extra.write_bytes(b"// extra msbuild-generated synthetic source")
        p = self.dirty.observation["projects"][0]
        p["argv"].append(str(extra))
        p["sources"].append({"path": str(extra), "logicalPath": "project/A.cs",
                             "origin": "msbuild-generated", "sha256": hb.sha(extra.read_bytes())})
        self.dirty.save()
        self.assert_refused("duplicate ordered closure identity")

    def test_1001_sources_per_snapshot(self):
        self.modify_dirty()
        for fx in (self.clean, self.dirty):
            project = fx.observation["projects"][0]
            for index in range(1000):
                original = fx.golden / ("F%04d.cs" % index)
                source = fx.copy / original.name
                original.write_bytes(("// source %d\n" % index).encode())
                source.write_bytes(original.read_bytes())
                fx.graph["nodes"][0]["compileFiles"].append(str(original))
                project["argv"].append(str(source))
                project["sources"].append({"path": str(source), "logicalPath": "project/" + source.name,
                                           "origin": "evaluated", "originalPath": str(original),
                                           "sha256": hb.sha(source.read_bytes())})
            fx.save()
        receipt, _ = self.run_pair()
        for label in ("clean", "dirty"):
            self.assertEqual(receipt[label]["entryCount"], 1005)  # 1001 sources + 4 metadata/generator
            self.assertEqual(receipt[label]["fileCount"], 1002)
        print("Synthetic 1001 source files/snapshot, not executed Csc.")

    @unittest.skipUnless(shutil.which("git"), "git binary unavailable")
    def test_readonly_git_status_and_same_commit_modified_overlay(self):
        def cmd(root, *args):
            proc = subprocess.run(["git", "-C", str(root), *args], capture_output=True, check=True)
            return proc.stdout.decode().strip()
        cmd(self.clean.golden, "init", "-q")
        cmd(self.clean.golden, "-c", "user.name=Synthetic Test", "-c", "user.email=test@example.invalid",
            "add", "--all")
        cmd(self.clean.golden, "-c", "user.name=Synthetic Test", "-c", "user.email=test@example.invalid",
            "commit", "-qm", "synthetic snapshot")
        shutil.copytree(self.clean.golden / ".git", self.dirty.golden / ".git")
        # Keep the actual clean snapshot commit identical, but mutate dirty bytes.
        self.modify_dirty()
        commit = cmd(self.clean.golden, "rev-parse", "HEAD")
        receipt, _ = self.run_pair(clean_commit=commit, dirty_commit=commit, require_git=True)
        self.assertEqual(receipt["clean"]["gitCommit"], commit)
        self.assertEqual(receipt["dirty"]["gitCommit"], commit)
        self.assertNotEqual(receipt["clean"]["gitStatusSha256"], receipt["dirty"]["gitStatusSha256"])
        self.assertEqual(cmd(self.clean.golden, "status", "--porcelain=v1"),
                         "")
        self.assertNotEqual(cmd(self.dirty.golden, "status", "--porcelain=v1"), "")

    def test_external_witness_structural_gate_synthetic_only(self):
        project = self.clean.observation["projects"][0]
        # These local bytes intentionally do NOT represent a real Csc event.
        event_file = self.clean.tmp / "synthetic-event.json"
        event_file.write_bytes(b'{"synthetic":true,"executedCsc":false}\\n')
        stdout = self.clean.tmp / "synthetic-stdout.bin"
        stderr = self.clean.tmp / "synthetic-stderr.bin"
        stdout.write_bytes(b"")
        stderr.write_bytes(b"")
        event = {"nodeId": project["nodeId"], "argvSha256": project["argvSha256"],
                 "compilerSha256": project["compiler"]["sha256"],
                 "workingDirectory": project["workingDirectory"],
                 "eventSha256": hb.hash_file(event_file)[0],
                 "stdoutSha256": hb.hash_file(stdout)[0],
                 "stderrSha256": hb.hash_file(stderr)[0],
                 "eventFile": str(event_file), "stdoutFile": str(stdout),
                 "stderrFile": str(stderr), "exitCode": 0,
                 "eventSource": "R1-01"}
        record = {"schema": paired.WITNESS_SCHEMA,
                  "graphSha256": hb.hash_file(self.clean.graph_path)[0],
                  "observationSha256": hb.hash_file(self.clean.observation_path)[0],
                  "events": [event]}
        witness = self.clean.tmp / "synthetic-external-crosswalk.json"
        def write():
            witness.write_bytes(hb.canonical(record))
            return hb.hash_file(witness)[0]
        pin = write()
        self.assertEqual(paired.check_external_witness(witness, pin, self.clean.graph_path,
                         self.clean.observation_path), pin)
        with self.assertRaisesRegex(ValueError, "digest"):
            paired.check_external_witness(witness, "0" * 64, self.clean.graph_path,
                                          self.clean.observation_path)
        event["argvSha256"] = "0" * 64
        with self.assertRaisesRegex(ValueError, "crosswalk mismatch"):
            paired.check_external_witness(witness, write(), self.clean.graph_path,
                                          self.clean.observation_path)
        event["argvSha256"] = project["argvSha256"]
        event_file.write_bytes(b"tampered synthetic event")
        with self.assertRaisesRegex(ValueError, "event bytes differ"):
            paired.check_external_witness(witness, write(), self.clean.graph_path,
                                          self.clean.observation_path)
        event_file.write_bytes(b'{"synthetic":true,"executedCsc":false}\\n')
        record["events"].append(dict(event))
        with self.assertRaisesRegex(ValueError, "duplicate"):
            paired.check_external_witness(witness, write(), self.clean.graph_path,
                                          self.clean.observation_path)
        record["events"].pop()
        event["exitCode"] = 1
        with self.assertRaisesRegex(ValueError, "nonzero exit"):
            paired.check_external_witness(witness, write(), self.clean.graph_path,
                                          self.clean.observation_path)
        self.modify_dirty()
        self.assert_refused("missing exact reviewed R1-01 witness SHA",
                            require_witness=True)

    def test_core_512m_boundary_refusal(self):
        self.modify_dirty()
        with mock.patch.object(paired, "CORE_MAX_BYTES", 1):
            self.assert_refused("512 MiB")

    @unittest.skipUnless(shutil.which("git"), "git binary unavailable")
    def test_wrong_clean_dirty_git_state_refused(self):
        def git(root, *args):
            subprocess.run(["git", "-C", str(root), *args], capture_output=True, check=True)
        git(self.clean.golden, "init", "-q")
        git(self.clean.golden, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
            "add", "--all")
        git(self.clean.golden, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
            "commit", "-qm", "fixture")
        shutil.copytree(self.clean.golden / ".git", self.dirty.golden / ".git")
        commit = subprocess.check_output(["git", "-C", str(self.clean.golden), "rev-parse", "HEAD"]).decode().strip()
        self.modify_dirty()
        self.clean.original.write_bytes(self.clean.original.read_bytes() + b"// unapproved clean change")
        with self.assertRaisesRegex(ValueError, "pinned clean snapshot"):
            self.run_pair(clean_commit=commit, dirty_commit=commit, require_git=True)



if __name__ == "__main__":
    unittest.main()
