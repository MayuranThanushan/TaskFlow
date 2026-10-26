import copy
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts'))
from pilot_load import budget_failures, db_snapshot, operation, origin_url, percentile, summarize, validate_config


class PilotLoadTests(unittest.TestCase):
    def setUp(self):
        self.config = json.loads((Path(__file__).parents[1] / 'load/local-baseline.json').read_text(encoding='utf-8'))

    def test_target_rejects_insecure_credentialed_and_implicit_remote_origins(self):
        self.assertEqual(origin_url('https://localhost:15173/'), 'https://localhost:15173')
        self.assertEqual(origin_url('https://staging.example.test', True), 'https://staging.example.test')
        for target in ['http://localhost', 'https://user:secret@localhost', 'https://localhost/path',
                       'https://localhost?secret=x', 'https://localhost#x', 'https://production.example.test']:
            with self.subTest(target=target), self.assertRaises(ValueError): origin_url(target)

    def test_fixture_rejects_unbounded_work_and_invalid_budgets(self):
        validate_config(self.config)
        for key, value in [('concurrency', 0), ('concurrency', 51), ('requests', 100001),
                           ('requests', 8), ('tasksPerProject', True), ('groups', 1), ('timeoutSeconds', 61)]:
            config = copy.deepcopy(self.config); config[key] = value
            with self.subTest(key=key, value=value), self.assertRaises(ValueError): validate_config(config)
        for invalid in [float('nan'), float('inf'), True, 0, -1]:
            config = copy.deepcopy(self.config); config['proposedBudgets']['readP95Milliseconds'] = invalid
            with self.subTest(value=invalid), self.assertRaises(ValueError): validate_config(config)

    def test_nearest_rank_latency_includes_failed_and_timed_out_requests(self):
        self.assertIsNone(percentile([], .95))
        self.assertEqual(percentile(list(range(1, 101)), .95), 95)
        report = summarize([('dashboard', 200, 10), ('dashboard', 500, 1000), ('task_status', 0, 10000)])
        self.assertEqual(report['all']['unexpected'], 2)
        self.assertEqual(report['all']['p95Milliseconds'], 10000)
        self.assertEqual(report['all']['statusCounts'], {'0': 1, '200': 1, '500': 1})
        self.assertEqual(set(budget_failures(report, self.config['proposedBudgets'])),
                         {'unexpectedResponseRate', 'dashboard.p95', 'task_status.p95'})

    def test_route_mix_has_real_filtered_reads_and_hot_parent_writes(self):
        group = {'projects': [{'id': '9007199254741111', 'tasks': ['9007199254741999', '9007199254742000']}]}
        mix = [operation(group, i) for i in range(16)]
        self.assertEqual(sum(row[1] == 'PATCH' for row in mix), 2)
        self.assertIn('status=InProgress&priority=Medium', mix[2][0])
        self.assertEqual(mix[6][2]['status'], 'ToDo')
        self.assertEqual(mix[14][2]['status'], 'InProgress')
        self.assertIn('9007199254741999', mix[6][0])

    def test_database_metrics_require_complete_real_snapshots(self):
        self.assertIsNone(db_snapshot(None, None))
        with self.assertRaises(ValueError): db_snapshot('mysql', None)
        with patch('pilot_load.subprocess.run') as process:
            process.return_value.returncode = 0
            process.return_value.stdout = 'Innodb_row_lock_waits\t2\nInnodb_row_lock_time\t3\nlock_deadlocks\t0\n'
            self.assertEqual(db_snapshot('mysql', 'private.cnf')['Innodb_row_lock_time'], 3)
            process.return_value.stdout = 'Innodb_row_lock_waits\t0\n'
            with self.assertRaises(ValueError): db_snapshot('mysql', 'private.cnf')
            process.return_value.returncode = 1
            with self.assertRaisesRegex(ValueError, 'withheld'): db_snapshot('mysql', 'private.cnf')


if __name__ == '__main__':
    unittest.main()
