import argparse
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'scripts'))
import backup_recovery as recovery
import release_manifest as release

class BackupSafetyTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name); self.backup = self.root / 'backup'; self.backup.mkdir(); (self.backup / 'keys').mkdir()
        (self.backup / 'database.sql').write_bytes(b'CREATE TABLE example (id BIGINT);')
        (self.backup / 'keys/key-example.xml').write_bytes(b'<key />')
        self.manifest = {'formatVersion': 1, 'serverSettings': {'lowerCaseTableNames': 1}, 'sourceSchema': 'taskflow_test_source', 'tableCounts': {'AspNetUsers': 1, '__EFMigrationsHistory': 1}, 'migrations': ['Initial'], 'files': {name: recovery.digest(self.backup / name) for name in ['database.sql', 'keys/key-example.xml']}}
        self.save()
    def save(self):
        (self.backup / 'manifest.json').write_text(json.dumps(self.manifest), encoding='utf-8')
    def args(self, **changes):
        return argparse.Namespace(backup=str(self.backup), target_schema='taskflow_test_restore_new', keys_destination=str(self.root / 'restored-keys'), **changes)
    def test_valid_backup_verifies_without_a_database_connection(self):
        self.assertEqual(recovery.verify(self.backup)['tableCounts']['AspNetUsers'], 1)
    def test_tampered_sql_or_key_is_rejected(self):
        for name in ['database.sql', 'keys/key-example.xml']:
            with self.subTest(name=name):
                path = self.backup / name; data = path.read_bytes(); path.write_bytes(b'changed')
                with self.assertRaises(recovery.RecoveryError): recovery.verify(self.backup)
                path.write_bytes(data)
    def test_missing_keys_and_unexpected_payload_are_rejected(self):
        (self.backup / 'secret.txt').write_text('must not be included')
        with self.assertRaises(recovery.RecoveryError): recovery.verify(self.backup)
        (self.backup / 'secret.txt').unlink(); self.manifest['files'].pop('keys/key-example.xml'); self.save()
        with self.assertRaises(recovery.RecoveryError): recovery.verify(self.backup)
    def test_path_traversal_and_absolute_manifest_paths_are_rejected(self):
        for name in ['../outside.sql', '/outside.sql', 'C:/outside.sql', 'keys/../../outside.xml', 'keys\\outside.xml']:
            with self.subTest(name=name), self.assertRaises(recovery.RecoveryError): recovery.safe_file(self.backup, name)
    def test_restore_rejects_unsafe_or_source_targets_before_database_access(self):
        for target in ['taskflow', 'mysql', 'taskflow_test_source', 'taskflow_restore_x;DROP DATABASE mysql']:
            args = self.args(); args.target_schema = target
            with patch.object(recovery, 'query') as query, self.assertRaises(recovery.RecoveryError): recovery.restore(args)
            query.assert_not_called()
    def test_restore_rejects_incompatible_table_name_case_mode_before_creation(self):
        with patch.object(recovery, 'query', return_value=['0']) as query, self.assertRaises(recovery.RecoveryError): recovery.restore(self.args())
        self.assertEqual(query.call_count, 1); self.assertFalse((self.root / 'restored-keys').exists())
    def test_existing_restore_database_cannot_be_overwritten(self):
        def occupied(_args, sql):
            if sql == 'SELECT @@lower_case_table_names': return ['1']
            self.assertTrue(sql.startswith('CREATE DATABASE ')); self.assertNotIn('IF NOT EXISTS', sql)
            raise recovery.RecoveryError('Already exists')
        with patch.object(recovery, 'query', side_effect=occupied) as query, patch.object(recovery.subprocess, 'run') as run, self.assertRaises(recovery.RecoveryError): recovery.restore(self.args())
        self.assertEqual(query.call_count, 2); run.assert_not_called(); self.assertFalse((self.root / 'restored-keys').exists())
    def test_existing_key_destination_rejected_before_database_creation(self):
        (self.root / 'restored-keys').mkdir()
        with patch.object(recovery, 'query') as query, self.assertRaises(recovery.RecoveryError): recovery.restore(self.args())
        query.assert_not_called()
    def test_import_failure_retains_isolated_target_without_publishing_keys(self):
        (self.root / 'client.cnf').write_text('[client]')
        with patch.object(recovery, 'query', return_value=['1']) as query, patch.object(recovery.subprocess, 'run', return_value=argparse.Namespace(returncode=1)), self.assertRaises(recovery.RecoveryError): recovery.restore(self.args(mysql='mysql', defaults_file=str(self.root / 'client.cnf')))
        self.assertFalse((self.root / 'restored-keys').exists())
    def test_restore_checks_counts_before_rotating_stamps_and_restoring_keys(self):
        args = self.args(mysql='mysql', defaults_file=str(self.root / 'client.cnf')); Path(args.defaults_file).write_text('[client]')
        with patch.object(recovery, 'query', return_value=['1']) as query, patch.object(recovery.subprocess, 'run', return_value=argparse.Namespace(returncode=0)), patch.object(recovery, 'schema_state', return_value={'tableCounts': {}, 'migrations': []}), self.assertRaises(recovery.RecoveryError): recovery.restore(args)
        self.assertEqual(query.call_count, 2); self.assertFalse((self.root / 'restored-keys').exists())
    def test_successful_restore_invalidates_prior_sessions_and_restores_key_bytes(self):
        args = self.args(mysql='mysql', defaults_file=str(self.root / 'client.cnf')); Path(args.defaults_file).write_text('[client]')
        with patch.object(recovery, 'query', return_value=['1']) as query, patch.object(recovery.subprocess, 'run', return_value=argparse.Namespace(returncode=0)), patch.object(recovery, 'schema_state', return_value={k:self.manifest[k] for k in ['tableCounts','migrations']}): recovery.restore(args)
        self.assertIn('SecurityStamp=UUID()', query.call_args.args[1]); self.assertEqual((self.root / 'restored-keys/key-example.xml').read_bytes(), b'<key />')
    def test_backup_requires_quiesced_writers_before_database_access(self):
        args = argparse.Namespace(schema='taskflow_test_source', maintenance_confirmed=False)
        with patch.object(recovery, 'query') as query, self.assertRaises(recovery.RecoveryError): recovery.backup(args)
        query.assert_not_called()
    def test_private_outputs_are_never_overwritten(self):
        path = self.root / 'private'; recovery.private_write(path, b'original')
        with self.assertRaises(FileExistsError): recovery.private_write(path, b'new')
        self.assertEqual(path.read_bytes(), b'original')

class ReleaseReceiptTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name); (self.root / 'app.dll').write_bytes(b'binary')
        release.create(self.root, ROOT)
    def test_receipt_binds_source_migrations_and_payload(self):
        manifest = release.verify(self.root)
        self.assertRegex(manifest['sourceRevision'], r'^[a-f0-9]{40}$'); self.assertTrue(manifest['migrations']); self.assertIn('app.dll', manifest['files'])
    def test_modified_missing_or_added_files_fail_verification(self):
        (self.root / 'app.dll').write_bytes(b'changed')
        with self.assertRaises(recovery.RecoveryError): release.verify(self.root)
        (self.root / 'app.dll').write_bytes(b'binary'); (self.root / 'extra').write_bytes(b'extra')
        with self.assertRaises(recovery.RecoveryError): release.verify(self.root)
        (self.root / 'extra').unlink(); (self.root / 'app.dll').unlink()
        with self.assertRaises(recovery.RecoveryError): release.verify(self.root)
    def test_manifest_cannot_reference_an_external_payload(self):
        path = self.root / release.MANIFEST; manifest = json.loads(path.read_text()); manifest['files']['../escape'] = 'a' * 64; path.write_text(json.dumps(manifest))
        with self.assertRaises(recovery.RecoveryError): release.verify(self.root)

if __name__ == '__main__': unittest.main()
