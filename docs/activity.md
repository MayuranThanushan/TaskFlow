# Project activity reader and UI - step 19

Owners and current members can read paginated project history. The reader is separate from the existing internal transactional writer; no public create/update/delete activity endpoint is added. No schema or dependency change is required.

## API

GET `/api/v1/projects/{projectId}/activity` returns `{items,page,pageSize,totalCount,totalPages}`. ActivityDto contains id, projectId, actorUserId, optional taskId, actionType, details and createdAtUtc. IDs are positive decimal strings, nullable taskId is explicit null and timestamps are UTC with Z. Details is a human-readable backend-generated summary rather than raw stored JSON, descriptions or authentication data. Only whitelisted valid user IDs and status values contribute to summaries; malformed/unknown payloads safely use generic wording.

Authentication and current owner/member access are required. Outsiders, removed members and missing/deleted projects receive the same 404. Responses are no-store and follow the Problem Details/trace contract. Individually deleted tasks retain their history in active-project activity: the query does not join or filter by current task visibility or actor membership. Deleted projects retain history internally without a public reader.

Query options are page/pageSize (defaults 1/20, maximum size 100) and optional inclusive fromUtc/toUtc. Both timestamps require Z or an explicit offset, normalize to UTC and support the MySQL date range. Reversed bounds, unsupported/repeated parameters, invalid IDs/bounds/page values and offset overflow return 400. There is no configurable sort: order is createdAtUtc descending then numeric event id descending.

For sub-microsecond bounds, the lower bound rounds up and upper bound rounds down to the representable DATETIME(6) grid. This preserves inclusive instant comparisons instead of widening the range through truncation; an interval containing no representable instant returns no rows. Original instants are checked for reversal before precision adjustment. Access, count and page share a REPEATABLE READ snapshot. Out-of-range pages return empty items with real totals, and zero results have zero totalPages.

## UI

Project detail links to the protected `/projects/:projectId/activity` route. The feed shows readable summaries, actor/task IDs and local timestamps as text. Historical task IDs remain plain metadata, so deleted-task events do not require a live task endpoint. No event editing controls are present.

Optional local date/time limits convert to UTC on Apply, resetting the page. Untouched precise bookmarked bounds are preserved instead of being reduced to browser millisecond precision. Page size, range and paging live in the URL and restore on reload/back. Reset clears them; Refresh activity reads the current range again. Loading, no history/no matching range, out-of-range recovery, invalid-filter reset, safe failure/retry and unavailable-project states are handled. The shared identity-scoped loader clears previous-account history and ignores late responses.

## Event coverage and verification

The active-project reader is verified for ProjectCreated/Updated, MemberAdded/Removed, TaskCreated/Updated/Assigned/Unassigned/StatusChanged/Deleted. ProjectDeleted is recorded transactionally and retained internally; its parent is no longer publicly readable. Deleted-task and removed-actor records remain readable to current authorized members. Normalized no-op task edits/status requests create no extra history. Public write attempts fail without altering stored entries; persistence append-only checks remain in place.

All 105 backend tests passed with real MySQL and zero skips (34 non-database and 71 live). Five new cases verify complete visible mutation coverage and immutable public behavior, access/retention, inclusive offset/microsecond bounds and large IDs, stable paging and query validation, safe summary redaction/cross-project scope and a forced concurrent mutation between count and page. Existing rollback tests verify failed mutations do not publish partial history.

All 84 frontend tests passed, including six new feed cases for protection, safe text/opaque metadata, date conversion/precise bounds, paging/history, invalid/reversed/empty range handling, retry/refresh and account-switch late-response safety. Type checking, ESLint, Release and production builds passed; no migration is pending. Existing CI discovers the new cases. Hosted CI, Docker execution and selected-host verification remain pending.

The published Production build passed local HTTPS smoke verification and Edge browser journeys for owner/member access, paging with reload/back, precise inclusive bounds, reset/no-match/invalid-filter recovery, refresh, retained deleted-task history, and outsider/removed-member/deleted-project denial. The 390px mobile feed was reviewed with no horizontal overflow. The disposable smoke database was removed and verification services stopped.

Step 20 [profile editing](profile.md) is implemented. Next is step 21: browser journeys, accessibility and focused security/performance review.
