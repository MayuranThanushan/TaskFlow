# SaaS-028: task watchers, grouping and noise controls

15 November 2026. Local implementation; watcher delivery is disabled by default. This release adds personal task watching, a grouped inbox and timing controls for watched updates. No public hosting, email, paid worker or live/shared database migration is activated.

## Personal subscriptions

The task page offers Watch task / Stop watching task when the frontend watcher flag is enabled. GET and PUT `/api/v2/workspaces/{workspaceId}/tasks/{taskId}/watcher` expose only the current user's subscription, never a watcher directory/count or actor selector. Default is false with revision `0`. PUT requires `isWatching` and a captured decimal-string `expectedVersion`; concurrent stale writes return 409. Repeated requests at the current state/revision are no-ops.

Any currently authorized reader, including a project Viewer or company Guest, may manage this personal setting. Watching grants no resource access. Active company membership, current private-project access and live task/parent state are checked before binding and again inside the company write gate. Company administrators do not gain a private-project override. Existing administrator MFA step-up rules apply to personal scoped writes too.

Watching affects future new comments and task edits, assignments/unassignments and status changes. Comment edits/deletions do not generate watcher noise. Task deletion hides existing source rows through current visibility checks. Self-actions are suppressed. Direct assignment or mention notifications take precedence over a watcher notification for the same recipient/source event. Historical persisted outbox replay never discovers new watchers or changes the intended recipients.

At most 90 stored subscribed rows per task bound watcher fan-out. A comment can additionally have at most 10 direct mention recipients, so its combined audience is at most 100; overlap is delivered once. Subscription admission and fan-out serialize through the company gate. Revoked users cannot receive updates, but their retained subscribed rows still count toward the cap until explicitly unwatched after access restoration or handled by an approved retention operation. The current pilot also caps active company members at 25. This does not claim unbounded enterprise fan-out.

Unwatching changes the subscription revision, fencing pending work captured under the old revision even if the user re-subscribes before delivery. Already delivered, still-authorized inbox history remains. A restored project/company grant does not replay events from a revoked interval. The deferred worker additionally checks for intervening suspend/remove/leave and project-removal/ownership audit facts after its capture-time audit-ID watermark, so revoke-and-restore between batches cannot revive an old pending update. This covers normal audited application mutations; privileged direct database changes remain outside that audit contract and current access is still rechecked.

## Timing and mute controls

Inbox preferences add `watcherDeliveryMode` = Immediate, Digest or Off, plus `quietStartHour` and `quietEndHour`. Supplying mode replaces all three noise values; omitting mode preserves the existing noise settings. Hours are both null or distinct integers 0-23. Existing revision conflict handling and company/user scope apply.

Immediate watcher updates arrive at source commit unless quiet hours defer them. Digest uses the existing daily digest switch, local hour and IANA timezone; choosing Digest enables that existing digest setting in the UI, including its assigned-deadline digest behavior. Daily digest cannot be turned off while watcher mode remains Digest. A digest slot inside quiet hours moves to the first quiet end, and its one-hour delivery window moves with it. Overnight intervals and DST use the existing deterministic gap/fold policy. Missed digest windows move to the next eligible slot; pending watcher work expires after seven days.

Turning In-app notifications off or watcher mode Off cancels pending watcher intent in the preference transaction, so re-enabling does not replay it. Timing changes reschedule pending intent using the current settings. A task watch remains a separate change-feed subscription from direct assignment alerts. Direct mentions and direct assignments retain their own event preferences/delivery rules; watcher quiet hours do not delay those explicit notifications. In-app mute still governs all ordinary inbox delivery. No security/invitation mail policy changes.

Deferred delivery requires the separate in-app scheduler and its existing scheduling flag. Without a running scheduler, immediate non-quiet events work, but queued work waits; this is stated in the UI. No independent hosted cron/resource is provisioned by this release.

## Queue authority and traceability

