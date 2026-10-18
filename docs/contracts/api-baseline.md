# API contract amendments: implementation baseline

Date: 18 October 2026. Implements delivery step 01. Applies to `Documents/API Spec - TaskFlow.docx`, with decisions in [the register](../decisions/README.md). Implementation status: authentication/CSRF, health/readiness and project create/list/detail/edit/delete and membership routes, plus task creation/list/detail/edit/status/delete, and dashboard totals are implemented; activity/profile-update routes remain planned. Unchanged Word specification routes remain in scope.

## Common contracts

- Base `/api/v1`; camelCase JSON; opaque positive decimal-string IDs everywhere (including path, filter, user, owner, actor and assignee IDs).
- DTOs only. Unknown/duplicate request body properties and unsupported enum values return 400. Required field limits and normalization follow decision 0003.
- UTC ISO timestamps with Z; nullable fields emit explicit null. Input deadlines require an offset or Z. All errors use Problem Details with status/title/traceId and field errors when applicable, including antiforgery errors.
- 401 unauthenticated; 404 missing/inaccessible/deleted; 403 visible but forbidden; 409 uniqueness/ownership conflicts; 429 rate limiting; safe 500 unexpected errors.
- Create responses: 201 and Location for retrievable resources (registration Location points to `/api/v1/auth/me`, which requires subsequent login). Delete/logout: 204 without a body. Update/read: 200 with DTO.

## Authentication

| Method and route | Body/response | Rules |
| --- | --- | --- |
| GET /auth/csrf | 200 `{ "requestToken": "opaque token", "headerName": "X-CSRF-TOKEN" }` | Public; issue framework antiforgery cookie; Cache-Control: no-store |
| POST /auth/register | displayName, email, password → ProfileDto | Valid CSRF token required; 201; no automatic sign-in; duplicate email 409 |
| POST /auth/login | email, password → ProfileDto | Valid CSRF token required; 200 and auth cookie; generic 401 for invalid/locked credentials |
| GET /auth/me | ProfileDto | Authenticated; 200 or 401 |
| POST /auth/logout | No body → 204 | Authenticated and CSRF-protected; rotate security stamp then sign out all sessions |
| PATCH /users/me | displayName → ProfileDto | Authenticated and CSRF-protected; only displayName editable |

ProfileDto: `{ "id": "101", "displayName": "Alex Morgan", "email": "alex@example.test" }`. Do not expose password hashes or return bearer tokens. Fetch a fresh CSRF token after login/logout. All state-changing routes below require the token too. Use no-store for authentication/profile responses. Expiry, password policy, lockout and rate limits are defined in decision 0001.

## Project and membership amendments

ProjectDto remains id, name, description, ownerId, createdAtUtc, updatedAtUtc, with string IDs. GET `/projects/{projectId}` returns ProjectDetailsDto, adding totalTasks, completedTasks and progressPercentage, computed from active tasks in that active project. Progress is an integer 0–100.

```json
{
  "id": "201",
  "name": "TaskFlow",
  "description": null,
  "ownerId": "101",
  "createdAtUtc": "2026-10-18T08:00:00Z",
  "updatedAtUtc": "2026-10-18T08:00:00Z",
  "totalTasks": 3,
  "completedTasks": 1,
  "progressPercentage": 33
}
```

POST `/projects` accepts name and optional description. PUT `/projects/{projectId}` requires name; omitted or null description clears it as replacement semantics. Only owner may PUT/DELETE. A DELETE soft-deletes and returns 204; subsequent requests return 404.

GET `/projects/{projectId}/members` returns `{items:[{userId,displayName,role,joinedAtUtc}]}`. Owner is synthesized first, then members by numeric userId ascending. POST accepts `{ "userId": "102", "role": "Member" }`; role defaults to Member when omitted, all other supplied roles fail 400. Return 201 with membership DTO. Target must exist; duplicate/owner returns 409. DELETE `/projects/{projectId}/members/{userId}` returns 204 and unassigns active tasks atomically. Owner removal returns 409. All membership mutations are owner-only.

## Task amendments

TaskDto is the Word specification's task representation with all identifiers as strings. Optional description/assigneeId/dueDateUtc are nullable. No DeletedAtUtc or internal auth/audit state is exposed.

POST `/projects/{projectId}/tasks`: title required; description, assigneeId, dueDateUtc optional/null; priority defaults Medium if omitted. Supplied null/invalid priority fails 400. Server sets ToDo. Accept only the documented fields. Assignee must be owner or current member; ineligible/missing user returns 400 on assigneeId.

