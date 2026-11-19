#!/usr/bin/env python3
"""Fail if any opt-in legacy projection was omitted or changed after catalog capture."""
import hashlib
import json
from pathlib import Path
import re
import unittest

HWIR = Path(__file__).resolve().parents[1]
INDEX = HWIR / 'Migration' / 'projection-inventory.v1.json'


class ProjectionInventoryRegression(unittest.TestCase):
    def test_all_bridge_projection_blobs_are_exactly_captured(self):
        report = json.loads(INDEX.read_text('utf8'))
        self.assertEqual('hybridcpu.hardwarebridge.hb-int-05.projection-inventory/v1', report['schema'])
        self.assertEqual('report-only-incomplete', report['status'])
        self.assertRegex(report['sourceHead'], r'^[0-9a-f]{40}$')
        found = sorted(path for path in HWIR.glob('BridgeHwir*.cs') if path.is_file())
        entries = report['entries']
        self.assertEqual(len(found), report['entriesCount'])
        self.assertEqual([p.name for p in found], [e['file'] for e in entries])
        for path, entry in zip(found, entries):
            with self.subTest(file=path.name):
                before = path.stat()
                data = path.read_bytes()
                after = path.stat()
                self.assertEqual((before.st_size, before.st_mtime_ns), (after.st_size, after.st_mtime_ns))
                self.assertEqual(len(data), entry['bridgeCodeBytes'])
                git_blob = b'blob ' + str(len(data)).encode('ascii') + b'\x00' + data
                self.assertEqual(hashlib.sha1(git_blob).hexdigest(), entry['bridgeCodeGitBlobSha1'])
                self.assertEqual('NotEstablished', entry['nativeReceiptAgainstCurrentSource'])
                self.assertEqual('Blocked:CW-INT-06', entry['genericCoreParity'])
                self.assertEqual('Blocked:CW-INT-13', entry['statefulCoreParity'])
                self.assertIsNone(entry['goldenVectorCoverage'])


if __name__ == '__main__':
    unittest.main(verbosity=2)
