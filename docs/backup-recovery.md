# Private backups and fresh-target recovery

Use Python 3.12+ and MySQL 8.4 mysql/mysqldump clients. The [operations tool](../scripts/backup_recovery.py) backs up the current InnoDB base-table schema and XML Data Protection key ring. It does not back up MySQL accounts/grants, host configuration, TLS secrets or external key-encryption certificates. Custom views/triggers/routines/events need a separately reviewed backup procedure; they are not part of the current application schema.

## Private storage and credentials

Prepare a private parent directory outside source control. On Linux use an operator-owned 0700 directory and 0600 client files. On Windows apply a restrictive ACL to the parent before using the tool; POSIX chmod modes do not establish Windows ACLs. The local rehearsal used an explicitly restricted Windows ACL. The tool creates new output directories/private files and refuses existing outputs, but the caller owns parent ACLs, encryption and remote storage policy.

Store MySQL client configuration in that parent, never in command arguments:

```ini
[client]
host=<private-database-host>
port=3306
user=taskflow_backup
password=<generated-backup-password>
ssl-mode=VERIFY_IDENTITY
ssl-ca=/private/path/to/database-ca.pem
```

Run these tools from an operator host/job with private database reachability and a mounted physical key ring. A Compose deployment keeps MySQL unpublished: use an approved operations job on its private network with a read-only key-volume mount, rather than exposing a public database port. Container-specific execution still needs host verification.

Use a different recovery-operator defaults file for the new destination. Never put either file, a SQL dump or a key ring into source control, an image, ordinary logs or public CI artifacts. SQL dumps contain account credential hashes and private user data; key files contain cryptographic secrets. This tool writes a private plaintext logical backup, not an encrypted archive. Use encrypted storage and the selected host's approved encrypted off-host transfer/backup system. Keep decryption keys separately; verify access and alerts.

## Take a consistent backup

Stop every app instance/writer and migrations. Keep that maintenance window in place until backup and checksums complete, so row counts and the database/key snapshot describe the same inactive state. The confirmation flag records this operator action; it cannot enforce maintenance remotely.

```sh
python3 scripts/backup_recovery.py backup \
  --defaults-file /private/taskflow/backup-client.cnf \
  --schema taskflow \
  --keys /private/taskflow/protection-keys \
  --output /private/backups/taskflow-unique-backup-id \
  --release-id reviewed-release-id \
  --maintenance-confirmed
python3 scripts/backup_recovery.py verify /private/backups/taskflow-unique-backup-id
```

Set --mysql/--mysqldump to absolute executable paths if they are not on PATH. The tool uses single-transaction/quick dumping, disables table locks, GTID-purged changes, tablespace output and restore-time add-locks, and preserves binary values. It includes soft-deleted rows and internal history: no application query filters apply. A completed manifest records source release ID, server version/table-case mode, migration IDs, table row counts and SHA-256 hashes for database.sql and each key XML. Failed dumps have no completion manifest and are not usable backups. Existing files are never overwritten.

The [MySQL mysqldump documentation](https://dev.mysql.com/doc/refman/8.4/en/mysqldump.html) explains snapshot limitations under concurrent DDL; this runbook prevents such DDL/writes instead of assuming they are safe. Explicit key-file persistence does not itself encrypt XML at rest, and losing keys invalidates protected state; see [ASP.NET Core Data Protection defaults](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/default-settings). Preserve the entire key-ring lifetime needed by retained application data and any separate wrapping-key/certificate material.

As an initial operational objective, plan at least daily snapshots and a verified pre-upgrade snapshot, retain seven daily and four weekly copies, and rehearse restoration after material schema/key changes. A 24-hour RPO and 60-minute RTO are proposed demonstration objectives, not approved production guarantees. Configure/measure the real schedule, encryption, off-host retention, failure alerts and recovery time once the host is selected. No point-in-time/binlog recovery is configured by this tool. Purge expired backups only through the chosen retention system after verifying newer recoverable copies; the script does not delete backups.

## Restore without touching the source

Keep traffic stopped. Choose a unique taskflow_restore_* destination (taskflow_test_restore_* for test rehearsals). Grant the recovery operator DDL/DML rights on that exact future schema before running the tool. Do not pre-create the schema: recovery uses CREATE DATABASE without IF NOT EXISTS and refuses an occupied destination. Choose a new key-directory path under a private parent as well.

```sh
python3 scripts/backup_recovery.py restore \
  --defaults-file /private/taskflow/recovery-client.cnf \
  --backup /private/backups/taskflow-unique-backup-id \
  --target-schema taskflow_restore_unique_recovery_id \
  --keys-destination /private/taskflow/restored-keys-unique-id
```

Checksum/path/layout validation precedes database creation. Checksums establish integrity relative to the manifest, not authenticity against an attacker who can rewrite both; recover only trusted, access-controlled backups. Linked/traversing payloads are rejected. Source and target lower_case_table_names must match. Windows commonly stores table names differently from Linux; a Windows case-folded dump must not be replayed onto the default case-sensitive Linux server and assumed compatible. Cross-case/platform conversion needs its own reviewed migration rehearsal.

After import, every table count and migration ID must match the backup. The tool then changes all user SecurityStamp/ConcurrencyStamp values and UpdatedAtUtc to invalidate pre-recovery sessions, and copies the verified key bytes into the new key directory. It does not preserve old logins. Accounts/password hashes, memberships, business records, timestamps outside that deliberate user update, retained deletions and activity remain from the snapshot. A failed import/validation leaves the new schema isolated for investigation and does not publish a key directory or start the app. Do not retry over it; use another explicitly reviewed fresh destination.

Compare representative accounts/projects/tasks/assignments/deletions/activity to backup evidence. Grant the runtime account its normal rights on the recovered schema, including DELETE only on ProjectMembers. Select a compatible application release, switch database and physical key-directory configuration together, and verify readiness. Test old-cookie 401, fresh sign-in, project/member/task workflows, dashboard/progress, history, restart/persisted session and logout replay. The [release runbook](release-runbook.md) describes traffic cutover and compatible rollback. Keep the original database untouched and private until the incident is resolved.

## Local rehearsal

The sanitized [step 22 evidence](evidence/step22.json) contains timings/checks from a real, fresh-target recovery with restricted accounts. The source was changed and its saved session revoked after backup. Restoration recovered the earlier data, retained deleted rows/history, matched all business-data fingerprints and key hashes, rejected the old cookie and accepted fresh sign-in. Re-running restore against the occupied target failed without altering it. These are small local demo measurements, not a production-scale recovery SLA.
