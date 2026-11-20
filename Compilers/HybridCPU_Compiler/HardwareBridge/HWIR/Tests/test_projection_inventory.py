#!/usr/bin/env python3
"""R3-03: baseline pinning checks; never a passing HWIR semantic equivalence test."""
import importlib.util
from pathlib import Path
import unittest

HWIR = Path(__file__).resolve().parents[1]
SCRIPT = HWIR / 'Migration' / 'r3_03_inventory_gate.py'
spec = importlib.util.spec_from_file_location('r3_03_inventory_gate', SCRIPT)
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


class ProjectionInventoryRegression(unittest.TestCase):
    def test_all_60_current_blobs_and_historical_git_are_pinned(self):
        report = gate.audit(HWIR, require_history=True)
        self.assertEqual((60, 20), (report['files'], report['reviewedDeltas']))
        self.assertTrue(report['historicalGitVerified'])
        self.assertEqual(('Blocked', 'Blocked', 'NotRun'),
                         (report['status'], report['semanticEquivalence'], report['native']))

    def test_git_blob_encoding_is_not_plain_sha_or_file_size(self):
        data = b'line1\nline2\n'
        self.assertNotEqual(__import__('hashlib').sha1(data).hexdigest(), gate.git_blob(data))
        self.assertNotEqual(gate.git_blob(data), gate.git_blob(data + b'\r\n'))

    def test_no_verified_or_rtl_promotion_in_reviewed_matrix(self):
        inventory = gate.strict_json(HWIR / 'Migration' / 'r3_03_projection-review-inventory.v2.json')
        for item in inventory['entries']:
            with self.subTest(file=item['file']):
                self.assertIsNone(item['exactGoldenSourceIdentity'])
                self.assertEqual('Blocked', item['semanticEquivalence'])
                self.assertEqual('NotRun', item['nativeReceipt'])
                self.assertEqual('Blocked', item['processorAcceptance'])


if __name__ == '__main__':
    unittest.main(verbosity=2)
