# SaaS-025: company inbox and recipient preferences

12 November 2026. This release adds the inbox UI, bounded bulk mark-read and self-owned notification preferences within each company. Both the backend notification flag and frontend inbox flag remain default off. No email, reminder scheduler or public-host activation is added.

## Inbox experience

With the workspace/lifecycle preview and inbox flag enabled, every active company member gets an Inbox navigation link with the currently known unread badge. Badge refresh preserves keyboard focus. The company inbox supports All/Unread filters in the URL, twenty-item pages, local timestamps, explicit read/unread actions, safe task links, manual/focus refresh, empty/loading/error states and recovery from invalid or out-of-range bookmarks. The interface never fabricates a zero total while loading.

“Mark page as read” submits only the unread IDs displayed on that page. It does not mark unseen pages or later arrivals. Individual and bulk writes are single-flight and never retried automatically. A rejected stale/inaccessible selection clears private rows and offers a fresh inbox reload; no old titles are reused during recovery. Session expiry redirects to sign-in. Company/account changes discard lists, drafts and pending requests; a write already transmitted remains bound to its original company.

Titles are plain escaped React text from the existing authorized API. Deep links preserve opaque string IDs and remain inside the selected company. The real destination rechecks task access. Desktop/mobile layouts, native keyboard preferences and live status/error feedback are included. No notification token, account email or raw outbox fact is exposed in the inbox.

## Preferences

Preferences are per user **within the selected company**, not company-wide controls and not global account defaults. Only the stamped current account may read/change its settings. Missing rows default to In-app enabled and Tasks assigned to me enabled, with revision `0`. A saved row starts at revision `1`. Writes check `expectedVersion` under the shared company gate; stale tabs return 409 without overwriting the newer settings. No-op saves preserve the revision. The UI keeps a conflicting draft and reloads current preferences only after explicit action.

In-app is the available channel and TaskAssigned is the currently supported event. Disabling either stops future assignment materialization atomically with assignment admission. Existing inbox history remains visible when authorized, and re-enabling does not backfill muted events. Self-action suppression and current company/project checks remain in place.

Task-assignment email is unavailable and cannot be enabled through this API. The disabled email control explains that account security and invitation email are separate. These preferences do not suppress verification/recovery/invitation messages or enable SendGrid. Higher fan-out events, actual task email, reminder/digest scheduling and additional channels are future releases.

## API additions

All operations remain under `/api/v2/workspaces/{workspaceId}/notifications`, with active membership, the existing notification/workspace flags, no-store, stamped sessions and current resource authorization. Mutations retain CSRF, actor rate limits and the existing administrator MFA policy where enabled.

| Method | Suffix | Contract |
| --- | --- | --- |
| POST | `/read` | `{ notificationIds: string[] }`, 1–100 distinct positive IDs. All must be visible to this recipient. Atomic 204; any inaccessible selection returns 404 without partial updates. |
| GET | `/preferences` | `{ inAppEnabled, taskAssignedEnabled, emailAvailable: false, version }`. No target-user selector. |
| PUT | `/preferences` | `{ inAppEnabled, taskAssignedEnabled, expectedVersion }`; booleans required, revision is a canonical nonnegative decimal string. Returns current DTO or 409 conflict. |

Unknown fields, repeated/unsupported query keys, duplicate IDs (including equivalent leading-zero forms), empty/oversized selections and invalid revisions are rejected. List/count/single-read behavior from [SaaS-024](notifications.md) remains unchanged. Public contract inventory: 63 paths / 82 operations.

## Migration and rollout

1. Back up and review the [current deployment checklist](../free-hosting.md). Apply migration thirteen, `20261112160350_NotificationPreferences`, with the matching migration identity/build. It creates an empty preferences table and does not backfill accounts or modify existing notification history.
2. Apply the updated [runtime grants](../../deploy/database-roles.sql.example): SELECT/INSERT on NotificationPreferences and UPDATE of InAppEnabled, TaskAssignedEnabled and Version only. Identity keys cannot be updated and runtime DELETE is not granted. Mail/checkpoint/security-reader identities gain no access to preferences.
3. Deploy the matching API and frontend. Readiness checks the exact thirteen-migration set, even when notifications are disabled. Disabling flags on the compatible binary is the immediate feature rollback; an older binary/schema rollback follows the existing reviewed recovery process.
4. For an isolated local preview, set these in the API terminal before starting the reviewed API:

```powershell
$env:WorkspaceReads__Enabled = 'true'
$env:WorkspaceWrites__Enabled = 'true'
$env:WorkspaceLifecycle__Enabled = 'true'
$env:Notifications__Enabled = 'true'
```

5. In the frontend terminal, enable the matching build-time flags before running/building Vite:

```powershell
$env:VITE_WORKSPACES_ENABLED = 'true'
$env:VITE_WORKSPACE_LIFECYCLE_ENABLED = 'true'
$env:VITE_NOTIFICATIONS_ENABLED = 'true'
npm --prefix TaskFlow-FE run dev
```

For a portable image, the frontend Docker stage also accepts `VITE_NOTIFICATIONS_ENABLED`; its default is false. The Render Blueprint explicitly keeps it false. Environment changes require a frontend rebuild, not just an API restart. No paid worker, periodic polling service or scheduler is needed for this inbox.

6. Assign a synthetic task to another active project member. Verify their inbox and badge, read/unread and page actions, mute/re-enable behavior, another company/account's isolation, and revocation/deletion handling. Record selected-host latency and accessibility before rollout. The feature remains a reviewed preview; provider-delivery/commercial/hosted-CI gates are separate.

## Verification and operating notes

MySQL tests cover defaults and actual persisted false values, concurrent stale preference writes, self/company isolation, muting each supported channel/event, retained history/no backfill, explicit bulk selection/new-arrival exclusion, idempotency, all-or-nothing rejection, CSRF, input bounds and suspended membership. Frontend tests cover routing, URL filters/paging, literal text/opaque links, read/bulk behavior, retry states, conflicts, expired sessions, late responses and company switches.

The published HTTPS browser journey checks recipient read/unread/bulk/filter/task links, keyboard settings, persisted mute/re-enable and account switching under separate restricted runtime grants. Desktop/mobile axe scans cover unread, open preferences and another recipient's empty inbox; these automated checks are not a substitute for manual assistive-technology review. See [release evidence](../evidence/saas025-inbox.json) and [browser setup](../../tests/browser/README.md).

Preferences affect future materialization and do not purge data. Review retention/legal hold and restored preferences under the existing recovery runbook. Support must not turn a revoked notification back into an access grant or overwrite another user's preferences. New events arriving after a page action remain unread; that is expected. Account security is the recovery path for an administrator step-up error.

SaaS-026 now extends this inbox with [due reminders, daily digest task cards and delivery status](notification-scheduling.md). Scheduling is separately gated and defaults off. Next task: SaaS-027 comments and mentions.
