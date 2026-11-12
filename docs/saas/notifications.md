# SaaS-024: recipient in-app notifications

12 November 2026. This release adds the notification persistence and API foundation. It is default off. The inbox screen and channel/event preferences belong to SaaS-025; reminders/digests belong to SaaS-026. SendGrid staging and its outstanding real-delivery/bounce gates remain separate.

## Initial event and delivery semantics

The initial supported notification kind is `TaskAssigned`. A scoped task creation with an assignee already emits TaskAssigned; changing an assignee emits a new assignment event. Only that intended current assignee is eligible, with active company membership and current project access. Self-assignment produces no notification. Unassigned tasks, unrelated task edits, company facts and historical event replay produce none. This avoids notifying newly admitted members about old events.

Notification materialization runs inside the existing business transaction after saving generated resource IDs and before committing the assignment, audit and outbox. This is deliberate for the current bounded single-recipient event: it needs no additional worker, external send or paid scheduler. If notification persistence fails, the source change/audit/outbox all roll back. Outbox checkpoint behavior remains unchanged. Higher fan-out events may need a separate recipient queue in a future release; do not turn checkpoint history into send authority.

`UserNotifications` stores company, recipient, source event, project/task identifiers, closed kind, creation time and nullable read time. It stores no email address, title, description, rendered text or token. A unique company/recipient/event key prevents duplicate materialization. Composite references constrain event company, recipient membership and project/task parent scope. Existing event identity is never retargeted to a later assignee. Persisted events are not eligible for materialization replay; atomic source delivery means there is no partial successful source commit to repair.

Only `ReadAtUtc` is mutable through the application model and runtime database grants. No browser API can retarget a notification, delete it, choose another recipient or read raw outbox facts. No notification means email was sent or accepted by SendGrid.

## API

All routes require a stamped authenticated session, `Notifications:Enabled=true` and all three workspace rollout flags. Recipient is always the current session account. Company access is checked before binding and again in the service transaction. All responses are no-store; PATCH requires CSRF and the existing actor governance rate limiter.

When administrator MFA enforcement is enabled, the existing scoped-write step-up policy also applies to Owner/Admin read-state mutations. The notification API does not bypass that policy. The SaaS-025 UI must handle `mfa.administration_required` through the existing Account security flow.

| Method | Path under `/api/v2/workspaces/{workspaceId}` | Behavior |
| --- | --- | --- |
| GET | `/notifications?page=1&pageSize=20&unreadOnly=false` | Current recipient's visible items, total filtered count, total pages and visible unread count. |
| GET | `/notifications/unread-count` | Current recipient's visible unread count. |
| PATCH | `/notifications/{notificationId}` | `{ "isRead": true }` or `{ "isRead": false }`; idempotent 204. |

List DTOs contain string IDs, kind, live project name/task title, UTC creation time and nullable UTC read time. No actor account metadata, source event ID or recipient selector is returned. Link construction can use `/companies/{workspaceId}/tasks/{taskId}` in the future inbox UI. These are identifiers, not authorization grants.

Items sort by descending notification ID. Page size is 1–100 and offsets are capped at 10000. Unknown/repeated query keys, invalid booleans, invalid IDs and absent/unknown PATCH fields are rejected. A list response computes items, filtered total and visible unread count in one repeatable-read snapshot. The unread count covers all visible unread items, even when pagination shows a subset. Separate HTTP requests naturally can observe intervening changes.

Every list/count/write uses the same visibility predicate: active current company membership, current project ownership or non-revoked project grant, matching company scope and live project/task. Loss of access or deletion suppresses both metadata and count. Names are resolved only after these checks, so stored history cannot leak a revoked title. Owners/Admins cannot read another recipient's inbox through company administration. Read/unread holds the same company gate as membership/resource writers; repeated mark-read preserves the first read time, and mark-unread clears it. Last committed read-state action wins; this low-impact state does not change task revisions or business audit.

Reactivation or a new project grant can make an existing recipient-owned row visible again if the resource still exists; current authorization remains authoritative. No past event is newly delivered to a replacement assignee. Data retention is not automated in this release. Review retention under the existing legal-hold policy before a broader rollout; no runtime DELETE permission is introduced.

## Migration and rollout

1. Back up the database and Data Protection ring. Use an isolated preview/maintenance window and a reviewed matching build.
2. Apply migration twelve, `20261112153116_UserNotifications`, through the migration identity. It adds an empty table/indexes/constraints, without changing old nullable scope, backfilling history or enrolling existing users.
3. Apply the new [runtime grants](../../deploy/database-roles.sql.example): SELECT/INSERT on UserNotifications and UPDATE on ReadAtUtc only. Checkpoint, mail and security-reader identities receive no notification grants. Apply grants before enabling the feature.
4. Deploy the matching API and regenerated frontend contract. Readiness compares the exact migration set; an older eleven-migration binary will not accept the new schema. For application rollback, first disable the flag on the same compatible binary. A binary/schema rollback needs the existing reviewed recovery procedure; do not blindly drop recipient data.
5. On the isolated API, explicitly configure:

```powershell
$env:WorkspaceReads__Enabled = 'true'
$env:WorkspaceWrites__Enabled = 'true'
$env:WorkspaceLifecycle__Enabled = 'true'
$env:Notifications__Enabled = 'true'
```

6. Assign a synthetic task to another active member who has project access. Verify that recipient sees one unread item, other accounts see none, and read/unread changes counts. Revoke the project grant and verify item/count disappear and mutation returns 404. Repeat with deleted task and removed company membership.
7. Record selected-host latency and access checks before enabling for users. The flag is not enabled in the Render manifest by this change, and no inbox UI is exposed yet. There is no new background process to configure.

For migrations, use the current reviewed build and commands in the [free-hosting checklist](../free-hosting.md), which now requires all twelve migrations. Keep private connection values out of command arguments and chat. Disable the feature to stop both new materialization and HTTP access without deleting persisted data.

## Verification

The MySQL suite covers atomic assignment/outbox/notification persistence, self-action suppression, default-off behavior, recipient isolation, company/project revocation, soft deletion, current names, concurrent read idempotency, unread count/list consistency, pagination/validation/CSRF, database uniqueness/composite references, rollback and historical replay suppression.

A published HTTPS rehearsal uses a separate restricted runtime identity in a disposable schema. It applies twelve migrations, verifies assignment/read/unread/count and revocation, and confirms that the database account can update read state but cannot retarget or delete inbox rows. Its temporary schema and database identities are removed afterward. See [release evidence](../evidence/saas024-notifications.json).

The OpenAPI inventory is 61 paths / 79 operations, with generated TypeScript updated. Hosted CI, public-host verification and commercial acceptance remain separate gates. Next: SaaS-025 inbox UI, bulk mark-read and per-user/company channel/event preferences.
