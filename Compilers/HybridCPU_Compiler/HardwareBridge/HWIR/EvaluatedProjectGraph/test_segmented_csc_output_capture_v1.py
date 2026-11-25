"""Unit tests for the additive output-byte sidecar; no real compiler is simulated."""
import copy
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
import tempfile
import unittest
import uuid

sys.path.insert(0, str(Path(__file__).parent))
import segmented_csc_capture_v2 as v2
import segmented_csc_output_capture_v1 as output


def digest(data):
    return hashlib.sha256(data).hexdigest()


def write_json(path, value):
    Path(path).write_bytes(v2.canonical(value))


class OutputCaptureTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="hb-csc-output-v1-"))
        self.golden = self.tmp / "golden"
        self.isolated = self.tmp / "isolated"
        self.golden.mkdir()
        self.isolated.mkdir()
        self.project = self.golden / "HybridCPU_ISE.csproj"
        self.project.write_bytes(b"<Project />")
        self.source = self.golden / "A.cs"
        self.source.write_bytes(b"public sealed class A {}\n")
        self.isolated_source = self.isolated / "A.cs"
        self.isolated_source.write_bytes(self.source.read_bytes())
        self.compiler = self.isolated / "csc.exe"
        self.compiler.write_bytes(b"test pin only; not a compiler")
        self.graph_path = self.tmp / "graph.json"
        self.observation_path = self.tmp / "observation.json"
        self.node_id = "HybridCPU_ISE|Configuration=Release;TargetFramework=net11.0"
        self.cwd = self.isolated
        self.argv = [
            "/target:library", "/langversion:preview", "/define:RELEASE",
            "/nullable:enable", "/out:obj/Release/net11.0/HybridCPU_ISE.dll",
            "/pdb:obj/Release/net11.0/HybridCPU_ISE.pdb",
            "/refout:obj/Release/net11.0/refint/HybridCPU_ISE.dll",
            str(self.isolated_source),
        ]
        self.graph = {"schema": v2.GRAPH_SCHEMA, "root": str(self.project), "nodes": [{
            "id": self.node_id, "project": str(self.project),
            "compileFiles": [str(self.source)], "globalProperties": {"Configuration": "Release"},
            "projectAssets": None,
        }]}
        self.observation = {"schema": v2.OBS_SCHEMA, "graphSha256": "", "projects": [{
            "nodeId": self.node_id, "role": "primary", "workingDirectory": str(self.cwd),
            "compiler": {"path": str(self.compiler), "sha256": digest(self.compiler.read_bytes())},
            "projectSha256": digest(self.project.read_bytes()), "filePins": [],
            "argv": self.argv, "argvSha256": digest(v2.canonical(self.argv)),
            "sources": [{"path": str(self.isolated_source), "logicalPath": "project/A.cs",
                         "sha256": digest(self.isolated_source.read_bytes()), "origin": "evaluated",
                         "originalPath": str(self.source)}],
            "generatorSources": [],
        }]}
        write_json(self.graph_path, self.graph)
        self.observation["graphSha256"] = digest(self.graph_path.read_bytes())
        write_json(self.observation_path, self.observation)

        self.outputs = []
        specs = [
            ("compiler-pe", "obj/Release/net11.0/HybridCPU_ISE.dll", b"PE fixture bytes"),
            ("compiler-pdb", "obj/Release/net11.0/HybridCPU_ISE.pdb", b"PDB fixture bytes"),
            ("compiler-refout", "obj/Release/net11.0/refint/HybridCPU_ISE.dll", b"REF fixture bytes"),
        ]
        for role, logical, data in specs:
            source = self.cwd.joinpath(*Path(logical).parts)
            source.parent.mkdir(parents=True, exist_ok=True)
            source.write_bytes(data)
            self.outputs.append({"role": role, "logicalPath": logical, "sourcePath": str(source),
                                 "sha256": digest(data), "bytes": len(data)})
        self.crosswalk_sha = digest(b"crosswalk witness fixture")
        self.comparison_sha = digest(b"44/44 comparison witness fixture")
        self.parent_dir = self.temp_child("parent")
        self.parent_closure = v2.capture(str(self.graph_path), str(self.observation_path),
                                         str(self.golden), str(self.parent_dir))
        self.temp_outputs = [self.parent_dir]
        self.parent_index_path = self.parent_dir / "index.json"
        self.parent_index_sha = digest(self.parent_index_path.read_bytes())
        self.capture_index_sha = digest(b"raw HardwareBridge capture index pin")
        self.capture_root_sha = digest(b"raw HardwareBridge capture root pin")
        self.manifest_path = self.tmp / "output-manifest.json"
        self.save_manifest()

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)
        for child in getattr(self, "temp_outputs", []):
            shutil.rmtree(child, ignore_errors=True)

    def temp_child(self, label):
        candidate = Path(tempfile.gettempdir()) / ("hb-output-v1-" + label + "-" + uuid.uuid4().hex)
        return candidate

    def save_manifest(self, *, resource_status="NotPresent", outputs=None,
                      base_index_sha=None, base_closure_sha=None):
        value = {
            "schema": output.MANIFEST_SCHEMA,
            "disposition": "unqualified-software-output-observation",
            "baseIndexSha256": base_index_sha or self.parent_index_sha,
            "baseClosureSha256": base_closure_sha or self.parent_closure,
            "primaryNodeId": self.node_id,
            "primaryArgvSha256": digest(v2.canonical(self.argv)),
            "taskCrosswalkSha256": self.crosswalk_sha,
            "directBuildComparisonSha256": self.comparison_sha,
            "resourceOutputStatus": resource_status,
            "outputs": copy.deepcopy(self.outputs if outputs is None else outputs),
        }
        write_json(self.manifest_path, value)
        return digest(self.manifest_path.read_bytes())

    def run_capture(self, **kwargs):
        target = self.temp_child("sidecar")
        self.temp_outputs = getattr(self, "temp_outputs", [])
        self.temp_outputs.append(target)
        manifest_sha = self.save_manifest(**kwargs)
        result = self.call_capture(target, self.parent_index_sha, self.parent_closure, manifest_sha)
        verified = output.verify(str(target), expected_capture_index_sha=self.capture_index_sha,
                                 expected_capture_root_sha=self.capture_root_sha,
                                 expected_output_index_sha=result["indexSha256"],
                                 expected_task_witness_sha=result["taskOutputWitnessSha256"])
        self.assertEqual(result, verified)
        return target, result

    def call_capture(self, target, index_sha, closure_sha, manifest_sha):
        return output.capture(str(self.parent_dir), index_sha, closure_sha,
                              str(self.manifest_path), manifest_sha, str(target),
                              expected_capture_index_sha=self.capture_index_sha,
                              expected_capture_root_sha=self.capture_root_sha)

    def test_positive_capture_is_separate_and_bound_to_primary_argv(self):
        sidecar, result = self.run_capture()
        self.assertEqual(result["rows"], 3)
        self.assertEqual(result["resourceOutputStatus"], "NotPresent")
        self.assertEqual(v2.verify(str(self.parent_dir)), self.parent_closure)
        self.assertEqual(digest(self.parent_index_path.read_bytes()), self.parent_index_sha)
        index = json.loads((sidecar / "index.json").read_bytes())
        self.assertEqual(index["Schema"], output.INDEX_SCHEMA)
        self.assertEqual(index["TaskExecution"], "NotEstablished")
        self.assertEqual(index["ResponseConsumption"], "NotEstablished")
        self.assertEqual(index["Admission"], "NotAdmitted")
        rows = [json.loads(line) for line in (sidecar / output.CHUNK_NAME).read_text().splitlines()]
        self.assertEqual([row["Role"] for row in rows],
                         ["compiler-pe", "compiler-pdb", "compiler-refout"])
        self.assertTrue(all("\\" not in row["Path"] and row["Sha256"] == row["Sha256"].upper()
                            for row in rows))

    def test_output_rows_use_dotnet_utf16_ordinal_order(self):
        # Python code-point ordering puts U+E000 before U+10000; .NET ordinal
        # compares UTF-16 units and puts U+10000's D800 surrogate first.
        bmp = "\uE000"
        supplementary = "\U00010000"
        self.assertLess(bmp, supplementary)
        self.assertLess(output._ordinal_key(supplementary), output._ordinal_key(bmp))

        argv = list(self.argv)
        argv[4] = "/out:obj/Release/net11.0/" + bmp + ".dll"
        argv[5] = "/pdb:obj/Release/net11.0/" + supplementary + ".pdb"
        argv[6] = "/refout:obj/Release/net11.0/refint/Ref.dll"
        self.argv = argv
        project = self.observation["projects"][0]
        project["argv"] = argv
        project["argvSha256"] = digest(v2.canonical(argv))
        write_json(self.observation_path, self.observation)

        parent = self.temp_child("utf16-parent")
        self.parent_dir = parent
        self.temp_outputs.append(parent)
        self.parent_closure = v2.capture(str(self.graph_path), str(self.observation_path),
                                         str(self.golden), str(parent))
        self.parent_index_path = parent / "index.json"
        self.parent_index_sha = digest(self.parent_index_path.read_bytes())

        specs = [
            ("compiler-pe", "obj/Release/net11.0/" + bmp + ".dll", b"PE fixture bytes"),
            ("compiler-pdb", "obj/Release/net11.0/" + supplementary + ".pdb", b"PDB fixture bytes"),
            ("compiler-refout", "obj/Release/net11.0/refint/Ref.dll", b"REF fixture bytes"),
        ]
        self.outputs = []
        for role, logical, data in specs:
            source = self.cwd.joinpath(*Path(logical).parts)
            source.parent.mkdir(parents=True, exist_ok=True)
            source.write_bytes(data)
            self.outputs.append({"role": role, "logicalPath": logical, "sourcePath": str(source),
                                 "sha256": digest(data), "bytes": len(data)})

        sidecar, _ = self.run_capture()
        rows = [json.loads(line) for line in (sidecar / output.CHUNK_NAME).read_text().splitlines()]
        paths = [row["Path"] for row in rows]
        self.assertLess(paths.index("obj/Release/net11.0/" + supplementary + ".pdb"),
                        paths.index("obj/Release/net11.0/" + bmp + ".dll"))

    def test_stale_parent_index_or_closure_pins_refused(self):
        manifest_sha = self.save_manifest()
        with self.assertRaisesRegex(ValueError, "parent V2 index SHA"):
            self.call_capture(self.temp_child("stale-index"), "0" * 64,
                              self.parent_closure, manifest_sha)
        with self.assertRaisesRegex(ValueError, "parent V2 closure SHA"):
            self.call_capture(self.temp_child("stale-closure"), self.parent_index_sha,
                              "0" * 64, manifest_sha)

    def test_manifest_parent_binding_and_manifest_pin_refused(self):
        bad_sha = self.save_manifest(base_index_sha="0" * 64)
        with self.assertRaisesRegex(ValueError, "stale/different V2 parent"):
            self.call_capture(self.temp_child("manifest-parent"), self.parent_index_sha,
                              self.parent_closure, bad_sha)
        good_sha = self.save_manifest()
        with self.assertRaisesRegex(ValueError, "external SHA-256 pin"):
            self.call_capture(self.temp_child("manifest-pin"), self.parent_index_sha,
                              self.parent_closure, "0" * 64)

    def test_missing_or_duplicate_required_roles_and_duplicate_paths_refused(self):
        missing = [row for row in self.outputs if row["role"] != "compiler-pe"]
        with self.assertRaisesRegex(ValueError, "missing required"):
            self.run_capture(outputs=missing)
        duplicate_role = self.outputs + [copy.deepcopy(self.outputs[0])]
        with self.assertRaisesRegex(ValueError, "duplicate/unsupported Csc output role"):
            self.run_capture(outputs=duplicate_role)
        duplicate_path = copy.deepcopy(self.outputs)
        duplicate_path[1]["logicalPath"] = duplicate_path[0]["logicalPath"]
        with self.assertRaisesRegex(ValueError, "duplicate output logical path"):
            self.run_capture(outputs=duplicate_path)

    def test_resource_status_has_exact_optional_role_cardinality(self):
        with self.assertRaisesRegex(ValueError, "cardinality"):
            self.run_capture(resource_status="CapturedTaskOutput")
        resource = self.cwd / "obj/Release/net11.0/native.res"
        resource.write_bytes(b"native resource fixture")
        with_resource = self.outputs + [{"role": "win32-resource-output",
            "logicalPath": "obj/Release/net11.0/native.res", "sourcePath": str(resource),
            "sha256": digest(resource.read_bytes()), "bytes": resource.stat().st_size}]
        _, result = self.run_capture(resource_status="CapturedTaskOutput", outputs=with_resource)
        self.assertEqual(result["rows"], 4)
        self.assertEqual(result["resourceOutputStatus"], "CapturedTaskOutput")

    def test_missing_and_altered_output_source_refused(self):
        row = self.outputs[0]
        Path(row["sourcePath"]).unlink()
        with self.assertRaisesRegex(ValueError, "missing input/chunk"):
            self.run_capture()
        Path(row["sourcePath"]).write_bytes(b"changed bytes")
        with self.assertRaisesRegex(ValueError, "changed/stale file hash"):
            self.run_capture()

    def test_unrelated_hash_pinned_source_cannot_impersonate_argv_output(self):
        unrelated = self.isolated / "unrelated-output.bin"
        unrelated.write_bytes(b"not the primary Csc PE output")
        forged = copy.deepcopy(self.outputs)
        forged[0]["sourcePath"] = str(unrelated)
        forged[0]["sha256"] = digest(unrelated.read_bytes())
        forged[0]["bytes"] = unrelated.stat().st_size
        with self.assertRaisesRegex(ValueError, "source path differs from primary Csc argv location"):
            self.run_capture(outputs=forged)

    def test_same_relative_path_in_alternate_root_cannot_impersonate_argv_output(self):
        row = self.outputs[0]
        alternate = self.tmp / "alternate-root" / row["logicalPath"]
        alternate.parent.mkdir(parents=True)
        shutil.copyfile(row["sourcePath"], alternate)
        forged = copy.deepcopy(self.outputs)
        forged[0]["sourcePath"] = str(alternate)
        with self.assertRaisesRegex(ValueError, "Csc output escapes the primary working directory"):
            self.run_capture(outputs=forged)

    def test_wrong_size_or_hash_refused(self):
        bad = copy.deepcopy(self.outputs)
        bad[0]["sha256"] = "0" * 64
        with self.assertRaisesRegex(ValueError, "changed/stale file hash"):
            self.run_capture(outputs=bad)
        bad = copy.deepcopy(self.outputs)
        bad[0]["bytes"] += 1
        with self.assertRaisesRegex(ValueError, "changed/stale file size"):
            self.run_capture(outputs=bad)

    def test_output_payload_tampering_refused_by_independent_verify(self):
        sidecar, result = self.run_capture()
        index = json.loads((sidecar / "index.json").read_bytes())
        self.assertEqual(index["BaseIndexSha256"], self.capture_index_sha.upper())
        self.assertEqual(index["BaseClosureSha256"], self.capture_root_sha.upper())
        with self.assertRaisesRegex(ValueError, "stale/different raw Bridge capture"):
            output.verify(str(sidecar), expected_capture_index_sha="0" * 64,
                          expected_capture_root_sha=self.capture_root_sha,
                          expected_output_index_sha=result["indexSha256"],
                          expected_task_witness_sha=result["taskOutputWitnessSha256"])
        payload = sidecar / self.outputs[0]["logicalPath"]
        payload.write_bytes(b"tampered after capture")
        with self.assertRaisesRegex(ValueError, "output payload missing/altered"):
            output.verify(str(sidecar), expected_capture_index_sha=self.capture_index_sha,
                          expected_capture_root_sha=self.capture_root_sha,
                          expected_output_index_sha=result["indexSha256"],
                          expected_task_witness_sha=result["taskOutputWitnessSha256"])

    def test_invalid_resource_status_and_primary_binding_refused(self):
        with self.assertRaisesRegex(ValueError, "status must be explicit"):
            self.run_capture(resource_status="Unknown")
        value = json.loads(self.manifest_path.read_bytes())
        value["primaryArgvSha256"] = "0" * 64
        write_json(self.manifest_path, value)
        with self.assertRaisesRegex(ValueError, "primary command binding"):
            self.call_capture(self.temp_child("bad-command"), self.parent_index_sha,
                              self.parent_closure, digest(self.manifest_path.read_bytes()))


if __name__ == "__main__":
    unittest.main(verbosity=2)
