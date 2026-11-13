# SaaS-026: due reminders and daily inbox digest

13 November 2026. In-app scheduling is implemented behind a separate default-off flag. This is not email delivery, public deployment or hosted acceptance. SendGrid staging account-security/invitation mail is unchanged.

## User behavior

Company Inbox ? Notification preferences offers due reminders, daily digest, IANA timezone and local hour. Both schedule switches default off; timezone defaults UTC and hour 09:00. Company/user ownership, revision conflicts, CSRF and existing administrator MFA step-up rules apply. Older clients omitting all four scheduling properties preserve existing settings; a partial group is rejected.

A due reminder appears at the deadline, with up to 24 hours of outage catch-up. The daily digest adds individual task cards for overdue work and work due within the next 24 hours. It runs during the hour following the selected local hour; missed days are skipped. It is an inbox digest, not a consolidated email or a single summary item. Tasks without deadlines are excluded. Delivery time depends on scheduler cadence and company size.

Times are stored in UTC. Missing DST times move forward to the first valid minute; repeated times use the first occurrence. A digest key includes recipient, company, task and local calendar date, preventing a second delivery during a repeated hour. Changing timezone can change which calendar date is eligible; already delivered dates remain deduplicated. Due reminders use the precise persisted deadline as their occurrence identity.

`deliveryStatus: InAppDelivered` and the creation time mean an inbox row committed. Read state remains independent. There is no pending-email status, delivery receipt, bounce tracking or claim that an email reached a mailbox.

## Safety and work ownership

The worker locks the same company row as resource, membership and preference writers. It reads current tasks, active company membership, project grants and preferences inside that transaction. Source titles/content are never copied into work payloads. Inbox reads, unread counts and writes additionally hide scheduled rows when the task is completed, reassigned, deleted, rescheduled or no longer accessible. Muting prevents future deliveries and preserves authorized existing history.

A locking transaction is the work lease: it cannot survive a disconnected/crashed database session. The cursor and at most 200 inserts commit together. A failure rolls everything back, so the next invocation retries safely. Unique `(WorkspaceId, RecipientUserId, ScheduleKey)` enforces deduplication in the database. Concurrent workers use `SKIP LOCKED` and report Busy rather than waiting. There are no external side effects, detached leases, automatic poison-message retries or checkpoint/outbox delivery authority in this path.

Each invocation scans at most 100 raw task IDs, advancing a durable per-company cursor even across ineligible/deleted tasks, then wrapping to zero at the end. A linked 20-second cancellation deadline bounds a batch. An operator must invoke every enabled company often enough to complete a full scan inside the one-hour digest window. For N tasks, budget at least ceil(N/100) calls per sweep (an exact multiple may need a final empty wrap call). Watch scan lag and failure logs; this bounded implementation does not promise immediate delivery at arbitrary scale.

## Operator checklist

- [ ] Back up the database and verify recovery to a new schema. Apply migration fourteen, `ScheduledNotifications`, before deploying this API/worker. Down migration intentionally refuses to destroy historical scheduling data; disable the feature and roll forward, or restore a pre-release backup to a new schema.
- [ ] Apply the expanded preference-column runtime grants in [database-roles.sql.example](../../deploy/database-roles.sql.example). API identity needs no cursor privileges.
- [ ] Privately create a separate scheduler account and apply the commented scheduler grants from that template. It can read current authorization/task/preferences and append notifications/update cursors. It has no identity secrets, account-mail payloads, outbox replay, notification mutation/delete or migration privileges. `UPDATE(Id)` on Workspaces permits the MySQL locking read; it grants no policy/name writes.
- [ ] Use MySQL 8.4 with verified locking semantics. TiDB/other compatible hosts remain unapproved until `FOR UPDATE SKIP LOCKED` and the concurrent permission tests pass on that host. Do not activate on the free Render/TiDB deployment by assumption.
- [ ] Supply the private scheduler connection using a protected environment or key-per-file directory. Never use the API, backup or migration identity. No email credentials or Data Protection key ring are needed.
- [ ] Set `WorkspaceReads__Enabled=true`, `WorkspaceWrites__Enabled=true`, `WorkspaceLifecycle__Enabled=true`, `Notifications__Enabled=true` and `Notifications__SchedulingEnabled=true` for the worker only. Existing API and frontend inbox flags still govern inbox availability.
- [ ] Publish the worker and run an explicit company batch, replacing `<company-id>` with the positive numeric ID:

```powershell
Set-Location D:\TaskFlow
dotnet publish TaskFlow-BE/TaskFlow.Worker --configuration Release --output .local/releases/notifications-worker /p:RestoreLockedMode=true
dotnet .local/releases/notifications-worker/TaskFlow.Worker.dll --notifications-once <company-id>
```

- [ ] After a disposable company verification, configure your own scheduler to invoke enabled company IDs repeatedly (for example every minute, sufficient calls per sweep). No web endpoint enumerates companies or starts privileged work. A result with NextTaskId greater than zero means continue the sweep. Zero means the next invocation starts a new sweep.
- [ ] Monitor JSON event 3501 `notifications.batch_completed`, Scanned, Delivered, NextTaskId and Busy. Startup/failure logs withhold private diagnostics. Inspect `NotificationScheduleCursors.LastBatchAtUtc` via an operations identity for stale successful batches; this is not a provider receipt.
- [ ] Stop scheduling by removing its flag/invocations. This preserves inbox history and read state. Backup/recovery must include cursors and notification occurrence keys together; restoring both prevents replay duplicates. An old backup naturally replays deliveries newer than that backup inside the restored database only.

Free hosting remains off, no background scheduler resource is provisioned, no paid service or real email is activated. See [release evidence](../evidence/saas026-scheduling.json) for local verification and remaining hosted gates.
