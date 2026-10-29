import contextlib
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts'))
import export_security_events as export


class SecurityExportTests(unittest.TestCase):
    def test_untrusted_schema_cursor_and_unbounded_batches_are_rejected(self):
        for args in [('db;DROP TABLE Users', 0, 10), ('db', -1, 10), ('db', 0, 1001), ('db', 9223372036854775808, 10)]:
            with self.assertRaises(ValueError):
                export.query(*args)

    def test_exports_cannot_overwrite_or_enter_web_and_source_directories(self):
        with self.assertRaises(ValueError):
            export.destination(export.ROOT / 'docs' / 'events.ndjson')
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            (folder / 'wwwroot').mkdir()
            with self.assertRaises(ValueError):
                export.destination(folder / 'wwwroot' / 'events.ndjson')
            existing = folder / 'existing.ndjson'
            existing.write_text('retained', encoding='utf-8')
            with self.assertRaises(ValueError):
                export.destination(existing)
            self.assertEqual(existing.read_text(encoding='utf-8'), 'retained')

    def test_read_only_export_preserves_opaque_ids_and_ndjson(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory); credentials = folder / 'reader.cnf'; credentials.write_text('# test fixture', encoding='utf-8')
            output = folder / 'events.ndjson'
            row = {'id': '9007199254741999', 'schemaVersion': 1, 'action': 'Login', 'outcome': 'Denied', 'subjectUserId': None}
            completed = subprocess.CompletedProcess([], 0, (json.dumps(row) + '\n').encode(), b'')
            args = ['export', '--mysql', 'mysql', '--defaults-file', str(credentials), '--schema', 'taskflow_test_export', '--output', str(output)]
            with patch.object(sys, 'argv', args), patch.object(export.subprocess, 'run', return_value=completed) as run, contextlib.redirect_stdout(io.StringIO()):
                export.main()
            self.assertEqual(json.loads(output.read_text(encoding='utf-8')), row)
            command = run.call_args.args[0]
            self.assertIn('SELECT JSON_OBJECT', command[-1])
            self.assertIn('LIMIT 100', command[-1])
            self.assertFalse(run.call_args.kwargs.get('shell', False))

    def test_private_mysql_errors_are_not_disclosed(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory); credentials = folder / 'reader.cnf'; credentials.write_text('# fixture', encoding='utf-8')
            args = ['export', '--mysql', 'mysql', '--defaults-file', str(credentials), '--schema', 'test', '--output', str(folder / 'events.ndjson')]
            stderr = io.StringIO()
            with patch.object(sys, 'argv', args), patch.object(export.subprocess, 'run', return_value=subprocess.CompletedProcess([], 1, b'', b'PRIVATE_SERVER_CANARY')), contextlib.redirect_stderr(stderr), self.assertRaises(SystemExit):
                export.main()
            self.assertNotIn('PRIVATE_SERVER_CANARY', stderr.getvalue())


if __name__ == '__main__':
    unittest.main()
