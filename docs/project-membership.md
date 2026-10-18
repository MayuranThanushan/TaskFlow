# Project membership - step 13

Project detail now lists the owner and current members. The owner can add an existing account by its decimal user ID and confirm member removal. The overview displays the signed-in account ID so teammates can share it without a user directory. Invitations, account search, owner transfer, role changes and self-service leave remain deferred.

## API and access

- GET `/api/v1/projects/{projectId}/members` returns `{items:[{userId,displayName,role,joinedAtUtc}]}` to the current owner or member. The owner is synthesized first using project creation time; Member rows follow in numeric user ID order. Email and authentication data are excluded.
- POST to that collection accepts `{userId,role?}` with role defaulting to Member, and returns 201 with the membership DTO and collection Location. Unsupported/null roles, invalid positive signed BIGINT IDs, numeric JSON IDs and unknown/duplicate fields fail validation. IDs stay strings throughout the client and API.
- DELETE `/api/v1/projects/{projectId}/members/{userId}` returns 204. Adding the owner or an existing member, or removing the owner, returns 409. A missing target account/membership returns 404.

All routes require authentication. Mutations require CSRF and ownership: current members receive 403; outsiders, missing and deleted projects receive the same safe 404. Removed users lose list/detail/member access on their next request. Error responses use the existing safe Problem Details and trace contract.

## Transaction and retention

Membership writes lock the parent project first and recheck access inside the shared transaction. Concurrent duplicate additions serialize to one 201 and one 409. No owner membership row is persisted.

Removal deletes the non-owner membership, clears that user's active task assignments and records MemberRemoved plus a TaskUnassigned event for each affected task. It preserves assignments on soft-deleted tasks and leaves other assignees untouched. Additions record MemberAdded. Activity details contain backend-created user IDs, with task IDs attached to unassignment events. All domain and activity changes commit together; activity insertion failure rolls back membership and task changes.

This implements assignment cleanup required by the membership contract now. Step 16 will extend verification to concurrent assignment/removal once task mutation endpoints exist; those writes must use the same parent lock and current assignee eligibility checks. No schema or dependency change is required.

## Frontend and verification

The member panel handles loading, failure/retry, pending and success states. Only owners see add/remove controls; the owner has no remove button. IDs are validated without Number conversion. Duplicate/missing-user errors preserve input. Removal explains access loss and assignment cleanup, focuses Keep member, supports Escape and disables duplicate pending actions. The list reloads after a successful mutation. Unmounted/account-switched panels ignore late write completions.

All 70 backend tests passed with real MySQL and zero skips, including three new HTTP/database cases for membership access, validation/conflicts, owner-first ordering, concurrent duplicates, next-request revocation, active-task cleanup and rollback. All 47 frontend tests, type checking, ESLint and production build passed; five new frontend cases cover add/validation/roles/removal/cancellation/pending/failure recovery.

Existing CI discovers these tests automatically. Hosted CI and Docker/selected-host verification remain pending; no external publication is claimed.

The combined published Production app passed HTTPS migration/readiness/restart smoke checks and a real two-account Edge journey: owner add/reload, member listing with hidden owner controls, server-side 403 for a member write, keyboard cancellation, mobile confirmation, owner removal and next-request detail/list access revocation. The mobile confirmation screenshot was inspected without horizontal overflow. The disposable database was removed and verification services stopped.
