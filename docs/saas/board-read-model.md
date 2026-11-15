# SaaS-030: board read model, status counts and column pagination

15 November 2026. Implemented read-only foundation for the project board. This release adds two company-scoped GET operations and a typed client; it does not add a board screen, persisted rank, drag/drop or move commands. Those are SaaS-031 and SaaS-032. There is no new database migration, dependency, worker or paid resource. The schema remains at sixteen migrations.

## API and filters

Both routes require a signed-in session and the existing `WorkspaceReads:Enabled` gate. The application's default is false; the current free Render blueprint explicitly enables workspace reads. This release does not change that configuration or add a second board flag. An enabled deployment exposes the new read routes when the new binary is deployed. Production OpenAPI remains private/disabled as before.

| GET route under /api/v2/workspaces/{workspaceId}/projects/{projectId} | Response |
| --- | --- |
| /board | projectId, totalCount and exactly three columns with status/totalCount |
| /board/columns/{status} | Existing TaskPage: items, page, pageSize, totalCount, totalPages |

`status` is exactly ToDo, InProgress or Done. Every summary includes all three in that order, including zero counts. Counts cover the entire matching project, never just the fetched cards. The sum of column counts equals totalCount in that response. IDs and task versions stay decimal strings; counts are numbers. No new status or workflow configuration is added.

Both routes accept `search` (trimmed, maximum 200 characters), `priority` (Low/Medium/High) and `assigneeId` (positive signed-BIGINT decimal string). Filters combine with AND and use exactly the task list's authorized filter query. Blank search means no search; blank priority/assignee filters are invalid. The summary intentionally rejects status, sorting and paging parameters so it cannot silently report a partial board.

Column requests additionally accept page (default 1), pageSize (default 20, maximum 100), sortBy (title/priority/dueDateUtc/createdAtUtc) and sortDirection (asc/desc). Defaults are createdAtUtc descending, then ID descending. Other sort modes retain the task list's deterministic ID-descending tie break, explicit priority order and null-deadline-last behavior. Page offset must fit Int32. Unsupported/repeated parameters, noncanonical status names, invalid filters and overflow return safe 400 validation problems.

Example relative requests after selecting an authorized company/project:

```text
GET /api/v2/workspaces/1/projects/2/board?priority=High
GET /api/v2/workspaces/1/projects/2/board/columns/ToDo?priority=High&page=1&pageSize=100
GET /api/v2/workspaces/1/projects/2/board/columns/ToDo?priority=High&page=2&pageSize=100
GET /api/v2/workspaces/1/projects/2/board/columns/InProgress?priority=High&page=1&pageSize=20
```

Column pagination is independent. A caller follows totalPages for each status, including columns containing more than 100 tasks. Empty/far pages return an empty items array with truthful counts and requested page metadata, not a misleading 404 or truncated complete board. Each request returns at most 100 cards; the summary loads no cards.

## Visibility and consistency

The implementation shares the existing Tasks/Projects access predicates and the task-list filter helper, with no separate board authorization rule. Active company membership, current private-project attachment/ownership, live task/parent and valid scoped assignee references are required exactly as in list reads. Company administrators gain no private-project override. Authorized Viewer/Guest readers can read boards. Deleted/unscoped/cross-company or inaccessible resources remain hidden; revoked/suspended access is rechecked on each request. There is no legacy v1 board route.

Each response authorizes and reads inside the existing repeatable-read transaction. A column's totalCount and cards share one snapshot even during a concurrent write. No cache, copied title, new materialized table or background counter is introduced; GET does not add audit/outbox mutations.

Separate requests are live views, not a durable multi-page snapshot. Concurrent task creation, deletion or status changes may shift offset pages or make a previously read summary differ from the next column response. Clients must refresh affected columns and summary after mutations, reset column pages when filters/sorting change, deduplicate IDs while appending and offer reload if the result changes. Stable rank/cursor/session snapshot semantics are not promised here. Server-owned rank/conflict-safe movement follows in SaaS-031.

## Typed client and integration

[board-api.ts](../../TaskFlow-FE/src/features/board/board-api.ts) exposes getBoard and getBoardColumn using generated operation query/response types. It requires a captured company client, rejects unsafe project IDs/unknown columns before transport, supports cancellation and propagates access failures instead of rendering an empty successful board. The existing account/company request lifecycle remains in force. The [API snapshot](../contracts/openapi.json) and generated TypeScript include both routes: **71 paths / 93 operations**.

SaaS-032 will wire these reads into the accessible board UI. Do not label the existing task list as a fully shipped Kanban screen. Keep the currently working task list available during future board loading/recovery.

## Verification and rollout checklist

- [ ] Deploy matching frontend/backend contract artifacts after the normal CI/release checks. No migration beyond the existing sixteen is required and no grants beyond current task-read privileges are introduced.
- [ ] Confirm the existing workspace-read configuration and private-project access behavior in the selected environment.
- [ ] In a disposable project, verify all three counts, empty columns and second pages for columns with more than 100 cards. Compare filters/sorting against task list results.
- [ ] Verify guest/viewer reads, administrator-without-project denial, suspended/revoked membership, soft-deleted task/parent and mixed-company URLs.
- [ ] Confirm the current API query plan and selected-host latency at the agreed project size. Local MySQL verification is not a hosted capacity or TiDB certification.
- [ ] Preserve the frontend/list fallback and existing feature configuration. Rolling back the binary needs no destructive schema rollback for this release.

Automated tests cover 364 visible cards across three independently paged columns, full filtered counts, every supported sort mode, empty/far pages, canonical statuses, strict query validation, scope/deletion/role denials and synchronized concurrent creation. Client tests cover company capture, read-only transport, filters, ID validation, cancellation and access failure. See [release evidence](../evidence/saas030-board.json) and the [documentation index](../documentation-index.md).

Next: SaaS-031 server-owned rank and atomic move/reorder API, then SaaS-032 board UI and keyboard/non-drag movement.

<!-- current-documentation-index -->
Current implementation, release boundaries and rollout gates: [documentation index](../documentation-index.md). Release-specific counts and follow-ups above describe their recorded release.
