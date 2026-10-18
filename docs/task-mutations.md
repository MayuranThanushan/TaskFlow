# Task editing, status and soft deletion - step 15

Task detail now offers editing and explicit status changes to owners/current members, and confirmed deletion to owners. No schema or dependency change is required.

## API

- PATCH `/api/v1/tasks/{taskId}` accepts title, description, priority, assigneeId and dueDateUtc; returns 200 TaskDto. Omitted fields remain unchanged. Explicit null clears description, assignment or deadline. Null/blank title and null priority fail validation. At least one editable field is required. Unknown/server-managed fields, duplicate keys, non-object/empty bodies, invalid types/enums/IDs and invalid/unzoned deadlines return field-level 400. Status cannot be changed through this endpoint.
- PATCH `/api/v1/tasks/{taskId}/status` requires exactly status (ToDo, InProgress or Done), permits every transition between valid states and returns `{id,status,updatedAtUtc}`. Empty/null/numeric/unsupported status and extra fields fail 400.
- DELETE `/api/v1/tasks/{taskId}` is owner-only and returns 204. It sets DeletedAtUtc and normal audit time; it retains the row, assignment and all history. A current member receives 403. Further reads/edits/status changes/deletions return 404; lists/counts/progress exclude the task. Deleted-task history remains internally available in an active project.

All writes require authentication and CSRF. Outsiders, removed members, missing/deleted tasks and deleted parents return the same safe 404. IDs remain opaque decimal strings. The creation deadline parser is shared with editing, including explicit offset/Z, UTC normalization, supported MySQL range and microsecond precision.

## Transaction and activity

Direct task writes resolve the current visible parent, then enter the shared parent-lock transaction and recheck access and active task state. This prevents editing/deletion races from resurrecting a task. Assignee eligibility is checked inside that boundary, including reassignment during ordinary editing. No task ownership/project ID can be changed.

Actual content/priority/deadline changes append TaskUpdated. Reassignment records TaskUnassigned for the old user and TaskAssigned for the new user; assignment-only edits use these specific events. Clearing assignment records TaskUnassigned. Status changes append TaskStatusChanged with from/to values, and deletion appends TaskDeleted. Domain changes, task timestamps and all events commit atomically; failed activity insertion rolls everything back. Normalized no-op edits and no-op status requests leave UpdatedAtUtc and activity unchanged.

## Frontend

The protected edit route `/tasks/:taskId/edit` prefills the shared task form. It sends only changed fields and explicit null for cleared optional values, preserving server changes to omitted fields. An untouched deadline is omitted to preserve microseconds and ambiguous local instants. A completely unchanged form returns to detail without sending an empty PATCH. Failed edits retain inputs and associated errors; successful edits reload detail with a notice.

The detail status selector disables unchanged and pending submissions, retains a failed selection for explicit retry and reloads detail after success. Member views show edit/status controls while deletion waits for an authenticated project-owner check. The server remains authoritative.

Owner deletion uses an inline confirmation explaining task-list/progress effects. Keep task receives focus; Escape cancels and restores focus. Pending confirmation disables duplicate actions. Failure preserves the task and confirmation for explicit retry; success navigates to a fresh task list with a notice. Late responses after unmount/account changes cannot navigate the new session. Lists and project progress reload from the API when revisited.

## Verification

All 82 backend tests passed with real MySQL and zero skips (34 non-database, 48 live). Six new cases verify PATCH omission/null semantics, normalized no-op timestamps/activity using an advanced clock, strict bodies/enums/IDs/deadlines, member edit/status and owner deletion, CSRF/401/403/404, retained rows/history and updated progress, assignment events/eligibility, real activity failure rollback for all three operations and concurrent edit/delete serialization.

All 64 frontend tests passed, including seven new mutation cases for changed-only editing/precision preservation, optional clearing/no-op forms, pending status updates, member controls, confirmed deletion/cancellation, error recovery and account-switch stale-response handling. Type-check, ESLint, Release/production builds pass. No model change is pending. Hosted CI, Docker execution and selected-host verification remain pending.

Step 16 [assignment/removal concurrency](assignment-concurrency.md) is now verified across creation, editing and status mutations, including both lock orderings and rollback. Next is step 17 filtering/sorting/pagination.

The published Production release passed verified HTTPS migration/readiness/restart smoke checks and real Edge owner/member editing, nullable clearing with omitted-value preservation, status/reload/progress refresh, member deletion 403, owner confirmation/Escape/deletion/list refresh and deleted deep-link rejection. Mobile edit/confirmation layouts passed overflow checks, and the confirmation screenshot was inspected. The disposable database was removed and verification services stopped.
