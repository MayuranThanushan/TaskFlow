# Release, migration and recovery runbook (step 22)

Portable release preparation and local Production recovery have been rehearsed. No hosting target, Docker engine or remote CI run is available here; public deployment remains gated on the checks below. Nothing has been published.

## Release identity and preparation

Use a reviewed commit, passing backend/frontend/operations checks, and a clean checkout. Keep the previous application release and its receipt. Prepare a new directory, never mix releases:

```powershell
./scripts/Prepare-Release.ps1 -OutputDirectory /private/releases/taskflow-candidate -Runtime linux-x64
python scripts/release_manifest.py verify /private/releases/taskflow-candidate
python scripts/release_manifest.py verify /private/releases/taskflow-candidate/app
```

Windows rehearsal uses `-Runtime win-x64`; Linux arm64 is also selectable but has not been executed here. A compatible ASP.NET Core 10 runtime is required; bundles are framework-dependent. Python 3.12+ supports the operations tooling. The output contains app/, operations/migration-from-empty.sql, operations/efbundle (or .exe), and package/application release-manifest.json receipts. Receipts bind the source commit, dirty-state flag, migration IDs and SHA-256 hashes of every payload file. Missing, modified, extra or linked files fail verification. Hashes detect drift; they are not publisher signatures. The local rehearsal intentionally records its uncommitted candidate as sourceDirty=true; actual rollout requires a clean committed build.

The publisher isolates frontend dependency installation. The package builder also copies tracked backend inputs into an ignored build directory: EF bundle RID restores cannot rewrite checkout lockfiles. Stage/commit new backend inputs before preparing a release. New package/output paths are required; partial failed builds remain unsuitable for rollout.

For Compose, set TASKFLOW_RELEASE_TAG to the reviewed release identifier in the private deployment env file. App and migration image names use that same tag. Keep/reuse the exact images for rollout and compatible rollback; do not silently rebuild an old tag. See [deployment configuration](deployment.md) for TLS, network, file permissions and persistent volumes.

## Database accounts and secrets

Provision the private MySQL database and migration account first. Apply the reviewed schema, then restrict the initialized runtime account before opening traffic. The [role template](../deploy/database-roles.sql.example) must be filled only in an operator-private copy and run through an administrative session. Limit account hosts to the actual app/tool hosts; the template's % is only a single-host Compose example.

| Account | Required scope |
| --- | --- |
| Runtime | SELECT/INSERT/UPDATE on the app schema; DELETE only on ProjectMembers, because removing membership deletes that join row. No schema DDL or physical project/task/history deletion privilege. |
| Migration | Schema-scoped SELECT/INSERT/UPDATE/DELETE/CREATE/DROP/ALTER/INDEX/REFERENCES; used only by the explicit migration job. |
| Backup | SELECT/SHOW VIEW on the app schema; no writes or DDL. |
| Recovery | DDL/DML rights on one explicitly named new taskflow_restore_* destination; never the runtime account. |

The existing MySQL initialization account initially owns its schema. The template's restrictions are an explicit operator step, not an automatic property of a fresh Compose volume. Do not apply the runtime restrictions until the separate migration credentials exist. CI uses an all-privilege account scoped only to its disposable schema; local rehearsal separately proved the restricted production-role grants, including real access-denied responses.

Compose now mounts Migration__ConnectionString into the migration job as ConnectionStrings__TaskFlow. The app receives only its runtime ConnectionStrings__TaskFlow. Both files must be provisioned; neither connection belongs in CLI arguments, image layers or frontend settings. Backup/recovery clients read operator-restricted MySQL defaults files. Remote database clients must use verified database TLS with the host's CA; the local loopback rehearsal uses encrypted MySQL connections. Keep database/admin credentials, TLS private keys and key-encryption material in the selected host's secret system.

## Initial installation and upgrades

