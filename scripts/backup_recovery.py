"""Private logical backups and fresh-target recovery for the TaskFlow MySQL 8.4 schema.
Credentials belong in an operator-restricted MySQL defaults file, never command arguments.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys

class RecoveryError(Exception):
    pass

def identifier(value):
    if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]{0,63}", value) or value.lower() in {'mysql', 'sys', 'information_schema', 'performance_schema'}:
        raise RecoveryError('Invalid or reserved schema/table identifier.')
    return '`' + value + '`'

def private_directory(path):
    path = Path(path).absolute()
    if path.is_symlink() or path.is_junction() or not path.parent.is_dir():
        raise RecoveryError('Use a fresh directory under an existing private parent.')
    path.mkdir(mode=0o700)  # Deliberately fails if anything already exists.
    return path

def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()

def private_write(path, data):
    with os.fdopen(os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), 'wb') as stream:
        stream.write(data)

def client(args, executable=None):
    config = Path(args.defaults_file).resolve(strict=True)
    return [executable or args.mysql, '--defaults-file=' + str(config), '--no-login-paths']

def query(args, sql):
    result = subprocess.run(client(args) + ['--batch', '--skip-column-names', '--raw', '--execute', sql], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    if result.returncode:
        code = re.search(rb'ERROR ([0-9]+)', result.stderr)
        raise RecoveryError('MySQL operation failed' + (' (code ' + code.group(1).decode() + ')' if code else '') + '; private server diagnostics are withheld.')
    return result.stdout.decode('utf-8').splitlines()

def schema_state(args, schema):
    db = identifier(schema)
    rows = query(args, f"SELECT TABLE_NAME, TABLE_TYPE, COALESCE(ENGINE,'') FROM information_schema.TABLES WHERE TABLE_SCHEMA='{schema}' ORDER BY TABLE_NAME")
    if not rows:
        raise RecoveryError('The source schema is empty or inaccessible.')
    counts = {}
    for row in rows:
        name, kind, engine = row.split('\t')
        if kind != 'BASE TABLE' or engine != 'InnoDB':
            raise RecoveryError('Only the current InnoDB base-table schema is supported.')
        counts[name] = int(query(args, f'SELECT COUNT(*) FROM {db}.{identifier(name)}')[0])
    if not {'__efmigrationshistory', 'aspnetusers'}.issubset({name.lower() for name in counts}):
        raise RecoveryError('Expected TaskFlow migration/account tables are missing.')
    migrations = query(args, f'SELECT MigrationId FROM {db}.`__EFMigrationsHistory` ORDER BY MigrationId')
    return {'tableCounts': counts, 'migrations': migrations}

def safe_file(root, name):
    parts = PurePosixPath(name)
    if parts.is_absolute() or '..' in parts.parts or '\\' in name or ':' in name or str(parts) != name:
        raise RecoveryError('Unsafe file path in backup manifest.')
    path = root.joinpath(*parts.parts)
    if not path.is_file() or any(p.is_symlink() or p.is_junction() for p in [path, *path.parents] if p != root.parent):
        raise RecoveryError('Missing, linked or invalid backup file.')
    if not path.resolve().is_relative_to(root.resolve()):
        raise RecoveryError('Backup file escapes its root.')
    return path

def verify(directory):
    root = Path(directory).resolve(strict=True)
    if Path(directory).is_symlink() or Path(directory).is_junction():
        raise RecoveryError('Linked backup roots are not supported.')
    manifest = json.loads((root / 'manifest.json').read_text(encoding='utf-8'))
    if manifest.get('formatVersion') != 1 or not isinstance(manifest.get('files'), dict):
        raise RecoveryError('Unsupported or incomplete backup manifest.')
    files = manifest['files']
    if 'database.sql' not in files or not any(name.startswith('keys/') for name in files):
        raise RecoveryError('The backup needs both its database and protection keys.')
    for name, expected in files.items():
        if not re.fullmatch(r'[a-f0-9]{64}', expected) or digest(safe_file(root, name)) != expected:
            raise RecoveryError('Backup checksum verification failed.')
        if name != 'database.sql' and not re.fullmatch(r'keys/[^/]+\.xml', name):
            raise RecoveryError('Unexpected backup payload.')
    actual = {p.relative_to(root).as_posix() for p in root.rglob('*') if p.is_file()}
    if actual != set(files) | {'manifest.json'}:
        raise RecoveryError('Unexpected files in the backup directory.')
    identifier(manifest['sourceSchema'])
    return manifest

def backup(args):
    identifier(args.schema)
    if not args.maintenance_confirmed:
        raise RecoveryError('Stop all writers and migrations, then pass --maintenance-confirmed.')
    keys = Path(args.keys).resolve(strict=True)
    if Path(args.keys).is_symlink() or Path(args.keys).is_junction():
        raise RecoveryError('Use the physical protection-key directory.')
    key_files = sorted(keys.glob('*.xml'))
    if not key_files or any(p.is_symlink() or not p.is_file() for p in key_files):
        raise RecoveryError('A nonempty physical XML protection-key ring is required.')
    # Current application migrations use no triggers, routines, events or views.
    unsupported = query(args, f"SELECT (SELECT COUNT(*) FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA='{args.schema}') + (SELECT COUNT(*) FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA='{args.schema}') + (SELECT COUNT(*) FROM information_schema.EVENTS WHERE EVENT_SCHEMA='{args.schema}')")
    if unsupported != ['0']:
        raise RecoveryError('Custom triggers/routines/events need a separately reviewed backup procedure.')
    state = schema_state(args, args.schema)
    output = private_directory(args.output)
    key_output = private_directory(output / 'keys')
    with os.fdopen(os.open(output / 'database.sql', os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), 'wb') as stream:
        result = subprocess.run(client(args, args.mysqldump) + ['--single-transaction', '--quick', '--no-tablespaces', '--set-gtid-purged=OFF', '--column-statistics=0', '--hex-blob', '--default-character-set=utf8mb4', '--skip-triggers', '--skip-add-locks', '--skip-comments', '--skip-dump-date', '--skip-lock-tables', args.schema], stdout=stream, stderr=subprocess.DEVNULL)
    if result.returncode:
        raise RecoveryError('Dump failed; incomplete output is retained without a completion manifest.')
    for source in key_files:
        private_write(key_output / source.name, source.read_bytes())
    files = {p.relative_to(output).as_posix(): digest(p) for p in sorted(output.rglob('*')) if p.is_file()}
    manifest = {'formatVersion': 1, 'createdAtUtc': datetime.now(timezone.utc).isoformat(), 'sourceSchema': args.schema, 'serverVersion': query(args, 'SELECT VERSION()')[0], 'serverSettings': {'lowerCaseTableNames': int(query(args, 'SELECT @@lower_case_table_names')[0])}, 'releaseId': args.release_id, **state, 'files': files}
    private_write(output / 'manifest.json', (json.dumps(manifest, indent=2) + '\n').encode('utf-8'))
    verify(output)
    print('Backup complete; checksums verified. Treat the entire directory as sensitive.')

def restore(args):
    manifest = verify(args.backup)
    target = args.target_schema
    identifier(target)
    if not re.fullmatch(r'taskflow_(?:test_)?restore_[A-Za-z0-9_]+', target) or target == manifest['sourceSchema']:
        raise RecoveryError('Recovery requires a new taskflow_restore_* or taskflow_test_restore_* schema.')
    key_target = Path(args.keys_destination).absolute()
    if key_target.exists() or key_target.is_symlink() or key_target.is_junction() or not key_target.parent.is_dir():
        raise RecoveryError('The restored key directory must not exist; its private parent must exist.')
    if int(query(args, 'SELECT @@lower_case_table_names')[0]) != manifest['serverSettings']['lowerCaseTableNames']:
        raise RecoveryError('Source/target table-name case modes differ; use a matching MySQL server.')
    # No IF NOT EXISTS: a second run or an occupied destination fails before importing SQL.
    query(args, f'CREATE DATABASE {identifier(target)} CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci')
    with (Path(args.backup) / 'database.sql').open('rb') as stream:
        result = subprocess.run(client(args) + ['--binary-mode=1', '--default-character-set=utf8mb4', '--database=' + target], stdin=stream, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    if result.returncode:
        raise RecoveryError('Import failed. Leave the new schema isolated for investigation; do not start the app.')
    if schema_state(args, target) != {'tableCounts': manifest['tableCounts'], 'migrations': manifest['migrations']}:
        raise RecoveryError('Restored table counts or migration history differ; do not start the app.')
    # An older snapshot could otherwise revive sessions revoked since its creation.
    query(args, f'UPDATE {identifier(target)}.`AspNetUsers` SET SecurityStamp=UUID(), ConcurrencyStamp=UUID(), UpdatedAtUtc=UTC_TIMESTAMP(6)')
    private_directory(key_target)
    for name in manifest['files']:
        if name.startswith('keys/'):
            private_write(key_target / PurePosixPath(name).name, safe_file(Path(args.backup), name).read_bytes())
    print('Recovery complete in a new schema; row counts/history match, keys restored and all prior sessions invalidated.')

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    for name in ['backup', 'restore']:
        item = commands.add_parser(name)
        item.add_argument('--mysql', default='mysql')
        item.add_argument('--defaults-file', required=True, help='Operator-restricted MySQL client configuration; never upload it.')
        if name == 'backup':
            item.add_argument('--mysqldump', default='mysqldump'); item.add_argument('--schema', required=True)
            item.add_argument('--keys', required=True); item.add_argument('--output', required=True)
            item.add_argument('--release-id', required=True); item.add_argument('--maintenance-confirmed', action='store_true')
        else:
            item.add_argument('--backup', required=True); item.add_argument('--target-schema', required=True)
            item.add_argument('--keys-destination', required=True)
    item = commands.add_parser('verify'); item.add_argument('backup')
    args = parser.parse_args()
    try:
        if args.command == 'backup': backup(args)
        elif args.command == 'restore': restore(args)
        else: verify(args.backup); print('Backup checksums and payload layout verified.')
    except (RecoveryError, OSError, ValueError, KeyError, TypeError) as error:
        # Neither database-client diagnostics nor file payloads belong in logs.
        print('Backup/recovery failed: ' + (str(error) if isinstance(error, RecoveryError) else 'Invalid configuration, manifest or filesystem operation.'), file=sys.stderr)
        return 1
    return 0

if __name__ == '__main__':
    sys.exit(main())
