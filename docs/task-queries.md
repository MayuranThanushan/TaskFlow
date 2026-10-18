# Task filtering, sorting and pagination - step 17

The task list now exposes the complete adopted query contract. No schema, dependency or backend query implementation change was required; step 14's query foundation is verified more thoroughly here.

## Frontend behavior

Search, status, priority, assignee, sort field/direction and page size are applied together. Assignee choices show the project owner/current members and retain IDs as opaque strings. All assignees includes unassigned tasks; the API has no separate unassigned-only filter. A selected ID absent from current member results remains visible as a user-ID option, preserving bookmarked filters after membership changes. Assignee-name failures offer independent retry without blocking other filters or clearing the selected ID.

Apply builds a canonical URL from supported fields, trims search and resets the page. Paging preserves all applied fields. Reload and browser back/forward restore controls from the URL. Reset clears filters, sorting and paging to defaults. Valid unknown assignee IDs produce ordinary empty results, not eligibility validation failures intended for writes.

No-match messages recognize any active filter. Out-of-range pages retain actual totals and offer a first-page action preserving filters. Invalid query URLs show a filter-specific error with an explicit reset; network/server failures retain retry. Superseded requests cannot replace the newest result. React renders items in the API's order without client-side sorting or ID conversion.

## API guarantees

GET `/api/v1/projects/{projectId}/tasks` combines trimmed literal case-insensitive title search, status, priority and assigneeId using AND. It applies current project access and soft-deletion scope before count/order/pagination. Current owners and members see the same authorized collection; outsiders, removed members and deleted projects return safe 404.

Allowed sorts are title, priority, dueDateUtc and createdAtUtc, with asc/desc direction. Default is createdAtUtc descending. Every sort uses numeric task ID descending as tie-breaker, even when the selected field ascends. Priority ranks Low < Medium < High despite enum-name persistence. Null deadlines are last in either direction.

Page/pageSize default to 1/20, maximum pageSize 100. Unknown/repeated parameters, invalid/empty enums/IDs/sorts, excessive search, invalid numbers and offset overflow return 400. Out-of-range pages return empty items and real totals; zero results yield zero totalPages. Access, count and items share a REPEATABLE READ snapshot, so another committed creation cannot make one response's count and page disagree. Separate page requests each reflect their own current snapshot; this is offset pagination, not a frozen multi-page export.

## Verification

All 95 backend tests passed with real MySQL and zero skips (34 non-database and 61 live checks). Five new live cases verify every sort/direction using explicit expected numeric tie order across pages; all three priorities and null/equal deadlines; literal wildcard/backslash search and AND filtering; cross-project/deletion/removal scope; repeated/invalid parameters; maximum/far/empty page metadata; and consistent count/page while an independent HTTP request creates a task. A bounded test interceptor pauses the real count query to force that last overlap.

All 72 frontend tests passed, including six new cases covering URL initialization/paging/back/reset, combined controls and returned order, invalid-URL recovery, failed assignee-name loading, late-response isolation and out-of-range filtered page recovery. Type checking, ESLint, Release/production builds pass. No migration is pending. Existing CI discovers the tests; hosted CI, Docker execution and selected-host verification remain pending.

Step 18 [dashboard totals/project progress](dashboard.md) is implemented. Next is step 19 activity reading.

The published Production release passed verified HTTPS migration/readiness/restart smoke checks and real Edge combined filtering/assignee selection, priority and nullable-deadline ordering, URL reload/back/forward, out-of-range recovery, no-match/invalid reset and mobile controls. Rendered task IDs matched API order. The mobile filter screenshot was inspected with no horizontal overflow. Existing account/project/task/removal checks also passed. The disposable database was removed and verification services stopped.
