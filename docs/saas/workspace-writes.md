# Scoped writes and relational references

SaaS-010, 27 October 2026. Nine v2 write operations are implemented. They require **both** `WorkspaceReads:Enabled` and `WorkspaceWrites:Enabled`; both remain false by default. The current frontend and v1 APIs remain compatible. This is a backend preview, not the coordinated company pilot cutover.

## Operations and permissions

All routes start with `/api/v2/workspaces/{workspaceId}` and require a validated session plus the existing paired CSRF cookie/header.

| Operation | Route suffix | Permission |
| --- | --- | --- |
| POST | /projects | Active company Owner/Admin/Member; Guests cannot create projects |
| PUT, DELETE | /projects/{projectId} | Authoritative project owner |
| POST | /projects/{projectId}/members | Project owner; target must already be an active member of this company |
| DELETE | /projects/{projectId}/members/{userId} | Project owner; cannot remove the project owner |
| POST | /projects/{projectId}/tasks | Project owner or explicit legacy project Member, including a company Guest granted project editing |
| PATCH | /tasks/{taskId}, /tasks/{taskId}/status | Same project task-write permission |
| DELETE | /tasks/{taskId} | Project owner |

Company Owner/Admin roles never imply access to another owner's private project. The actor comes from the session; workspace comes from the route; task/member/activity scope comes from the authorized parent. Requests cannot set owner, workspace or parent IDs through writable body fields. Write queries reject all query parameters, including workspace overrides. Missing/inactive company membership or mixed-company resource IDs return 404; a forbidden action on a visible project returns 403. Unknown/foreign company members cannot be silently invited through project membership.

PUT uses the current project request shape. Task PATCH preserves omitted fields and explicitly clears nullable description/assignee/deadline values with null. Empty, duplicate, unsupported and wrong-type fields are rejected; enums remain case-sensitive and numeric IDs are rejected. Deadline storage remains microsecond precision. Unchanged updates do not create success activity. Version conflicts/expectedVersion remain SaaS-015, not an invented timestamp-based concurrency promise.

## Transaction and lock boundary

`IWorkspaceWrites` delegates to mandatory-context `IWorkspaceTransactions`. It reuses the existing `IProjectMutation` implementation, rather than opening a second CRUD path without the existing assignment/history protections. Each operation uses a fresh DbContext and read-committed transaction.

Scoped lock order is **workspace row → actor membership → project → affected rows**. Actor membership is checked again under a locking read after waiting for the workspace gate. Assignee/add-member target membership is also locked and must be active; a global identity FK is not membership authorization. Scoped project locks include the requested workspace predicate, and task-to-parent lookup happens inside the transaction. Context is captured for the operation and never retargeted through a current-company preference.

The workspace gate intentionally serializes scoped writes within one company. It avoids opposing membership/assignment lock orders while lifecycle work is being added. Different workspaces have different gates. This is a conservative integrity choice, not a throughput claim: profile hot-company contention against the [pilot plan](../pilot-operations.md) before customer rollout. Future suspension, ownership, quotas and worker mutations must use this same order/transaction; do not nest this fresh-scope boundary under another connection already holding its gate.

Mutation, assignment cleanup and activity commit together. Removing a project collaborator clears their active task assignments in that scoped project, records unassignment/removal activity, and cannot restore an assignment from a queued stale edit. Deleted tasks keep historical assignments; unrelated projects/companies are untouched. Failed activity persistence rolls back the membership and assignment changes. Ownership and workspace identity cannot be changed by the generic mutation callback. All new scoped activity receives server-derived workspace/project/actor references.

## Composite database constraints

Migration `20261027144903_WorkspaceReferences` adds six restrictive foreign keys and supporting tenant-first indexes:

