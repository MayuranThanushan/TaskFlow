# Workspace expansion and legacy backfill

SaaS-008, 26 October 2026. The schema expansion and offline backfill are implemented. This stage preserves the current project API and access policy. It does **not** enable company isolation, company switching, new project roles or company quotas. Read [ADR 0005](../decisions/0005-commercial-saas-boundaries.md) and the [cutover contract](migration-cutover.md) before rollout.

## Phases and enforcement boundary

| Phase | Implementation | Release condition |
| --- | --- | --- |
| Expand | Migration `20261026153432_WorkspaceExpansion`: Workspaces, WorkspaceMemberships, nullable WorkspaceId on projects, project members, tasks and activity; restrictive workspace/user FKs; unique legacy-owner mapping; workspace-first indexes | Apply only with the matching application and controlled migration identity. Existing records/IDs remain intact |
| Backfill and reconcile | Offline plan/apply/verify commands; persisted owner-to-workspace IDs in a private manifest and Workspaces.LegacyOwnerId; transactional DML, source/mapping/access checksums, maintenance confirmation and migration lease | Restore/rehearse first, stop all writers, approve the snapshot and plan, then verify before reopening |
| Enforce and cut over | **Deferred to tenant read/write implementation and coordinated v2 rollout** | No null or mismatched scope; tenant-aware writes; mandatory workspace context; composite parent references; roles/assignments and API/UI cutover checks accepted |

Nullable scope is intentional during expansion: today's v1 writers do not populate it. An old successful backfill must never be treated as a permanent certificate of scoped data. Any new legacy data or relevant change invalidates the manifest fingerprint; stop writers, review a new plan and backfill again immediately before cutover. Readiness checks compiled/applied migration equality, not workspace isolation or completion of backfill.

Before the enforce migration in SaaS-009/010 and coordinated cutover:

1. Deploy and test server-derived workspace scope for every write, including membership cleanup, activity and raw SQL. Require active company membership plus project permission for reads and writes. Retire unscoped data routes according to ADR 0005 when the matching UI is deployed.
2. Stop writers and reconcile the final snapshot. Require exact owner mapping, complete non-null scope on all four tables, correct owner/Guest memberships and unchanged project grants. Resolve malformed references, ineligible active assignees and inconsistent scope explicitly.
3. Change all tenant scope columns to NOT NULL. Promote the existing unique workspace/project and workspace/project/task indexes to referenced composite keys. Add matching child-to-project references and activity-to-task references that include workspace and project. Preserve single/global ID uniqueness and existing historical foreign keys. A global user FK alone never proves membership or active-assignee eligibility.
4. Rehearse failed DDL, old/new binary readiness, interrupted application replacement, mixed-workspace references and recovery from a coordinated DB/application/key snapshot. MySQL DDL is not generally transactionally reversible; no automated down-migration recovery is promised.

These constraints are not silently enabled in expansion while v1 still writes null scope. Separate workspace FKs prevent dangling workspace IDs now; cross-workspace parent FKs and non-null enforcement are still pending. A successful schema migration alone does not satisfy the R1 pilot release gate.

## Mapping and preserved data

Every owner, including a deleted-only project owner, maps to one distinct workspace labelled `Legacy workspace`. This is not a verified legal-company identity. Non-owner project collaborators receive active workspace Guest memberships; existing project membership rows retain their current `Member` value and editing behavior. Their future project-role equivalent is Editor, to be introduced with role enforcement. A user can be an Owner in one workspace and a Guest in another. No other private-project membership is created.

Accounts without projects or collaboration receive no invented company. Activity actors and retained deleted-task assignees do not become members merely because history references them. Project/task/activity IDs, authoritative owners, deletion state, task fields, history content and source timestamps are preserved. The backfill changes only scope columns and inserts the mapping/workspace-membership rows. Its migration-only raw updates are separate from ordinary append-only business activity guards; normal API behavior remains unchanged.

The source fingerprint covers all projects, project members, tasks and history, including retained deleted rows and relevant content/timestamps, plus the global user-ID set. Content is hashed in memory, never written into the migration manifest. Source workspace columns are excluded so the exact manifest remains usable after apply/retry. Grants and persisted mapping are validated separately. A checksum detects drift; it is not a signature or authorization credential.

