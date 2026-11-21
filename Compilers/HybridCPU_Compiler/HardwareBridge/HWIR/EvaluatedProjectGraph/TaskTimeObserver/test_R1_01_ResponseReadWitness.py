"""R1-01 syscall mutations. Synthetic results are NOT executed-Csc proof."""
import json
from pathlib import Path
import shutil
import tempfile
import unittest

import R1_01_ResponseReadWitness as rsp


class ResponseReadTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(tempfile.mkdtemp(prefix="r1-01-rsp-fixture-"))
        self.trace = self.root / "trace"
        self.trace.mkdir()
        self.file = str(Path(tempfile.gettempdir()) / "MSBuildTempR101" / "tmp123.rsp")
        self.compiler = "/tmp/dummy/csc"
        self.raw = [self.compiler, "/noconfig", "@" + self.file]
        self.task = [self.compiler, "/noconfig", "/target:library", "Source.cs"]
        self.data = b"/target:library Source.cs\n"
        self.rows = [
            f"1760000000.010000 openat(AT_FDCWD, {json.dumps(self.file)}, O_RDONLY|O_CLOEXEC) = 4",
            f"1760000000.020000 read(4, {json.dumps(self.data.decode())}, 4096) = {len(self.data)}",
            '1760000000.030000 read(4, "", 4096) = 0',
            "1760000000.040000 close(4) = 0"
        ]
        self.write()

    def tearDown(self):
        shutil.rmtree(self.root)

    def write(self):
        (self.trace / "trace.200").write_text("\n".join(self.rows) + "\n")

    def check(self):
        return rsp.proof(self.trace, pid=200, exec_time="1760000000.000000",
                         exit_time="1760000000.050000", raw_argv=self.raw,
                         task_argv=self.task)

    def test_positive_full_fd_open_read_eof_close(self):
        proof = self.check()
        self.assertEqual(proof["responseBytes"], len(self.data))
        self.assertEqual(proof["readCalls"], 2)
        self.assertEqual(proof["schema"], rsp.SCHEMA)

    def test_missing_or_late_open_is_blocked(self):
        self.rows = self.rows[1:]
        self.write()
        with self.assertRaisesRegex(rsp.Blocked, "MISSING_OPEN"):
            self.check()
        self.rows = ["1760000000.060000 " + self.rows[0].split(" ", 1)[1]] + self.rows
        self.write()
        with self.assertRaisesRegex(rsp.Blocked, "MISSING_OPEN"):
            self.check()

    def test_rsp_mutation_and_malformed_body_are_blocked(self):
        self.task[-1] = "Different.cs"
        with self.assertRaisesRegex(rsp.Blocked, "COMMAND_MISMATCH"):
            self.check()
        self.task[-1] = "Source.cs"
        self.rows[1] = self.rows[1].replace(str(len(self.data)), str(len(self.data) + 1))
        self.write()
        with self.assertRaisesRegex(rsp.Blocked, "READ_LENGTH"):
            self.check()

    def test_sibling_pid_cannot_supply_rsp_read(self):
        (self.trace / "trace.201").write_text("\n".join(self.rows) + "\n")
        (self.trace / "trace.200").write_text("1760000000.030000 +++ exited with 0 +++\n")
        with self.assertRaisesRegex(rsp.Blocked, "MISSING_OPEN"):
            self.check()

    def test_truncation_read_after_eof_and_symlink_trace(self):
        self.rows[1] = self.rows[1].replace("Source.cs", "Source.cs...")
        self.write()
        with self.assertRaisesRegex(rsp.Blocked, "READ_LENGTH"):
            self.check()
        self.rows[1] = f"1760000000.020000 read(4, {json.dumps(self.data.decode())}, 4096) = {len(self.data)}"
        self.rows.insert(3, self.rows[1].replace(".020000 ", ".035000 "))
        self.write()
        with self.assertRaisesRegex(rsp.Blocked, "READ_AFTER_EOF"):
            self.check()
        (self.trace / "trace.200").unlink()
        (self.trace / "trace.200").symlink_to(self.root / "missing")
        with self.assertRaisesRegex(rsp.Blocked, "MISSING_OR_OVERSIZE_TRACE"):
            self.check()

    def test_nonread_operations_on_rsp_fd_block(self):
        self.rows.insert(2, "1760000000.025000 lseek(4, 0, SEEK_SET) = 0")
        self.write()
        with self.assertRaisesRegex(rsp.Blocked, "UNSUPPORTED_FD"):
            self.check()
        self.rows[2] = f"1760000000.025000 openat(AT_FDCWD, {json.dumps(self.file)}, O_WRONLY) = 5"
        self.write()
        with self.assertRaisesRegex(rsp.Blocked, "UNSAFE_OR_DUPLICATE_OPEN"):
            self.check()

    def test_missing_eof_and_bad_path(self):
        self.rows = [self.rows[0], self.rows[1], self.rows[3]]
        self.write()
        with self.assertRaisesRegex(rsp.Blocked, "CLOSE_BEFORE_EOF"):
            self.check()
        self.raw[-1] = "@/tmp/escape.rsp"
        with self.assertRaisesRegex(rsp.Blocked, "UNTRUSTED_RSP_PATH"):
            self.check()

    def test_unknown_rsp_flags_do_not_gain_acceptance(self):
        self.task[-1] = "/unknown-option:danger"
        with self.assertRaisesRegex(rsp.Blocked, "COMMAND_MISMATCH"):
            self.check()


if __name__ == "__main__":
    unittest.main()