1. Verify package/image identity and checksums. Review migrations against the destination's actual __EFMigrationsHistory. The generated migration-from-empty.sql is for first-install review, not a script to replay over an existing database. For future upgrades, generate and review the incremental EF script from the currently applied migration to the release's target before approving rollout. Never edit a migration already applied in a deployed environment.
2. Put every app instance/writer into maintenance and stop app processes. Leave MySQL available. Prevent concurrent migrations and key-ring writers. On a new, empty installation there is no previous database/key ring to back up; on upgrades take and verify the [database/key backup](backup-recovery.md) first. Record its ID and the previous release.
3. Run the matching bundle using the migration connection. For a non-container package, set ConnectionStrings__TaskFlow privately in the process environment, use app/ as the working directory (appsettings.json lives there), then execute ../operations/efbundle. Clear the migration environment value afterwards. For Compose, use the explicit migrate service. Never start traffic after a failed job.
4. Re-running the current bundle must be a no-op. MySQL DDL is not a general transactional rollback boundary; on any failure keep maintenance active, inspect history/schema using a private operator session, and use reviewed forward repair or fresh-target recovery. Do not automatically run down migrations.
5. Start the selected app with runtime credentials, Production environment, correct AllowedHosts/TLS and a persistent physical DataProtection key ring. Preserve the same application discriminator (TaskFlow). Keep database/key volumes across replacements; do not use down --volumes on a deployment.
6. Wait for /api/v1/health/ready=200. Current readiness requires database access, an applied migration set exactly matching the compiled set, and a readable Identity table. Missing or unrecognized future migrations return safe 503; /health/live stays process-only. Do not alter history rows to force readiness. Also compare the older binary's receipt/history explicitly before any rollback, since earlier releases may use weaker readiness checks.
7. Run the HTTPS smoke check with a dedicated smoke account. Test real project/member/task mutations, dashboard/progress, history, profile and deletion scope in the selected environment. Verify role grants include membership deletion. On a disposable rehearsal host, the [browser suite](../tests/browser/README.md) covers these workflows; it must not target production data.
8. Recreate/restart the app with the same database/key ring and prove the saved smoke session still works; then prove logout rejects the previous cookie. Restore traffic only after smoke/readiness pass. Record release/backup identifiers, migration IDs, checksums, safe check results and operator time. Monitor readiness, safe 5xx/latency logs and backup alerts.

## Rollback and recovery decisions

For a binary-only problem with an explicitly compatible, unchanged migration set, select the retained previous binary/image, keep the database/key ring, restart and smoke-test. The local rehearsal proved this case only: both binaries use InitialCreate. This is not evidence that arbitrary future schema changes can be rolled back safely.

If data/schema recovery is required, keep traffic stopped and restore the last verified snapshot into a new database and new key directory. The recovery tool validates payload hashes, table-name case mode, imported table counts and migration history, restores keys, and rotates every user's security/concurrency stamps. All old sessions must sign in again. This prevents restoring an older security stamp from reviving a cookie revoked after the snapshot. Post-backup writes are absent; assess that loss before changing traffic. See the full [backup/recovery procedure](backup-recovery.md).

Select an application whose compiled migrations match the restored history, grant runtime access to that new schema, switch the database/key-directory settings together, restart, verify readiness and core data, then open traffic. Retain the failed/source database for investigation under restricted access. Do not overwrite or delete it as part of recovery. TLS certificates and database/grant secrets are restored separately from the approved secret inventory.

## Evidence and remaining release gates

The [sanitized rehearsal record](evidence/step22.json) records real MySQL 8.4.11 recovery. Twelve tables, accounts/credential hashes, membership, large task/project IDs, assignments, progress, soft-deleted rows and activity survived the snapshot. Per-table business-data fingerprints match, excluding the three intentionally changed user fields: SecurityStamp, ConcurrencyStamp and UpdatedAtUtc. Protection-key hashes match. A cookie revoked after backup remains rejected after recovery; fresh sign-in works. The previous compatible binary accepted a new persisted session across restart, logout replay failed, and the current binary became ready again. Verification processes, synthetic database users and owned databases were removed; sensitive local backups remain ignored and access-restricted.

All 110 backend tests pass with live MySQL and no skips; all 92 frontend tests and 16 operations tests pass. Release build/publish, migration generation/idempotence, model-snapshot check, receipt verification, Compose configuration and actionlint checks pass locally. The operations tests are included in the backend CI job. Docker/container execution and hosted CI are still unrun.

Before public release, select the host and verify DNS/certificate renewal, actual-host HSTS/TLS/proxy behavior, readiness supervision, non-root container/secret/volume permissions, private database TLS/grants, encrypted off-host backup scheduling/retention/alerts, a restore on that host and required hosted CI checks. No host-specific publication, RPO/RTO guarantee or production backup service is claimed. Next is step 23: final documentation/demo handoff, while these external release gates remain open.
