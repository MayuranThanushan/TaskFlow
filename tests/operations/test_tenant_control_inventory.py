import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]

class TenantControlInventoryTests(unittest.TestCase):
    def test_every_current_operation_has_a_reviewed_control_and_existing_evidence_source(self):
        contract = json.loads((ROOT / 'docs/contracts/openapi.json').read_text(encoding='utf-8'))
        inventory = json.loads((ROOT / 'docs/saas/tenant-control-inventory.json').read_text(encoding='utf-8'))
        actual = {(method.upper(), path, operation['operationId']) for path, item in contract['paths'].items()
                  for method, operation in item.items() if method in {'get', 'post', 'put', 'patch', 'delete'}}
        rows = inventory['operations']
        self.assertEqual(len(rows), len(actual))
        self.assertEqual(actual, {(r['method'], r['path'], r['operationId']) for r in rows})
        for row in rows:
            self.assertTrue(row['controls'], row['operationId'])
            self.assertTrue(row['evidenceSources'], row['operationId'])
            for source in row['evidenceSources']:
                self.assertTrue((ROOT / source).is_file(), source)
        self.assertEqual({f'TEN-{i:02d}' for i in range(1, 25)}, {c['case'] for c in inventory['threatCases']})
        self.assertTrue(any('Future external delivery' in e['entry'] for e in inventory['nonHttpEntryPoints']))
