"""Mutation-only transport tests. NOT a substitute for a real Csc task."""
import copy
import json
from pathlib import Path
import shutil
import tempfile
import unittest

import task_csc_observation as o
import segmented_csc_capture_v2 as c


class TaskTimeObserverMutationTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="hb-task-contract-"))
        self.tool = self.tmp / "csc.dll"
        self.tool.write_bytes(b"fake compiler for negative parser tests; NEVER execution proof")
        self.log = self.tmp / "events.json"
        self.events = {
            "schema": o.EVENT_SCHEMA,
            "disposition": "unqualified-software-observation",
            "error": None,
            "events": [{
                "Id": "1:2:3", "ProjectFile": str(self.tmp / "P.csproj"),
                "StartUtc": "2026-11-20T00:00:00Z",
                "FinishUtc": "2026-11-20T00:00:01Z",
                "CommandLine": "dotnet exec " + str(self.tool) + " /noconfig /target:library",
                "Succeeded": True, "CommandEvents": 1
            }]
        }
        self.save()

    def tearDown(self):
        shutil.rmtree(self.tmp)

    def save(self):
        self.log.write_bytes(c.canonical(self.events))

    def test_task_context_bound(self):
        self.assertEqual(len(o.read_events(str(self.log))), 1)
        self.assertEqual(o.exact_argv(self.events["events"][0]["CommandLine"], self.tool),
                         ["/noconfig", "/target:library"])

    def test_synthetic_event_cannot_claim_actual_execution(self):
        # This is exclusively JSON parser coverage. Only the CI build + live
        # logger invocation can supply a genuine task execution receipt.
        self.events["events"][0]["Succeeded"] = False
        self.save()
        with self.assertRaisesRegex(ValueError, "failed"):
            o.read_events(str(self.log))

    def test_missing_command_and_duplicate_context(self):
        self.events["events"][0]["CommandEvents"] = 0
        self.save()
        with self.assertRaisesRegex(ValueError, "exactly one"):
            o.read_events(str(self.log))
        self.events["events"][0]["CommandEvents"] = 1
        self.events["events"].append(copy.deepcopy(self.events["events"][0]))
        self.save()
        with self.assertRaisesRegex(ValueError, "duplicate"):
            o.read_events(str(self.log))

    def test_unknown_schema_and_duplicate_json_key(self):
        self.events["schema"] = "fake-verified"
        self.save()
        with self.assertRaisesRegex(ValueError, "schema"):
            o.read_events(str(self.log))
        self.log.write_text('{"schema":"x","schema":"y"}')
        with self.assertRaisesRegex(ValueError, "duplicate"):
            o.read_events(str(self.log))

    def test_response_file_is_blocked_without_task_time_bytes(self):
        with self.assertRaisesRegex(ValueError, "response"):
            o.exact_argv("dotnet exec " + str(self.tool) + " @args.rsp", self.tool)

    def test_wrong_tool_and_ambiguous_quotes(self):
        other = self.tmp / "other.dll"
        other.write_bytes(b"other")
        with self.assertRaisesRegex(ValueError, "unexpected compiler"):
            o.exact_argv("dotnet exec " + str(self.tmp / "csc.dll") + " /noconfig", other)
        with self.assertRaisesRegex(ValueError, "quoting"):
            o.exact_argv('dotnet exec "' + str(self.tool) + " /noconfig", self.tool)

    def test_root_and_symlink_refusal(self):
        with self.assertRaisesRegex(ValueError, "TEMP"):
            o.require_temp_dir(str(Path("/")))
        target = self.tmp / "real"
        target.mkdir()
        link = self.tmp / "link"
        try:
            link.symlink_to(target, target_is_directory=True)
        except (OSError, NotImplementedError):
            self.skipTest("symlink unavailable")
        with self.assertRaisesRegex(ValueError, "symlink"):
            o.require_temp_dir(str(link))


if __name__ == "__main__":
    unittest.main()