| Source | Referenced key |
| --- | --- |
| Projects (WorkspaceId, OwnerId) | WorkspaceMemberships (WorkspaceId, UserId) |
| ProjectMembers (WorkspaceId, ProjectId) | Projects (WorkspaceId, Id) |
| ProjectMembers (WorkspaceId, UserId) | WorkspaceMemberships (WorkspaceId, UserId) |
| Tasks (WorkspaceId, ProjectId) | Projects (WorkspaceId, Id) |
| ActivityLogs (WorkspaceId, ProjectId) | Projects (WorkspaceId, Id) |
| ActivityLogs (WorkspaceId, ProjectId, TaskId) | Tasks (WorkspaceId, ProjectId, Id) |

These reject mismatched non-null scoped references even outside application services. Active assignment eligibility remains an application/locking rule: retained deleted-task assignees and historical activity actors need not have a current company membership, so they retain global identity FKs. The database does not claim to enforce active role/state eligibility or all lifecycle rules.

Scope columns remain nullable while legacy writers exist. Composite FK NULL semantics permit those transitional rows; they do not provide a non-null company-isolation guarantee. Scoped APIs always populate scope and scoped reads exclude unscoped rows. Use the preview only on a reconciled disposable snapshot without concurrent legacy writers. A legacy writer can invalidate reconciliation. The legacy owner-mapping tool belongs before scoped use; do not use it to regroup valid companies after intentional scoped creation or ownership changes.

These six compatibility-stage FKs are deliberately managed in the migration, not as nullable EF alternate keys: EF would make those key properties required and break the staged expansion. **Use migrations, not EnsureCreated.** Model-drift checks alone cannot prove these SQL constraints exist; live tests inspect all six constraints and exercise rejection. When promoting scope to NOT NULL, explicitly drop/recreate or adopt these named constraints in the reviewed EF enforcement migration so they are not duplicated or lost.

Physical company-membership deletion now requires dependent ownership/project grants to be reconciled first. Suspension retains the reference while denying requests. Complete leave/suspension/ownership workflows and their cleanup remain SaaS-012.

## Rehearsal and rollout

1. Keep customer traffic closed and snapshot database plus protection keys. Restore the snapshot to a fresh restricted rehearsal target first.
2. Validate/reconcile existing scope and affiliations before adding the FKs. The migration validates populated rows and must fail on inconsistencies; it never deletes or reassigns them to make constraints fit.
3. Apply the matching migration bundle with the migration identity. Existing binaries/current binaries with a mismatched compiled/applied migration set become not-ready. Verify exact readiness after migration.
4. Reconcile legacy data before enabling the scoped preview in an isolated environment:

```powershell
$env:WorkspaceReads__Enabled = 'true'
$env:WorkspaceWrites__Enabled = 'true'
dotnet ./TaskFlow.Api.dll
```

5. Verify session/CSRF protection, Owner/Admin/Guest boundaries, mixed-company denials, task PATCH semantics, membership cleanup, rollback and queued writes. Inspect FK presence and activity scope.
6. Keep NOT NULL enforcement, the matching company UI, role lifecycle, v1 retirement and full SaaS-016 acceptance open. **Flags do not close v1 bypasses:** legacy v1 still uses the project-only policy. Do not start a customer company pilot until the coordinated cutover is complete.

MySQL DDL is not generally transactionally reversible. A partially failed FK migration may leave some constraints/indexes installed without a migration-history entry. Keep maintenance active and use reviewed forward repair or restore the coordinated snapshot; do not blindly rerun or use a destructive Down migration as recovery. Backfill DML rollback/retry is separate from DDL recovery.

The local suite adds nine HTTP/MySQL cases covering scoped CRUD/CSRF, server-derived scope, company/private-project denials, foreign members/assignees, PATCH, suspension, queue rechecks, both assignment/removal orders, failed-activity rollback and direct SQL constraint rejection. See [verification evidence](../evidence/saas010-writes.json), including the populated-backup upgrade and published HTTPS checks. Hosted CI/container and selected-host rollout remain separate gates. Next is SaaS-011 company navigation and resource keys.
