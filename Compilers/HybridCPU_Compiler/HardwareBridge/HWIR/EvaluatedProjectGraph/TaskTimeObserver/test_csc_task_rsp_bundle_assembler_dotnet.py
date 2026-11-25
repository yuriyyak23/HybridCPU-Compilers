"""Windows contract fixtures for the additive C# task/RSP bundle assembler."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

HERE = Path(__file__).parent
sys.path.insert(0, str(HERE))
import test_windows_task_rsp_bundle_v1 as fixture_contract
import windows_task_rsp_bundle_v1 as python_contract


@unittest.skipUnless(os.name == "nt", "assembler binds Win32 CommandLineToArgvW")
class CSharpBundleAssemblerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.artifacts = Path(tempfile.mkdtemp(prefix="hb-csharp-rsp-build-"))
        project = HERE / "CscTaskRspBundleAssembler.csproj"
        subprocess.run(["dotnet", "build", str(project), "-c", "Release",
                        "-p:ArtifactsPath=" + str(cls.artifacts)], check=True,
                       capture_output=True, text=True)
        cls.dll = cls.artifacts / "bin" / "CscTaskRspBundleAssembler" / "CscTaskRspBundleAssembler.dll"

    @classmethod
    def tearDownClass(cls):
        import shutil
        shutil.rmtree(cls.artifacts, ignore_errors=True)

    def setUp(self):
        self.fixture = fixture_contract.SyntheticWindowsTaskRspBundleTests()
        self.fixture.setUp()

    def tearDown(self):
        self.fixture.tearDown()

    def arguments(self, output):
        f = self.fixture
        return ["assemble", str(f.events_path), f.pins["events"], str(f.process_path), f.pins["process"],
                str(f.crosswalk_path), f.pins["crosswalk"], str(f.receipt_path), f.pins["receipt"],
                str(f.responses), f.commit, f.tree, f.compiler, f.compiler_sha, str(output)]

    def run_assembler(self, output, args=None):
        return subprocess.run(["dotnet", str(self.dll), *(args or self.arguments(output))],
                              capture_output=True, text=True)

    def test_positive_bundle_matches_python_contract_bytes(self):
        output = Path(tempfile.gettempdir()) / ("hb-csharp-rsp-positive-" + next(tempfile._get_candidate_names()) + ".json")
        result = self.run_assembler(output)
        self.assertEqual(result.returncode, 0, result.stderr)
        expected = python_contract.validate(str(self.fixture.events_path), self.fixture.pins["events"],
            str(self.fixture.process_path), self.fixture.pins["process"], str(self.fixture.crosswalk_path),
            self.fixture.pins["crosswalk"], str(self.fixture.receipt_path), self.fixture.pins["receipt"],
            str(self.fixture.responses), self.fixture.commit, self.fixture.tree, self.fixture.compiler,
            self.fixture.compiler_sha)
        expected_bytes = (json.dumps(expected, ensure_ascii=False, sort_keys=True, separators=(",", ":")) + "\n").encode()
        self.assertEqual(output.read_bytes(), expected_bytes)
        self.assertEqual(hashlib.sha256(output.read_bytes()).hexdigest(), json.loads(result.stdout)["outputSha256"])
        self.assertEqual(expected["claims"]["ResponseBytesConsumed"], "NotEstablished")
        self.assertTrue(all(row["workingDirectory"] == {"status": "NotRecorded", "value": None}
                            for row in expected["commands"]))

    def test_stale_task_event_pin_fails_before_publication(self):
        output = Path(tempfile.gettempdir()) / ("hb-csharp-rsp-stale-" + next(tempfile._get_candidate_names()) + ".json")
        args = self.arguments(output)
        args[2] = "0" * 64
        result = self.run_assembler(output, args)
        self.assertEqual(result.returncode, 2)
        self.assertFalse(output.exists())

    def test_mutated_response_copy_fails_before_publication(self):
        response = self.fixture.responses / "csc-30904.rsp"
        original = response.read_bytes()
        output = Path(tempfile.gettempdir()) / ("hb-csharp-rsp-mutated-" + next(tempfile._get_candidate_names()) + ".json")
        try:
            response.write_bytes(original + b"\n")
            result = self.run_assembler(output)
            self.assertEqual(result.returncode, 2)
            self.assertIn("length/SHA-256", result.stderr)
            self.assertFalse(output.exists())
        finally:
            response.write_bytes(original)


if __name__ == "__main__":
    unittest.main()