`TaskWatchers` stores current personal subscription state. `WatcherNotificationIntents` stores only scoped source IDs, recipient, subscription revision, audit watermark, UTC availability and delivery state. It contains no task title, comment body, email address or rendered content. Pending -> Delivered or Suppressed is committed atomically with the inbox row. Suppression codes are closed: Expired, Unwatched, Revoked, Unavailable, Muted. There is no external email side effect, detached lease or user replay endpoint.

The scheduler locks one company row with SKIP LOCKED, then processes at most 100 due watcher intents plus the existing bounded reminder scan inside its 20-second transaction deadline. It rechecks current subscription/version, active membership, private-project/task access, source-comment visibility, revocation watermark and current preferences. A crash rolls back cursor, marker and inbox insertion together. Unique recipient/event keys prevent duplicate inbox rows on retries. Future rows are filtered by AvailableAtUtc; rescheduled quiet/digest rows move out of the due prefix, preventing permanent head-of-line blocking. Event 3501 now includes WatcherScanned, WatcherDelivered and WatcherSuppressed.

Intent and notification recipient/event keys retain the database source-event link. Delivered history and suppressed intent remain available to approved operations/recovery processes; no new public queue/outbox endpoint or raw private payload is exposed.

## Grouped inbox

Inbox -> By task reads `/notifications/groups`, grouping only currently visible source notifications by task. Titles and access are live projections. Counts are source updates, not watcher numbers. The unread badge remains a source-notification count. Groups do not merge/delete source rows or change read state.

View updates opens the existing flat inbox with an opaque `taskId` filter; individual notification IDs/kinds/times remain independently reviewable and read/unread actions target explicit source IDs. Mark page as read never includes unseen arrivals or an entire unbounded group. Deleted source comments, revoked projects, deleted tasks and removed memberships are excluded consistently from source lists, group counts and read writes. Group paging is bounded like the inbox. The grouped view loads on demand with an explicit reload/flat-view fallback for missing deployment chunks.

## Operator rollout checklist

- [ ] Back up and rehearse recovery into a new schema. Apply migration sixteen (`20261115073915_TaskWatchersAndNoiseControls`) and all earlier migrations before deploying this binary.
- [ ] Apply [runtime and scheduler grants](../../deploy/database-roles.sql.example). Runtime may read/insert subscription/intent metadata, update only subscription state/version, and reschedule/cancel pending recipient intent through scoped services. It cannot retarget/delete subscriptions or intent.
- [ ] The optional scheduler account needs metadata reads on watcher rows, source outbox, only comment identity/deletion columns, and only audit-ID/scope/actor/target/action columns; it must not read comment Body or identity secrets. Grant state/availability updates only on watcher intent; retain existing inbox-insert/cursor grants. Do not use migration/root/API credentials for the worker.
- [ ] Enable existing workspace and Notifications flags plus `Notifications__WatchersEnabled=true` for the reviewed API/worker preview. Compile the frontend with `VITE_TASK_WATCHERS_ENABLED=true`. Both watcher flags remain false in the free Render blueprint.
- [ ] Deferred work additionally requires `Notifications__SchedulingEnabled=true` and repeated explicit `--notifications-once <company-id>` invocations under the existing scheduler runbook. Budget enough batches for reminder sweeps and at most 100 due watcher rows per call; monitor deferred backlog/age and failures. Selected-host locking semantics must be verified before activation, especially on TiDB.
- [ ] Verify subscribe/unsubscribe, guest/viewer personal control, source delivery, group drill-down, mute/no-backfill, quiet/digest timing, revocation and restricted account behavior in a disposable company.
- [ ] Disable the watcher flags/invocations to stop new watcher capture/delivery. Keep all subscription, source event, intent and inbox tables together during backup/recovery. Down migration refuses to destroy this history; roll forward or restore a verified earlier backup to a new schema.

See [release evidence](../evidence/saas028-watchers.json). Hosted/container acceptance, real-provider delivery and manual assistive-technology review remain external gates. Next: SaaS-029 revocation races, delivery reliability/load and operational alert verification.

SaaS-029 now adds [delivery reliability, synchronized revocation races, bounded backlog verification and private alert checks](delivery-reliability.md). Selected-host activation remains gated.
