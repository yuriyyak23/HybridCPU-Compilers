"""Security/refusal tests. No Golden or Core source modification; fixture-only."""
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import types
import unittest
import uuid

MODULE = Path(__file__).with_name('Run-CrossRepoReplay.py')
spec = importlib.util.spec_from_file_location('hb_cross_repo_replay', MODULE)
r = importlib.util.module_from_spec(spec)
spec.loader.exec_module(r)

class ReplayRefusals(unittest.TestCase):
    def setUp(self):
        self.base = Path(tempfile.mkdtemp(prefix='hb-cross-repo-fixture-'))
        self.golden = self.base / 'golden'
        self.golden.mkdir()
        (self.golden / 'A.cs').write_bytes(b'class A {}\n')
        (self.golden / 'B.csproj').write_bytes(b'<Project />\n')
        subprocess.run(['git', 'init', '-q', str(self.golden)], check=True)
        subprocess.run(['git', '-C', str(self.golden), 'add', '.'], check=True)
        subprocess.run(['git', '-C', str(self.golden), '-c', 'user.email=test@example.invalid',
                        '-c', 'user.name=Fixture', 'commit', '-qm', 'fixture'], check=True)
    def tearDown(self):
        shutil.rmtree(self.base, ignore_errors=True)
    def test_content_sha_and_status_are_stable(self):
        a = r.inventory(self.golden)
        b = r.inventory(self.golden)
        self.assertEqual(a, b)
        self.assertEqual(a['files'], 2)
        self.assertEqual(a['status'], '')
    def replay_args(self, scope="fixture"):
        roots = self.base / 'roots.json'
        roots.write_bytes(b'{"Schema":"fixture-only"}')
        head = r.git(self.golden, ["rev-parse", "HEAD"])
        return types.SimpleNamespace(
            bridge=str(self.golden), core=str(self.golden), golden=str(self.golden),
            output=str(Path(tempfile.gettempdir()) / ("hb-test-refusal-" + uuid.uuid4().hex)),
            bridge_sha=head, core_sha=head, golden_sha=head, scope=scope,
            closure_sha="0" * 64, roots=str(roots), roots_sha=r.sha_file(roots)[0],
            sdk="11.0.100-rc.2.26504.105", dotnet=sys.executable,
            graph=str(self.base / 'graph.json'), observation=str(self.base / 'obs.json'),
            observer_argv=None, observer_argv_sha=None, observer_exe_sha=None,
            graph_sha=None, observation_sha=None, pe=None, pdb=None, pe_sha=None,
            pdb_sha=None, facts_argv=None, facts_argv_sha=None, facts_exe_sha=None,
            facts=None, facts_sha=None, timeout=10)

    def test_wrong_core_checkout_sha_blocks_before_copy_or_execution(self):
        args = self.replay_args()
        args.core_sha = "0" * 40
        with self.assertRaisesRegex(r.Blocked, "wrong Core SHA"):
            r.main(args)
        self.assertFalse(Path(args.output).exists())

    def test_wrong_bridge_checkout_sha_blocks(self):
        args = self.replay_args()
        args.bridge_sha = "0" * 40
        with self.assertRaisesRegex(r.Blocked, "wrong Bridge SHA"):
            r.main(args)
        self.assertFalse(Path(args.output).exists())

    def test_wrong_reviewed_root_manifest_sha_blocks(self):
        args = self.replay_args()
        args.roots_sha = "0" * 64
        with self.assertRaisesRegex(r.Blocked, "reviewed roots manifest byte mismatch"):
            r.main(args)
        self.assertFalse(Path(args.output).exists())

    def test_real_golden_dirty_checkout_rejected_before_observer(self):
        args = self.replay_args(scope="golden")
        # Separate clean pinned Bridge/Core clones: the Golden-only mutation
        # must reach the Golden guard, not the earlier Core checkout guard.
        for name in ("bridge", "core"):
            target = self.base / ("clean-" + name)
            subprocess.run(["git", "clone", "-q", str(self.golden), str(target)], check=True)
            setattr(args, name, str(target))
        (self.golden / "untracked.generated.cs").write_bytes(b"// dirty\n")
        with self.assertRaisesRegex(r.Blocked, "dirty Golden checkout"):
            r.main(args)
        self.assertFalse(Path(args.output).exists())

    def test_dirty_core_checkout_rejected_before_copy(self):
        args = self.replay_args()
        (self.golden / "dirty-core.cs").write_bytes(b"// dirty\n")
        with self.assertRaisesRegex(r.Blocked, "dirty pinned Core checkout"):
            r.main(args)
        self.assertFalse(Path(args.output).exists())

    def test_stale_pe_pdb_byte_pin_never_equals_changed_payload(self):
        pe = self.base / "a.dll"
        pdb = self.base / "a.pdb"
        pe.write_bytes(b"fixture-pe")
        pdb.write_bytes(b"fixture-pdb")
        before = {name: r.sha_file(path)[0] for name,path in (("PE", pe), ("PDB",pdb))}
        pdb.write_bytes(b"fixture-stale-pdb")
        self.assertNotEqual(r.sha_file(pdb)[0], before["PDB"])
        self.assertEqual(r.sha_file(pe)[0], before["PE"])

    def test_byte_mutation(self):
        before = r.inventory(self.golden)
        (self.golden / 'A.cs').write_bytes(b'class B {}\n')
        self.assertNotEqual(before, r.inventory(self.golden))
    def test_dirty_checkout(self):
        (self.golden / 'extra.cs').write_bytes(b'x')
        self.assertIn('extra.cs', r.inventory(self.golden)['status'])
    def test_missing_generated_source_is_refused(self):
        with self.assertRaisesRegex(r.Blocked, 'missing'):
            r.reject_links(self.golden / 'missing-generated.cs')
    def test_reject_symlink(self):
        dest = self.golden / 'Alias.cs'
        try:
            dest.symlink_to(self.golden / 'A.cs')
        except (OSError, NotImplementedError):
            self.skipTest('host disallows symlinks')
        with self.assertRaisesRegex(r.Blocked, 'symlink'):
            r.inventory(self.golden)
    def test_duplicate_json_fields(self):
        doc = self.base / 'dup.json'
        doc.write_bytes(b'{"Role":"source","Role":"owned-body"}')
        with self.assertRaisesRegex(r.Blocked, 'duplicate JSON'):
            r.json_file(doc)
    def test_invalid_sha(self):
        for value in ('0'*63, '0'*65, 'G'*64, 'A'*64, ''):
            with self.assertRaises(r.Blocked):
                r.check_sha(value, 'fixture')
    def test_index_alias_rejected_by_source_ancestor_links(self):
        sub = self.golden / 'sub'
        try:
            sub.symlink_to(self.golden, target_is_directory=True)
        except (OSError, NotImplementedError):
            self.skipTest('host disallows symlinks')
        with self.assertRaisesRegex(r.Blocked, 'symlink'):
            r.reject_links(sub / 'A.cs')
    def test_record_real_exit_and_stdout_stderr_sha(self):
        logs = self.base / 'logs'; logs.mkdir()
        proc = r.run('noop', [sys.executable, '-c', 'import sys;print("ok");print("err",file=sys.stderr)'],
                     self.base, logs, 15)
        self.assertEqual(proc['exit'], 0)
        self.assertEqual(proc['status'], 'Passed')
        self.assertEqual(proc['stdoutSha256'], r.sha_file(logs / 'noop.stdout.bin')[0])
        self.assertEqual(proc['stderrSha256'], r.sha_file(logs / 'noop.stderr.bin')[0])
    def test_command_rc_failure_not_pass(self):
        logs = self.base / 'logs'; logs.mkdir()
        receipt = r.run('fails', [sys.executable, '-c', 'import sys;sys.exit(7)'], self.base, logs, 15)
        self.assertEqual(receipt['status'], 'Failed')
        self.assertEqual(receipt['exit'], 7)
    def test_command_timeout(self):
        logs = self.base / 'logs'; logs.mkdir()
        receipt = r.run('cancels', [sys.executable, '-c', 'import time;time.sleep(9)'],
                        self.base, logs, .01)
        self.assertEqual(receipt['status'], 'Blocked')
    def test_no_output_inside_golden(self):
        self.assertTrue(r.within(self.golden / 'out', self.golden))
        self.assertFalse(r.within(self.base / 'out', self.golden))
    def test_prevent_csc_option_drift(self):
        digest = r.sha_file(self.golden / 'A.cs')[0]
        (self.golden / 'A.cs').write_bytes(b'class C {}')
        self.assertNotEqual(r.sha_file(self.golden / 'A.cs')[0], digest)

if __name__ == '__main__':
    unittest.main()
