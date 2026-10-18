# Project access and transactional activity - step 10

The shared authorization and mutation foundation is implemented. Project/task HTTP routes and screens remain feature work, starting with step 11. This step adds no schema changes, packages or public activity-writing endpoint. The owner/member rules remain those in [decision 0002](decisions/0002-permissions-and-retention.md).

## Access checks

`IProjectAccess` and the pure `ProjectAccessPolicy` live in Application/Features/Projects. Infrastructure implements the current-state database queries. Their decisions map to existing Problem Details: unauthenticated 401, missing/inaccessible/deleted 404, visible but forbidden 403. Successful authorization returns Allowed. Expected denials do not become unhandled 500s or include resource details.

| Operation | Owner | Member | Outsider |
| --- | --- | --- | --- |
| Read project/task resources | Allowed | Allowed | 404 |
| Manage project | Allowed | 403 | 404 |
| Manage membership | Allowed | 403 | 404 |
| Create/edit/assign tasks and change status | Allowed | Allowed | 404 |
| Delete tasks | Allowed | 403 | 404 |

Ownership derives from Projects.OwnerId, without an owner membership row. Membership is queried on each check; role claims and old tracked entities are not authorization sources. Nonpositive/missing actor IDs return Unauthenticated before querying the database. Missing/invalid roles fail closed. Soft-delete query filters scope projects, memberships and direct task lookup. A direct task ID is resolved through its own active accessible parent; deleted tasks and tasks in deleted/inaccessible projects are hidden.

Read checks support individual resource operations. Later list/count/search use cases must scope their SQL before pagination/aggregation rather than performing permission checks after loading all records. A read check alone does not authorize a later write: use the transaction boundary, which rechecks access under the lock.

## Mutation boundary

`IProjectTransactions` is an internal application interface. Its actor argument must come from the authenticated session; no public request accepts actor, owner or activity identities. API controllers retain their default authentication and CSRF filters.

`ExecuteAsync` opens a fresh scoped DbContext and a READ COMMITTED transaction. It acquires the existing project's row using a parameterized SELECT FOR UPDATE, then checks current active-project visibility and permissions before running the callback. The raw locking read is materialized directly without composing SQL around FOR UPDATE. The lock is held through data/activity persistence and commit. This provides the parent-first ordering required for later membership/task mutations and allows a waiting writer to observe committed removal/deletion before making its access decision.

`CreateAsync` opens the same atomic boundary for a new project, derives its owner from the session actor, and records ProjectCreated automatically. There is no existing row to lock during creation. Return the project object from the callback and map it to a DTO after the transaction completes, when its generated ID is available:

```csharp
var project = await transactions.CreateAsync(actorId, (change, token) =>
{
    change.Project.Name = validatedName;
    change.Project.Description = validatedDescription;
    return Task.FromResult(change.Project);
}, cancellationToken);
// Map project to the response here, after commit and generated-ID assignment.
```

For an existing project update, use ManageProject, modify the tracked project, and call `change.Record(ActivityAction.ProjectUpdated, details)` only when normalized business values actually change. Project identity/owner changes are rejected. A task/member permission cannot be used to modify project metadata. `Read` is not a valid mutation operation. The boundary normalizes project text before change detection, so whitespace-equivalent updates are no-ops.

The callback surface deliberately exposes the tracked project, activity recording and current assignee eligibility only. It does not expose DbContext, SaveChanges or generic repositories. Membership/task mutation methods will be added with their use cases in steps 13-16; those workflows are not implemented by this foundation. Extend the same scoped boundary with narrow methods, preserving parent-first locking and stable affected-record ordering. Do not perform writes in another DbContext or nested transaction inside a callback. Keep callbacks short, propagate cancellation, and perform no external service calls while holding the lock.

## Activity and failure behavior

Activity records take project/actor IDs from the boundary. Action values must be defined. Optional details are a server-created flat dictionary of strings/null, serialized immediately to a JSON object and bounded to 10,000 serialized characters. Dictionary changes after recording cannot alter the pending history. Use explicit, small non-secret details; do not serialize request bodies, credentials or whole entities. Task references must be positive and belong to the locked project, including retained soft-deleted tasks where relevant for history.

Changed data requires at least one activity record; activity without a data change is rejected as a programming error. A genuine no-op saves neither timestamps nor history. Existing append-only history enforcement remains in TaskFlowDbContext.

The boundary first saves domain changes/generated IDs and then the activity rows, both inside the same transaction. Commit occurs only after both saves succeed. Exceptions, invalid task scope, failed activity persistence and cancellation dispose/roll back the transaction. A fresh context per call prevents a failed mutation's tracked state from leaking into later operations. The boundary does not automatically replay mutation callbacks.

## Verification

Release build and all 59 backend tests passed against real MySQL 8.4.11, with zero skips: 34 checks can run without MySQL, and 25 require it. Existing CI discovers and runs the added tests under its mandatory database/no-skip gate. The model snapshot reports no pending changes.

Coverage added in this step includes:

- All owner/member operation combinations, hidden outsiders/unknown roles and anonymous checks without database access.
- Real current-state ownership/membership, owner-without-membership, removal taking effect on a reused access service, direct task access, deleted tasks/parents and denied write callbacks.
- Generated project IDs, authoritative owner/actor, UTC timestamps and committed structured activity details.
- A real activity foreign-key insert failure after the project save: creation/update and timestamps roll back, history stays absent, and a subsequent transaction succeeds.
- No-op normalization, rejected no-op/missing activity, immutable ownership, member task permission unable to edit project metadata, cross-project activity task rejection and callback cancellation.
- Two real connections: one holds the parent row and removes membership; a second writer demonstrably waits, then returns 404 before running its callback after the removal commits.
- Safe JSON 401/403/404 mapping through test-only API probes, without adding production probe routes.

The selected provider cannot execute a filtered bulk membership DELETE with its generated self-referencing SQL. The membership-removal test setup uses a normal tracked delete after loading the visible membership; cleanup uses explicit synthetic IDs with filters disabled. Future membership features should use the tracked mutation under the parent lock rather than copying a filtered ExecuteDelete query.

Mutation tests commit only their own synthetic records, verify results from fresh contexts, then remove their own records in FK order. They do not wipe tables or alter the schema. The local MySQL process stopped afterward. No frontend behavior, publication or hosting configuration changed in this step; [step 09's container/selected-host gates](deployment.md) remain open. Next is step 11: project creation, authorized listing/detail and frontend forms/pages.

Implementation references: [MySQL locking reads](https://dev.mysql.com/doc/refman/8.4/en/innodb-locking-reads.html), [MySQL transaction isolation](https://dev.mysql.com/doc/refman/8.4/en/innodb-transaction-isolation-levels.html), [EF SQL queries](https://learn.microsoft.com/ef/core/querying/sql-queries), and [EF transactions](https://learn.microsoft.com/ef/core/saving/transactions).