The tool refuses owner-as-member ambiguity, missing/cross-project parents or actors, unresolved active assignments, changed source/mapping, extra/promoted/suspended workspace grants, and conflicting non-null scope. It does not silently delete grants, unsuspend users or overwrite inconsistent mapping. Current pilot implementation reads the complete source into memory, capped at 100,000 rows per legacy source table; it refuses oversize input rather than truncating it. Review memory and batching before larger customer migrations.

## Operator checklist and commands

Use the reviewed application payload and migration bundle from a coordinated release. Back up database **and** protection keys under maintenance, verify the backup, and restore it into a fresh restricted rehearsal schema before touching a live target. Follow [backup/recovery](../backup-recovery.md) and the [release runbook](../release-runbook.md). No production migration was executed by this task.

Configure ConnectionStrings__TaskFlow through an operator-restricted secret file/environment. Use a controlled migration identity with the necessary update/insert rights. Normal runtime credentials should not gain activity-update or schema-management grants for this command. No connection string, password or token belongs in command arguments or manifests.

```powershell
# Existing release procedure: apply the expansion to the restored rehearsal target.
./efbundle.exe

# Create an operator-private directory first; restrict its Windows ACL / Unix permissions.
# Plan is read-only. The manifest path must be new and its parent must already exist.
dotnet ./TaskFlow.Api.dll --workspace-backfill=plan --manifest=/private/migrations/workspaces.json --snapshot-id=verified-pre-cutover-snapshot-001

# Review counts, source/mapping hashes and every owner mapping privately.
# Stop API replicas, workers and other writers; drain in-flight requests first.
dotnet ./TaskFlow.Api.dll --workspace-backfill=apply --manifest=/private/migrations/workspaces.json --maintenance-confirmed=true

# Verify independently, then demonstrate that retry creates no duplicate mapping/grants.
dotnet ./TaskFlow.Api.dll --workspace-backfill=verify --manifest=/private/migrations/workspaces.json
dotnet ./TaskFlow.Api.dll --workspace-backfill=apply --manifest=/private/migrations/workspaces.json --maintenance-confirmed=true
```

Commands use exact `--key=value` arguments. Only plan accepts a new snapshot reference; apply/verify consume the existing reviewed manifest. The snapshot reference is operator-supplied provenance, not proof that a backup exists. Commands require exact compiled/applied migration equality. The process builds services without opening an HTTP listener or starting hosted exporters. Failure returns exit code 1 with a safe generic diagnostic; do not upload private server diagnostics to CI.

Manifest files contain account/workspace IDs and must stay outside Git and CI artifacts. Plan creates a new file exclusively, rejects reparse-point parent directories and uses user-only creation permissions on Unix; restrict the existing parent directory and Windows ACLs yourself. Apply/verify reject reparse-point files and manifests larger than 20 MiB. Keep the reviewed manifest with the recovery snapshot. Never regenerate random mappings just to bypass a failed apply.

Apply acquires a database-specific MySQL advisory lock with no wait, then uses a serializable transaction and savepoint. Snapshot validation, workspace/membership insertion, scope updates and post-validation commit together. Failures roll back the DML; the same manifest can retry. The lease coordinates this tool, not arbitrary SQL clients; `--maintenance-confirmed=true` asserts an operator action and does not stop an application. No schema DDL runs inside the backfill. A lost connection during commit is ambiguous: keep maintenance active, verify the same manifest against the target, and reconcile before retry/restore.

After cutover, company transfers, suspensions and roles are deliberate lifecycle operations. Do not use this legacy mapping tool to undo those changes. Keep it restricted to reviewed pre-cutover maintenance.

## Local verification

Nine MySQL cases cover role/access/deleted-history preservation, orphan/historical accounts, opaque IDs, stale/tampered manifests, missing maintenance, ineligible assignments, conflicting scope, unexpected grants, a competing migration lease, savepoint rollback/retry, restrictive workspace references and exclusive/strict command manifests. All 157 backend tests passed with no skips. API contracts and the current frontend behavior remain unchanged.

The separate published-app rehearsal restores a populated original-schema backup, applies expansion, runs offline plan/apply/verify/retry, exercises real browser mutations, proves that subsequent v1 writes invalidate the old plan, reconciles again, and backs up/restores the expanded result. Only disposable local schemas are used. See [evidence](../evidence/saas008-workspaces.json) for measured outcomes and remaining gates.
