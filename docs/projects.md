# Project workflows - steps 11 and 12

The first project workflow is implemented across the API, database use cases and React pages. Owner-only editing and confirmed soft deletion are implemented in step 12; [Membership management](project-membership.md) is implemented in step 13; [Task creation/list/detail](tasks.md) is implemented in step 14; editing follows in step 15. No migration or dependency change is required.

## API

All project routes require the authenticated cookie session. Responses use no-store caching and the existing safe Problem Details/trace contract. The CSRF filter factory now has explicit order 1000, so authentication runs first on protected writes (anonymous project POST returns 401); public registration/login still require CSRF.

| Route | Behavior |
| --- | --- |
| POST /api/v1/projects | Accepts name and optional nullable description; returns 201 ProjectDto and Location pointing to detail |
| GET /api/v1/projects | Returns access-scoped, deterministically paginated ProjectDto items and actual totals |
| GET /api/v1/projects/{projectId} | Returns ProjectDetailsDto or generic 404 for missing/inaccessible/deleted projects |
| PUT /api/v1/projects/{projectId} | Owner-only replacement of name/description; returns 200 ProjectDto |
| DELETE /api/v1/projects/{projectId} | Owner-only soft deletion; returns 204 |

Name is trimmed and required, with 150 UTF-16 code units maximum. Optional description is trimmed, blank becomes null, and the maximum is 10,000. Unknown/duplicate body properties are rejected, including caller-supplied owner/id/audit fields. Owner comes exclusively from the current authenticated user. The step 10 transaction boundary saves the project and ProjectCreated activity atomically; failure of activity insertion rolls back creation.

List options are page (default 1), pageSize (default 20, range 1-100), search (trimmed name substring, maximum 200), sortBy (name/createdAtUtc/updatedAtUtc), and sortDirection (asc/desc, default desc). Default sort is createdAtUtc descending. Every sort adds numeric ID descending as tie-breaker. Search is literal and case-insensitive under the adopted MySQL collation; percent, underscore and backslash do not become wildcards. Unsupported/repeated query parameters, malformed bounds and offsets beyond Int32 range return 400. An out-of-range page returns empty items with the real totals; no matches means totalPages zero.

The shared ProjectAccess.VisibleProjects query applies active-project and current owner/member scope before search, sorting, counts or pagination. Role checks in step 10 use the same scope. List count and page share a REPEATABLE READ snapshot. Detail access and active task/completed counts use one SQL statement; soft-deleted tasks are excluded. Progress is zero for no tasks and otherwise rounds completed/total*100 to the nearest integer, midpoint upward. IDs in all DTOs remain decimal strings, including IDs above JavaScript's safe integer range. Invalid detail IDs return 400; valid missing IDs return 404. No soft-delete/auth state is exposed.

## Frontend

Protected routes are `/projects`, `/projects/new`, `/projects/:projectId` and `/projects/:projectId/edit`, linked from navigation and the workspace overview. The project list includes name search, sort/direction, page size, pagination and URL history. Applying filters resets the page; back/reload restores applied URL options. Empty workspace, no matches, out-of-range page, loading and retryable failure states are handled separately.

Creation validates and focuses invalid fields, associates server field errors, preserves input after failure and disables duplicate submissions. It sends only name/description, then opens the saved detail with a success notice. Returning to the list reloads it from the API. Detail displays plain text description, creation date and real task/progress values. Missing/private/deleted resources show one generic unavailable view. Owners see Edit project and Delete project actions. Editing prefills the shared validated form, preserves input on failure and reloads detail after save. Members cannot open the edit form. Deletion opens an inline confirmation section, focuses Keep project, supports Escape/cancellation and restores trigger focus. Confirmed deletion disables both controls while pending, keeps the project visible on failure with an explicit retry, and returns to a freshly loaded list after success. The [member panel](project-membership.md) provides listing and owner-managed membership; [Task pages](tasks.md) provide creation/list/detail; editing follows later.

