# Workspace-scoped read preview

SaaS-009, 27 October 2026. Nine read-only v2 operations are implemented behind `WorkspaceReads:Enabled`, **false by default**. Current v1 behavior and frontend navigation remain unchanged. This is a staged backend preview, not the coordinated company-isolation rollout. Tenant writes, relational enforcement, company switching and lifecycle/role cutover remain pending.

## Routes and rollout

| GET route | Result |
| --- | --- |
| /api/v2/workspaces | Paginated caller-active memberships: workspace ID/name and caller role |
| /api/v2/workspaces/{workspaceId}/projects | Visible private projects, scoped search/sort/count/page |
| /api/v2/workspaces/{workspaceId}/projects/{projectId} | Scoped project details and task progress |
| /api/v2/workspaces/{workspaceId}/projects/{projectId}/tasks | Scoped task filters/sort/count/page |
| /api/v2/workspaces/{workspaceId}/tasks/{taskId} | Scoped task details |
| /api/v2/workspaces/{workspaceId}/projects/{projectId}/members | Active company collaborators explicitly attached to this project, plus its active owner |
| /api/v2/workspaces/{workspaceId}/projects/{projectId}/activity | Project history, including individually deleted tasks |
| /api/v2/workspaces/{workspaceId}/activity | History across the caller's visible active projects in this workspace |
| /api/v2/workspaces/{workspaceId}/dashboard | Totals/status/assignment/overdue counts from this same scoped visibility |

Use the [expanded schema and offline backfill](workspace-expansion.md) in a disposable verification environment first. Unscoped rows are intentionally invisible to v2. Enabling the flag never infers a workspace or backfills data. Normal app readiness still checks migration equality, not backfill completion or company release acceptance.

```powershell
# Only in an isolated, reconciled verification environment for this preview.
$env:WorkspaceReads__Enabled = 'true'
dotnet ./TaskFlow.Api.dll
```

Authentication/CSRF/global profile routes retain their current v1 contract and cookie transport settings. Anonymous preview requests require login (401). Authenticated requests to disabled preview routes receive 404 with no-store Problem Details. No v2 writes are exposed. The existing frontend continues to use v1 and has no company switcher yet.

The flag is a rollout control, not a substitute for authorization. **Do not enable a customer company pilot at this stage:** v1 project routes still use the legacy project policy and do not enforce workspace suspension. SaaS-010 through the matching UI/role cutover must close those paths and retire v1 data endpoints before claiming company isolation. No automatic v1-to-v2 redirect or default-company adapter is added.

## Access and consistency rules

The controller builds an immutable `WorkspaceReadContext` from the authenticated account and explicit workspace route. Each scoped service call rejects missing/invalid context and requires active workspace membership. It never falls back to global project visibility. Workspace discovery lists only the caller's own active memberships, paginated at 20 by default with maximum pageSize 100.

Company Owner/Admin roles do not grant private-project readership. A project is visible only to its authoritative owner or explicitly attached legacy project member, and only inside the requested workspace. Guests can read explicitly shared projects. Owners in one company and Guests in another are evaluated independently. The legacy project `Member` value remains until the Editor/Viewer role stage; the workspace role returned in discovery is Owner/Admin/Member/Guest.

A resource filter checks workspace admission before model binding and automatic query validation. An absent, inaccessible or suspended company membership returns 404 even when its query filters are invalid. Services recheck admission inside a repeatable-read transaction. Counts, pages and aggregates share the same authorized snapshot; a legitimately admitted in-flight snapshot may complete during removal, but the next request rechecks current membership. Project-member removal also takes effect on the next request. No workspace data cache or mutable request-wide default company is introduced.

Every project predicate checks workspace equality, active parent state and the owner's association with that workspace. Tasks additionally check workspace/parent agreement and that a non-null assignee belongs to the workspace and the project. Inconsistent assignee rows fail closed rather than revealing an unrelated account ID. Suspended historical membership is distinct from an unrelated company reference; task ownership/assignment cleanup remains lifecycle work.

Member rows require matching workspace/project references and an active workspace membership before returning global display names. Their joined timestamp comes from the project membership, not the company membership. Email is not exposed. Activity requires matching workspace/project/task references; a historical actor or deleted-task assignee need not still be an active company member. Raw activity details and private task titles are not added to summaries.

Explicit deletion predicates are required even with EF query filters. The deleted-task history subquery uses IgnoreQueryFilters, which can propagate across composed EF subqueries. A regression test caught deleted-parent history appearing when only global filters were used. Project/task readers now explicitly require active parents; history preserves individually deleted tasks without restoring deleted-project visibility. NULL or mismatched workspace children cannot enter lists, counts, progress or feeds during the nullable expansion stage.

Project/task/activity paging, allowlisted filters, stable numeric-ID tie breakers, nullable-deadline ordering, literal substring search and inclusive UTC activity bounds retain their existing semantics. Resource IDs remain signed-BIGINT decimal strings. Query-supplied workspace overrides are rejected. Current task/member/project response shapes are reused; counts are numeric. Successes and errors use no-store responses and existing trace correlation.

The workspace-wide feed is **not** the future company governance audit console: private projects remain hidden, deleted projects remain unavailable, there is no company-admin bypass, and no durable export job is created. Current-page client export belongs to the existing v1 UI; company audit/export remains SaaS-013/014.

## Verification and remaining gates

Nine live-MySQL HTTP cases exercise default-off/anonymous behavior, one identity shared across two companies, explicit Guest access, company Owner/Admin without project grants, cross-company ID mixtures, filtering/paging/opaque IDs, suspension/removal, deleted parents, retained deleted-task history, and malformed/unscoped child and owner references. Existing project/task/dashboard/activity and migration tests continue to run. The reviewed OpenAPI snapshot has 26 paths / 33 operations, with generated read-only TypeScript operations and compile-time workspace-ID/role assertions.

The published HTTPS rehearsal runs current real-browser journeys with the flag off, backfills that disposable snapshot, enables the preview, then checks real-session workspace discovery and every reader plus outsider/invalid-filter denials. See [evidence](../evidence/saas009-reads.json). These are local results, not hosted production evidence or the full future SaaS-016 isolation acceptance matrix.

Next: SaaS-010 tenant-aware writes and composite references. Keep the [coordinated cutover checklist](migration-cutover.md), role lifecycle, frontend account/company switching, final backfill reconciliation and hosted release gates open.
