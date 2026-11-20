#!/usr/bin/env python3
"""Create reviewed-for-this-test-only 1001-file *synthetic* Csc observation.

No Csc command is launched. This fixture MUST NOT be reported as Golden execution.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

EVAL = Path(__file__).resolve().parents[2] / 'HWIR' / 'EvaluatedProjectGraph'
sys.path.insert(0, str(EVAL))
from test_segmented_csc_capture_v2 import ClosureTests
import segmented_csc_capture_v2 as c
import stage_core_v2 as stage


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--result', required=True)
    p.add_argument('--count', type=int, default=1001)
    a = p.parse_args()
    if a.count < 1001 or a.count > 1500:
        raise ValueError('synthetic fixture must contain 1001..1500 source rows')
    f = ClosureTests('test_positive_capture_and_byte_replay')
    f.setUp()
    pr = f.observation['projects'][0]
    for i in range(a.count - 1):
        original = f.golden / f'B{i:05d}.cs'
        copied = f.copy / original.name
        original.write_bytes(f'// synthetic source {i:05d}\n'.encode())
        copied.write_bytes(original.read_bytes())
        f.graph['nodes'][0]['compileFiles'].append(str(original))
        pr['argv'].append(str(copied))
        pr['sources'].append({'path': str(copied), 'logicalPath': 'source/' + original.name,
                              'sha256': c.sha(copied.read_bytes()), 'origin': 'evaluated',
                              'originalPath': str(original)})
    f.save()
    root = f.golden
    subprocess.run(['git', 'init', '-q', str(root)], check=True)
    subprocess.run(['git', '-C', str(root), 'add', '.'], check=True)
    subprocess.run(['git', '-C', str(root), '-c', 'user.email=fixture@example.invalid',
                    '-c', 'user.name=Fixture', 'commit', '-qm', 'synthetic fixture'], check=True)
    # Independent review is NOT claimed. This pre-stage pin only tests replay.
    bundle, expected = f.run_capture()
    roots = f.tmp / 'roots-for-core.json'
    roots.write_bytes(c.canonical({'Schema': stage.ROOTS, 'Roots': [{
        'TypeName': 'Fixture', 'MethodName': 'F', 'MetadataToken': None,
        'TypeArguments': [], 'MethodArguments': []}]}))
    receipt = {'scope': 'fixture', 'golden': str(root),
               'golden_sha': subprocess.check_output(['git', '-C', str(root), 'rev-parse', 'HEAD'], text=True).strip(),
               'graph': str(f.graph_path), 'observation': str(f.observation_path),
               'roots': str(roots), 'roots_sha': c.sha(roots.read_bytes()),
               'closure_sha': expected, 'source_count': a.count,
               'review_type': 'fixture-derived-not-independent'}
    Path(a.result).write_text(json.dumps(receipt, indent=2) + '\n')
    print('FIXTURE ONLY source_count=', a.count,
          ' preflight_capture_closure_sha=', expected, sep='')

if __name__ == '__main__':
    main()