PATCH `/tasks/{taskId}` permits title, description, priority, assigneeId, dueDateUtc, with omission/null semantics from decision 0003. At least one supported property is required. Example below clears assignment and deadline without changing other fields:

```json
{ "assigneeId": null, "dueDateUtc": null }
```

PATCH `/tasks/{taskId}/status` requires exactly status and returns `{id,status,updatedAtUtc}`. A no-op status request returns current data without an activity record. DELETE `/tasks/{taskId}` is owner-only and soft-deletes. Task create/edit/status operations are allowed for Owner and Member. All direct task routes check the active parent project's access.

## Queries and aggregates

| Collection | Filters | sortBy allowlist | Default |
| --- | --- | --- | --- |
| GET /projects | search (name) | name, createdAtUtc, updatedAtUtc | createdAtUtc desc, id desc |
| GET /projects/{projectId}/tasks | search (title), status, priority, assigneeId | title, priority, dueDateUtc, createdAtUtc | createdAtUtc desc, id desc |
| GET /projects/{projectId}/activity | fromUtc, toUtc | Fixed order; no sortBy | createdAtUtc desc, id desc |

Project/task sortDirection is asc/desc, default desc. Append id descending as tie-breaker for every selected sort. Priority order is Low < Medium < High. Nullable due dates sort last in either direction. Search terms are trimmed, maximum 200; empty means no filter. Searches are literal case-insensitive substrings. All task filters combine using AND. Activity time bounds are inclusive; require offset/Z; reject fromUtc > toUtc. Unsupported query parameters return 400 to expose client mistakes.

All three collections use page/pageSize and `{items,page,pageSize,totalCount,totalPages}` with defaults 1/20 and max size 100. Membership listing remains the unpaginated `{items}` contract from the Word specification. Apply authorization and active-record scope before aggregation or pagination.

GET `/dashboard` retains totalProjects, totalTasks, tasksByStatus (`toDo`, `inProgress`, `done`), assignedToMe, overdueTasks. Return nonnegative counts with consistent scope/time. Project progress is provided through project details, avoiding an additional endpoint.

Activity DTO: id, projectId, actorUserId, optional taskId, actionType, details, createdAtUtc. All IDs are strings. Details is a safe backend-generated summary. Activity event creation is transactional and private to backend use cases.

## Contract acceptance checks for later implementation

1. CSRF required even for login/registration; fresh token after identity changes; cookie replay rejected after logout.
2. Unknown/duplicate fields, invalid enums, null required fields and empty PATCH rejected.
3. Large valid IDs round-trip without precision loss; out-of-range IDs fail safely.
4. Members cannot delete tasks/manage membership; outsiders cannot enumerate private resource IDs.
5. Soft-deleted project/task visibility, atomic unassignment/history, and aggregates agree.
6. Progress, overdue instant equality, ordering ties, null deadlines and pagination boundaries match this contract.

## Foundation endpoint amendment (step 04)

GET /api/v1/health/live is public and returns 200 `{ "status": "Healthy" }` with no-store caching. It checks process liveness only, without querying MySQL. Error responses carry traceId and all responses include X-Trace-Id. See [API foundation](../api-foundation.md) for implementation and verification details. Authentication/CSRF routes above are now implemented; business/profile-update routes remain pending their feature steps.

## Authentication implementation amendment (step 07)

Registration/login passwords have a 128-code-unit upper bound; the password is never trimmed. Auth routes require HTTPS, use no-store caching and emit profile DTOs with string IDs. State-changing controller requests require antiforgery validation; all controller actions require authentication by default except explicit anonymous actions. Validation field names follow JSON names, and duplicate JSON keys are rejected globally. See [authentication](../authentication.md) for session, rate-limit, certificate and verification details.

Step 09 adds public GET /api/v1/health/ready with no-store caching: 200 `{ "status": "Healthy" }` when MySQL connects, migrations are current and an Identity table is readable; otherwise 503 `{ "status": "Unhealthy" }`. This health response intentionally uses the health DTO rather than Problem Details and exposes no database diagnostics. Liveness stays independent. See [deployment](../deployment.md).

Step 10 implements the internal project/task access service, owner/member operation policy, safe 401/403/404 denial mapping and transactional activity boundary. This does not expose project/task/activity HTTP routes yet. See [project foundation](../project-foundation.md) for integration rules and verification.

Step 11 implements POST /projects, GET /projects and GET /projects/{projectId}, including the documented list queries, stable ordering, current access/deletion scope, string IDs, real detail counts/progress and transactional ProjectCreated activity. Protected writes authenticate before CSRF validation; anonymous POST /projects returns 401. See [projects](../projects.md) for validation and verification.