GET effects cancel/ignore superseded responses. A change of authenticated user invalidates in-memory CSRF/session generations and remounts protected page content. Project data, form drafts and document titles clear before the new account's data is shown. A create response arriving after its form unmounts/account changes does not navigate the new account into the old request's resource. Already-authorized server mutations may still finish, consistent with decision 0002; the UI does not assume aborting navigation cancels a write. Project API 401s use the shared expiry redirect and preserve the intended route.

The layouts follow the existing responsive shell, with visible focus, associated labels/errors, disabled pending states, status announcements and native progress semantics. Descriptions are rendered as text, including strings that look like HTML.

## Verification

- Release build and 65 backend tests passed against real MySQL 8.4.11 with zero skips: 34 checks can run without MySQL, 31 require it. No model/snapshot change is pending.
- Six new HTTP/database tests cover authenticated/CSRF-protected creation, server-derived ownership and activity, member/outsider/deleted/removal visibility, literal search, stable pagination, query/body/id validation, real progress counts/rounding, large string IDs and real activity insert failure returning a safe 500 with no persisted project.
- Type checking, ESLint, production build and all 38 frontend tests passed. Fourteen new project tests cover creation, no-op invalid submission, pending duplicate protection, field errors, pagination/search/back navigation, recovery, safe text, progress, private-resource handling, expiry and account-switch data/draft/title/pending-response behavior.
- The final combined release ran in Production with file-mounted settings, verified HTTPS and real MySQL. Existing readiness/migration/restart/logout smoke checks passed. Edge completed protected navigation, blank-name validation, creation/detail/reload, a project ID above 2^53, literal search, pagination/reload, second-account isolation and an actual same-cookie-jar cross-tab account switch. Desktop/mobile detail/list screenshots were inspected; no page errors or horizontal overflow occurred at 390px.

The PowerShell publisher now builds the frontend in a unique ignored `.local/build` directory, copies only build inputs (no local environment files), runs npm ci/build, and copies only dist into the release. This avoids Windows native-module locks from active workspace dev/test processes. Cleanup validates the resolved build path stays inside that build directory; a locked temporary directory can be retained without deleting workspace dependencies. The isolated publish succeeded, and workspace dependencies were restored/checked without lockfile changes.

Synthetic records/accounts and the disposable production-smoke database were removed, and verification services stopped. Browser drivers/screenshots/releases/keys remain ignored local artifacts. Existing CI picks up the new tests; no hosted workflow run or publication is claimed. Step 09's Docker/selected-host verification gates remain pending. Step 13 membership is now implemented; step 14 task creation/list/detail is also implemented. Next is step 15 task editing/status/deletion.

## Step 12 mutation and retention guarantees

PUT requires a trimmed name; omitted, null or blank description clears the stored description. Invalid/unknown/duplicate fields return 400. IDs use the same positive signed BIGINT validation on GET, PUT and DELETE. Anonymous writes return 401; CSRF is required; a visible member receives 403, while outsiders, missing and deleted projects receive the same 404.

Both writes lock the parent project and recheck current access inside the existing transaction boundary. A normalized no-op PUT leaves timestamps and activity unchanged. Actual edits append ProjectUpdated; deletion sets DeletedAtUtc, updates the audit timestamp and appends ProjectDeleted. Activity failure rolls back the mutation. No raw project description is copied into activity payloads.

Deletion retains project, task, membership and activity rows. Task deletion timestamps remain untouched. Global parent filters hide tasks, memberships and history; list/count/detail exclude the project, and repeated deletion or further editing returns 404. There is no restore/purge endpoint. Pending write completions cannot navigate after page unmount or an account switch.

Step 12 verification: 67 backend tests passed with real MySQL and zero skips, including HTTP role/CSRF enforcement, replacement/no-op validation, retained related rows, post-delete invisibility and activity-failure rollback for both mutations. All 42 frontend tests, type-check, lint and production build passed. No schema change is pending.

The step 12 published Production app passed the HTTPS migration/readiness/restart smoke rehearsal and real Edge editing/reload, confirmation focus/Escape cancellation, confirmed deletion/list refresh/deleted deep-link rejection, plus the existing project/account/mobile checks. The disposable database was removed and all verification services stopped.
