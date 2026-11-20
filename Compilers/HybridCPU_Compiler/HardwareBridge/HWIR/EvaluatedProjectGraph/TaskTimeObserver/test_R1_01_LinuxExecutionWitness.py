"""Synthetic syscall parser mutations; they are NOT positive executed-Csc proof."""
import json
from pathlib import Path
import shutil
import tempfile
import unittest

import R1_01_LinuxExecutionWitness as w


class ExecutionWitnessMutationTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(prefix="hb-r1-01-trace-"))
        self.trace = self.tmp / "traces"
        self.trace.mkdir()
        self.app = self.tmp / "dotnet"
        self.app.write_bytes(b"toy host - never run")
        self.compiler = self.tmp / "csc"
        self.compiler.write_bytes(b"toy compiler - never run")
        self.project = self.tmp / "project"
        self.project.mkdir()
        self.argv = [str(self.compiler), "/noconfig", "/target:library", "Code.cs"]
        self.events = {"task-1": self.argv}
        self.root = '1760000000.000001 execve(' + json.dumps(str(self.app)) + ', ' + json.dumps([str(self.app), 'build']) + ', 0x7fff /* 10 vars */) = 0\n' + '1760000000.001000 clone(child_stack=NULL, flags=SIGCHLD) = 200\n' + '1760000000.010000 +++ exited with 0 +++\n'
        self.child = '1760000000.002000 chdir(' + json.dumps(str(self.project)) + ') = 0\n' + '1760000000.003000 execve(' + json.dumps(str(self.compiler)) + ', ' + json.dumps(self.argv) + ', 0x7fff /* 10 vars */) = 0\n' + '1760000000.004000 +++ exited with 0 +++\n'
        self.write()

    def tearDown(self):
        shutil.rmtree(self.tmp)

    def write(self):
        (self.trace / 'trace.100').write_text(self.root)
        (self.trace / 'trace.200').write_text(self.child)

    def get(self, **kw):
        args = {"launch_cwd": self.tmp, "launch_argv": [str(self.app), 'build'],
                "compiler": self.compiler, "event_argv": self.events,
                "expected_compiler_sha": w.digest(self.compiler)}
        args.update(kw)
        return w.verify(self.trace, **args)

    def test_os_child_cwd_is_derived_from_chdir_not_launcher(self):
        result = self.get()
        self.assertEqual(result['processes']['task-1']['cwd'], str(self.project))
        self.assertNotEqual(result['processes']['task-1']['cwd'], str(self.tmp))
        self.assertEqual(result['processes']['task-1']['exitCode'], 0)
        w.gate_receipt(result, self.trace, launch_cwd=self.tmp,
                       launch_argv=[str(self.app), 'build'], compiler=self.compiler,
                       event_argv=self.events, expected_compiler_sha=w.digest(self.compiler))

    def test_shared_fs_group_cwd_is_replayed(self):
        # A second cloned thread shares the root fs_struct. Its successful
        # chdir changes the future inherited cwd of a normal Csc fork.
        self.root = self.root.replace(
            '1760000000.001000 clone(',
            '1760000000.000500 clone(child_stack=NULL, flags=CLONE_FS|CLONE_THREAD) = 300\\n'
            '1760000000.001000 clone(').replace('\\n', '\n')
        (self.trace / 'trace.300').write_text(
            '1760000000.000700 chdir(' + json.dumps(str(self.project)) + ') = 0\n'
            '1760000000.000800 +++ exited with 0 +++\n')
        self.write()
        result = self.get()
        self.assertEqual(result['processes']['task-1']['cwd'], str(self.project))

    def test_changed_cwd_receipt_blocked(self):
        result = self.get()
        result['processes']['task-1']['cwd'] = str(self.tmp)
        with self.assertRaisesRegex(w.Blocked, 'receipt mutation'):
            w.gate_receipt(result, self.trace, launch_cwd=self.tmp,
                           launch_argv=[str(self.app), 'build'], compiler=self.compiler,
                           event_argv=self.events, expected_compiler_sha=w.digest(self.compiler))

    def test_missing_fork_cannot_claim_launcher_cwd(self):
        self.root = self.root.replace('1760000000.001000 clone(child_stack=NULL, flags=SIGCHLD) = 200\n', '')
        self.write()
        with self.assertRaisesRegex(w.Blocked, 'parent'):
            self.get()

    def test_wrong_compiler_binary_pin(self):
        with self.assertRaisesRegex(w.Blocked, 'SHA'):
            self.get(expected_compiler_sha='0' * 64)

    def test_response_file_refused_without_live_bytes(self):
        self.argv.append('@missing.rsp')
        self.child = self.child.replace('/target:library", "Code.cs"]', '/target:library", "Code.cs", "@missing.rsp"]')
        self.write()
        with self.assertRaisesRegex(w.Blocked, 'response-file'):
            self.get()

    def test_duplicated_csc_process_refused(self):
        self.root = self.root.replace('1760000000.010000 +++ exited', '1760000000.001100 clone(child_stack=NULL, flags=SIGCHLD) = 300\n1760000000.010000 +++ exited')
        (self.trace / 'trace.300').write_text(
            self.child.replace('1760000000.002000', '1760000000.002100')
                      .replace('1760000000.003000', '1760000000.003100')
                      .replace('1760000000.004000', '1760000000.004100'))
        self.write()
        with self.assertRaisesRegex(w.Blocked, 'process count'):
            self.get()

    def test_event_task_argv_mismatch(self):
        with self.assertRaisesRegex(w.Blocked, 'argv / MSBuild'):
            self.get(event_argv={'task-1': [str(self.compiler), '/noconfig', '/changed']})

    def test_unparseable_utf8_or_quote_mutation(self):
        self.child = self.child.replace('"Code.cs"', '"Code\\xFF.cs"')
        self.write()
        with self.assertRaisesRegex(w.Blocked, 'unparseable'):
            self.get()

    def test_unsupported_fd_cwd_mutation(self):
        self.child = self.child.replace('chdir(', 'fchdir(')
        self.write()
        with self.assertRaisesRegex(w.Blocked, 'fchdir'):
            self.get()

    def test_symlink_trace_is_blocked(self):
        file = self.trace / 'trace.200'
        other = self.tmp / 'backup'
        file.rename(other)
        try:
            file.symlink_to(other)
        except OSError:
            self.skipTest('symlink privilege missing')
        with self.assertRaisesRegex(w.Blocked, 'symlink'):
            self.get()

    def test_pdb_pe_copy_mutation(self):
        pe, pdb = self.tmp / 'a.dll', self.tmp / 'a.pdb'
        pe.write_bytes(b'original PE')
        pdb.write_bytes(b'original PDB')
        pins = [{"kind": kind, "path": str(f), "sha256": w.digest(f), "bytes": f.stat().st_size}
                for kind, f in [('pe', pe), ('pdb', pdb)]]
        w.verify_output_pins(pins)
        pe.write_bytes(b'copied fake PE')
        with self.assertRaisesRegex(w.Blocked, 'drift'):
            w.verify_output_pins(pins)

    def test_missing_pdb_blocked(self):
        pe = self.tmp / 'a.dll'
        pe.write_bytes(b'PE')
        with self.assertRaisesRegex(w.Blocked, 'both PE and PDB'):
            w.verify_output_pins([{"kind": 'pe', 'path': str(pe),
                                    'sha256': w.digest(pe), 'bytes': 2}])


if __name__ == '__main__':
    unittest.main()
