"""Synthetic-only negative and contract tests for the Windows bundle adapter."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

import windows_task_rsp_bundle_v1 as bundle


def sha(data):
    return hashlib.sha256(data).hexdigest()


def put_json(path, value):
    raw = (json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")
    Path(path).write_bytes(raw)
    return sha(raw)


@unittest.skipUnless(os.name == "nt", "Win32 CommandLineToArgvW contract is Windows-only")
class SyntheticWindowsTaskRspBundleTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="hb-win-rsp-bundle-")
        self.root = Path(self.temp.name)
        self.responses = self.root / "responses"
        self.responses.mkdir()
        compiler_file = self.root / "sdk" / "Roslyn" / "bincore" / "csc.exe"
        compiler_file.parent.mkdir(parents=True)
        compiler_file.write_bytes(b"synthetic compiler image")
        self.compiler = str(compiler_file)
        self.compiler_sha = sha(compiler_file.read_bytes())
        self.commit = "a" * 40
        self.tree = "b" * 40
        self.events_path = self.root / "task-events.json"
        self.process_path = self.root / "process-observation.json"
        self.crosswalk_path = self.root / "crosswalk.json"
        self.receipt_path = self.root / "receipt.json"
        self.output_path = self.root / "bundle.json"
        self.events = {"schema": bundle.TASK_SCHEMA, "disposition": "unqualified-software-observation",
                       "error": None, "events": []}
        self.process = {
            "schema": bundle.PROCESS_SCHEMA, "pinnedHead": self.commit,
            "trackedTreeBefore": self.tree, "workingTreeBefore": None, "buildExit": 0,
            "projectRoot": str(self.root / "project"), "sdk": "pinned synthetic SDK",
            "pollIntervalMilliseconds": 35, "observedCscProcesses": [], "observedCscCount": 5,
            "taskEventsCaptured": True, "trackedTreeAfter": self.tree, "headAfter": self.commit,
            "workingTreeAfter": None,
            "processObservation": "Sampled Win32_Process rows; not an ETW/kernel or file-read trace"
        }
        self.crosswalk = {
            "schema": bundle.CROSSWALK_SCHEMA,
            "parser": "Windows CommandLineToArgvW applied to each raw response-file string and MSBuild TaskCommandLineEventArgs.CommandLine; dropped compiler executable and /noconfig; ordered token comparison",
            "taskEventsSha256": "0" * 64, "processObservationSha256": "0" * 64,
            "crosswalkCount": 5, "allOrderedArgumentsEqual": True, "crosswalks": [],
            "limitations": ["Synthetic fixture only; no response bytes consumed claim."]
        }
        self.receipt = {
            "schema": bundle.RECEIPT_SCHEMA, "status": "ObservedLocalUnqualified",
            "pinnedGoldenCommit": self.commit, "trackedTreeBefore": self.tree,
            "trackedTreeAfter": self.tree, "workingTreeAfter": None, "sdk": "pinned synthetic SDK",
            "buildExit": 0, "taskEventFileSha256": "0" * 64, "taskEventCount": 5,
            "successfulTaskEventCount": 5,
            "processObservationMethod": "35ms sampled Win32_Process rows; not ETW/kernel complete",
            "processCount": 5, "processes": [],
            "claims": {"CscFileReadBuffersObserved": False, "ActualCscProcessObserved": True,
                       "ActualResponseFileBytesCaptured": True, "IndependentCoreAdmission": False,
                       "TrustedHandoff": False, "ResponseTokensCrosswalkedToTaskEvent": True,
                       "TaskExecutionSucceeded": True},
            "limits": ["Synthetic evidence only; copied bytes do not prove reads."],
            "responseTaskCrosswalk": {"file": "crosswalk.json", "sha256": "0" * 64,
                                      "crosswalkCount": 5, "allOrderedArgumentsEqual": True,
                                      "parser": self.crosswalk["parser"]},
            "captureFiles": []
        }
        for index, pid in enumerate((30904, 30632, 27412, 18052, 6468)):
            task_id = f"1:{index + 1}:{500 + index}"
            project = str(self.root / "project" / f"Project{index}.csproj")
            response_path = str(self.root / "MSBuildTemp" / f"csc-{pid}.rsp")
            response_copy = self.responses / f"csc-{pid}.rsp"
            args = ["/unsafe-", "/checked-", f"/define:DEFINE{index}", "/langversion:15",
                    "/nullable:disable", "/target:library", f"/out:obj\\Release\\A{index}.dll",
                    f"Source File {index}.cs", f"/reference:C:\\refs\\Ref {index}.dll"]
            rsp_bytes = subprocess.list2cmdline(args).encode("utf-8")
            response_copy.write_bytes(rsp_bytes)
            rsp_sha = sha(rsp_bytes)
            task_command = subprocess.list2cmdline([self.compiler, "/noconfig", *args])
            os_command = subprocess.list2cmdline([self.compiler, "/noconfig", "@" + response_path])
            self.events["events"].append({"Id": task_id, "ProjectFile": project,
                "StartUtc": "2026-11-22T10:00:00.0000000+00:00", "FinishUtc": "2026-11-22T10:00:01.0000000+00:00",
                "CommandLine": task_command, "Succeeded": True, "CommandEvents": 1})
            self.process["observedCscProcesses"].append({"observedUtc": "2026-11-22T10:00:00.5+00:00",
                "pid": pid, "parentPid": 14000, "processName": "csc.exe", "executablePath": self.compiler,
                "commandLine": os_command, "responseFilePath": response_path,
                "responseCopy": str(response_copy), "responseBytes": len(rsp_bytes),
                "responseSha256": rsp_sha, "responseCaptureError": None})
            self.receipt["processes"].append({"pid": pid, "parentPid": 14000, "processName": "csc.exe",
                "executablePath": self.compiler, "executableSha256": self.compiler_sha,
                "commandLine": os_command, "responsePath": response_path,
                "responseCopy": response_copy.name, "responseLength": len(rsp_bytes), "responseSha256": rsp_sha})
            self.crosswalk["crosswalks"].append({"pid": pid, "responseSha256": rsp_sha,
                "outputToken": f"/out:obj\\Release\\A{index}.dll", "taskEventId": task_id,
                "projectFile": project, "taskSucceeded": True, "taskArgumentCount": len(args),
                "responseArgumentCount": len(args), "orderedArgumentsEqual": True, "differences": []})
        self._write_bundle()

    def tearDown(self):
        self.temp.cleanup()

    def _write_bundle(self):
        event_sha = put_json(self.events_path, self.events)
        process_sha = put_json(self.process_path, self.process)
        self.crosswalk["taskEventsSha256"] = event_sha
        self.crosswalk["processObservationSha256"] = process_sha
        cross_sha = put_json(self.crosswalk_path, self.crosswalk)
        self.receipt["taskEventFileSha256"] = event_sha
        self.receipt["responseTaskCrosswalk"]["sha256"] = cross_sha
        put_json(self.receipt_path, self.receipt)
        self.pins = {"events": event_sha, "process": process_sha, "crosswalk": cross_sha,
                     "receipt": sha(self.receipt_path.read_bytes())}

    def _validate(self, *, pins=None):
        pins = pins or self.pins
        return bundle.validate(str(self.events_path), pins["events"], str(self.process_path), pins["process"],
            str(self.crosswalk_path), pins["crosswalk"], str(self.receipt_path), pins["receipt"],
            str(self.responses), self.commit, self.tree, self.compiler, self.compiler_sha)

    def _reseal(self):
        self._write_bundle()

    def test_synthetic_positive_preserves_unqualified_claims_and_unknown_cwd(self):
        result = self._validate()
        self.assertEqual(result["disposition"], "unqualified-software-observation")
        self.assertEqual(len(result["commands"]), 5)
        self.assertTrue(all(c["workingDirectory"] == {"status": "NotRecorded", "value": None}
                            for c in result["commands"]))
        self.assertEqual(result["claims"]["ResponseBytesConsumed"], "NotEstablished")
        self.assertEqual(result["claims"]["IndependentCoreAdmission"], "NotAdmitted")

    def test_missing_process_is_refused(self):
        self.process["observedCscProcesses"].pop()
        self.process["observedCscCount"] = 4
        self._reseal()
        with self.assertRaises(bundle.Refused): self._validate()

    def test_duplicate_process_pid_is_refused(self):
        self.process["observedCscProcesses"][1]["pid"] = self.process["observedCscProcesses"][0]["pid"]
        self._reseal()
        with self.assertRaises(bundle.Refused): self._validate()

    def test_duplicate_task_identity_is_refused(self):
        self.events["events"][1]["Id"] = self.events["events"][0]["Id"]
        self._reseal()
        with self.assertRaises(bundle.Refused): self._validate()

    def test_stale_external_task_pin_is_refused(self):
        pins = dict(self.pins); pins["events"] = "f" * 64
        with self.assertRaises(bundle.Refused): self._validate(pins=pins)

    def test_altered_response_bytes_are_refused(self):
        path = self.responses / "csc-30904.rsp"
        path.write_bytes(path.read_bytes() + b" altered")
        with self.assertRaises(bundle.Refused): self._validate()

    def test_swapped_response_copy_identity_is_refused(self):
        self.process["observedCscProcesses"][0]["responseCopy"] = str(self.responses / "csc-30632.rsp")
        self._reseal()
        with self.assertRaises(bundle.Refused): self._validate()

    def test_response_token_reordering_is_refused_even_when_hashes_are_resealed(self):
        path = self.responses / "csc-30904.rsp"
        tokens = bundle.parse_windows_command_line(path.read_bytes().decode("utf-8"))
        tokens[0], tokens[1] = tokens[1], tokens[0]
        raw = subprocess.list2cmdline(tokens).encode("utf-8")
        path.write_bytes(raw)
        row = self.process["observedCscProcesses"][0]
        row["responseBytes"] = len(raw); row["responseSha256"] = sha(raw)
        row2 = self.receipt["processes"][0]
        row2["responseLength"] = len(raw); row2["responseSha256"] = sha(raw)
        self.crosswalk["crosswalks"][0]["responseSha256"] = sha(raw)
        self._reseal()
        with self.assertRaises(bundle.Refused): self._validate()

    def test_process_task_project_mismatch_is_refused(self):
        self.crosswalk["crosswalks"][0]["projectFile"] = str(self.root / "other.csproj")
        self._reseal()
        with self.assertRaises(bundle.Refused): self._validate()

    def test_response_process_path_mismatch_is_refused(self):
        self.process["observedCscProcesses"][0]["responseFilePath"] = str(self.root / "other.rsp")
        self._reseal()
        with self.assertRaises(bundle.Refused): self._validate()

    def test_wrong_compiler_hash_is_refused(self):
        with self.assertRaises(bundle.Refused):
            bundle.validate(str(self.events_path), self.pins["events"], str(self.process_path), self.pins["process"],
                str(self.crosswalk_path), self.pins["crosswalk"], str(self.receipt_path), self.pins["receipt"],
                str(self.responses), self.commit, self.tree, self.compiler, "d" * 64)

    def test_compiler_image_mutation_is_refused(self):
        Path(self.compiler).write_bytes(b"altered synthetic compiler image")
        with self.assertRaises(bundle.Refused): self._validate()

    def test_failed_task_is_refused(self):
        self.events["events"][0]["Succeeded"] = False
        self._reseal()
        with self.assertRaises(bundle.Refused): self._validate()

    def test_nested_response_argument_is_refused(self):
        path = self.responses / "csc-30904.rsp"
        raw = subprocess.list2cmdline(["@nested.rsp"]).encode("utf-8")
        path.write_bytes(raw)
        self.process["observedCscProcesses"][0]["responseBytes"] = len(raw)
        self.process["observedCscProcesses"][0]["responseSha256"] = sha(raw)
        self.receipt["processes"][0]["responseLength"] = len(raw)
        self.receipt["processes"][0]["responseSha256"] = sha(raw)
        self.crosswalk["crosswalks"][0]["responseSha256"] = sha(raw)
        self._reseal()
        with self.assertRaises(bundle.Refused): self._validate()


if __name__ == "__main__":
    unittest.main()
