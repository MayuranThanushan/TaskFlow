# Task workflows - steps 14 and 15

Owners and current members can create tasks, browse active project tasks and open direct task details. [Editing, status changes and owner-only soft deletion](task-mutations.md) are implemented in step 15. No schema or dependency change is required.

## API and validation

- POST `/api/v1/projects/{projectId}/tasks` returns 201 TaskDto with a direct detail Location.
- GET `/api/v1/projects/{projectId}/tasks` returns `{items,page,pageSize,totalCount,totalPages}`.
- GET `/api/v1/tasks/{taskId}` returns TaskDto after checking active task and current parent-project access.

TaskDto contains id, projectId, title, description, status, priority, assigneeId, dueDateUtc, createdAtUtc and updatedAtUtc. IDs remain positive signed BIGINT decimal strings, including values beyond JavaScript's safe integer range. Nullable values emit explicit null; deletion/authentication state is excluded.

Title is trimmed and required, maximum 200 UTF-16 code units. Description is trimmed, blank becomes null, maximum 10,000. Omitted priority defaults to Medium; supplied null, unsupported or numeric priorities fail. The server sets ToDo. Unknown, duplicate and server-managed fields such as status/projectId are rejected. Optional assignee must be the owner or a current member; missing/ineligible users return field-level 400 on assigneeId.

Optional deadlines require ISO date/time with Z or an explicit offset. They normalize to UTC and DATETIME(6) precision; fractional ticks beyond microseconds are truncated. UTC years 1000-9999 match MySQL's representable range. Past deadlines are valid. Missing/null deadlines remain null; unzoned/date-only/invalid values fail 400. Display uses local time.

Authentication is required for all routes, with CSRF on creation. Missing/inaccessible/deleted resources return the same safe 404, including after membership removal. Reads/totals apply active-project/task and current owner/member scope. Responses are no-store and use safe Problem Details and trace IDs. Middleware serialization now preserves derived validation field errors.

## Transactions and queries

Creation locks the parent project, rechecks current access/assignee eligibility and saves the task with TaskCreated activity. Initial assignment also records TaskAssigned. Generated task IDs resolve before activity insertion; raw descriptions are excluded from history. Activity failure rolls back task and assignment. Membership boundary methods require the membership operation; task creation requires the task write operation.

Creation and member removal share the parent lock: removal clears a just-created assignment or creation rejects an already-removed assignee. Task editing is implemented in step 15; step 16 will extend member-removal concurrency verification across its assignment writes.

Listing supports page/pageSize (defaults 1/20, maximum size 100), trimmed literal case-insensitive title search (maximum 200), status, priority and assigneeId combined with AND. Sorts are title, priority, dueDateUtc and createdAtUtc, with asc/desc direction. Default is createdAtUtc descending. Every sort adds numeric id descending; priority explicitly ranks Low < Medium < High despite text persistence. Null deadlines sort last in both directions. Access, count and page share a consistent read snapshot. Unsupported/repeated parameters, empty/invalid enum/ID filters and overflowing offsets fail 400. Out-of-range pages return empty items and real totals.

## Frontend

Project detail links to `/projects/:projectId/tasks`, with creation at `/projects/:projectId/tasks/new` and direct detail at `/tasks/:taskId`. The form validates/focuses fields, lists eligible assignees, defaults to unassigned/Medium and converts a local optional deadline to UTC. It preserves failed input/server field errors, prevents duplicate submissions and opens saved detail. Assignee-list failures offer retry and permit unassigned creation.

The list supports search, page size, pagination and URL history, plus empty/no-match/out-of-range/loading/retry states. Advanced filter/sort controls remain step 17; their API contract is supported. Detail shows plain text, status, priority, assignee ID, local deadline and creation time, with links to tasks and current project progress. Returning fetches fresh totals. Account changes remount protected content, clear drafts/titles and ignore stale pending write completions.

## Verification

Release build has zero warnings/errors. All 76 backend tests passed against real MySQL with zero skips (34 non-database and 42 live checks). Six new task HTTP/database cases cover defaults, member creation/assignment/activity IDs, validation/authentication/CSRF/UTC, eligibility field errors, large IDs, visibility/revocation/deletion, literal AND filters, paging/null/priority ordering, rollback and concurrent creation/removal.

All 57 frontend tests passed, including ten task cases for protected creation, defaults, opaque assignment, UTC conversion, safe text, invalid/pending submissions, retained field errors, search/pagination/history, failure recovery, private detail, expiry and account-switch safety. Type checking, ESLint and production build passed. No migration is pending. Existing CI discovers the tests; hosted CI, Docker execution and selected-host verification remain pending.

The combined Production release passed verified HTTPS migration/readiness/restart smoke checks and real Edge owner/member task creation, eligible assignment, local deadlines, large IDs, safe text, reload, progress refresh, task search/pagination/reload, member-removal unassignment/access denial and mobile layouts. Form/detail screenshots were inspected. Final review also corrected field errors to clear when users edit the associated field. The disposable database was removed and verification services stopped.
