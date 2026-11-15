# SaaS-027: task comments and safe mentions

15 November 2026. Local implementation; disabled by default. This release adds a flat task discussion, bounded project collaborator search, explicit mentions, in-app mention notifications and metadata-only revision history. It does not activate public hosting, email or automatic content purging.

## Permissions and privacy

All routes are under `/api/v2/workspaces/{workspaceId}/tasks/{taskId}/comments`. They require the three workspace flags plus `Comments:Enabled`. A task visibility check runs before JSON/query binding, then the service rechecks current active company membership, project access and live task/parent state in its own transaction. Company administrators have no private-project override. The existing administrator MFA step-up policy covers these scoped mutations.

Current project collaborators can read comments and their revision metadata. Guests and project Viewers are read-only. Other active collaborators can post. Only the author edits; the author or current project owner can delete. Deletion is confirmed in the UI. PUT and DELETE require an explicit positive decimal-string `expectedVersion` in the JSON body; stale versions return 409 `resource.conflict`. A no-op edit produces no revision or notification. Up to 100 text revisions are allowed, with a final deletion still permitted. There is no restore or rewrite of another person's text.

Plain text is trimmed and limited to 5000 characters. Tabs/newlines are allowed; other control characters are rejected. React renders text directly; HTML, Markdown and URLs never become executable HTML or automatic links. Discussion is newest-first, 20 rows/page, page 1-51; a task has a maximum of 1000 comments including tombstones. Revision history is capped at 101 metadata entries per comment.

## Mentions

The picker performs an explicit name search (2-100 characters), returning at most 10 current active company/project collaborators, with display names and opaque IDs only. No email, global user search, foreign-company member directory or implicit access grant is exposed. The candidate endpoint rechecks task access on every request. A changed/revoked resource hides the discussion; account/company/task changes discard prior scope state and cancel outstanding requests.

Canonical `@[userId]` tokens identify mentions. IDs are positive signed BIGINT decimal strings without leading zeroes; at most 10 distinct recipients are allowed. Repeated tokens deduplicate; bare `@name`, emails and unsupported token syntax remain literal text and do not resolve. Recognized invalid IDs or recipients outside the active project produce the same generic validation failure. The server validates the entire selection before saving anything. Former collaborators are not resolved to current names on comment reads.

New non-self mentions create `TaskMention` inbox rows in the same company transaction as the comment, revision metadata and metadata-only outbox intent, when Notifications is enabled and the recipient has not muted In-app notifications. The assignment-specific switch does not mute mentions. Edits notify only newly added recipient IDs; existing mentions/no-op edits never replay. Removing and later adding a mention is a new notification. Notifications link to the task, do not store comment text and become invisible if the source comment is deleted or the task/project/company becomes inaccessible. Muting preserves authorized history and suppresses future rows. No mention email is sent; [personal watchers, grouping and watcher noise controls](task-watchers.md) are added by SaaS-028.

## History and retention

`TaskComments` stores the current body. Editing replaces it; no earlier text snapshot is retained. Soft deletion clears the body and keeps a tombstone. `CommentRevisions` appends actor, version, action and UTC time only. Neither raw bodies nor names enter outbox intent. Comment history is read in the discussion; it is not added to the existing project activity stream or company audit export. Revision updates/deletes and physical comment deletion are blocked by the application/runtime grants.

This is not a promise to erase prior backups, logs at an external host, copied browser text or third-party exports. Backups retain the database at their capture time under the existing backup policy. No automatic purge of comments/revisions is introduced; a future purge/export policy must respect legal holds, foreign keys and the approved retention process. Keep comments, revision metadata, outbox and notification pointers together during backup/recovery.

## Rollout checklist

- [ ] Back up and verify recovery to a new schema. Apply migration fifteen (`20261115061917_TaskComments`) before deploying this API.
- [ ] Apply the new runtime grants in [database-roles.sql.example](../../deploy/database-roles.sql.example): SELECT/INSERT comments and revisions; UPDATE only Body, Version, EditedAtUtc and DeletedAtUtc on comments. No retarget, delete or revision-update grant. Existing outbox producer/inbox grants remain sufficient; schedulers get no new content/history access.
- [ ] Use the private migration connection, never the API identity, for migrations. The scoped task FK is added explicitly because legacy nullable task scope cannot be an EF principal key.
- [ ] Build a reviewed frontend with `VITE_COMMENTS_ENABLED=true` and enable `Comments__Enabled=true` alongside the existing workspace flags. Set `Notifications__Enabled=true` and its frontend flag only if the company inbox is part of the preview. Comments work without notifications.
- [ ] Rehearse author/reader/outsider behavior, edit conflict, deletion, mention mute and revocation using disposable data before activation.
- [ ] Disable comments by switching the flags off. The down migration refuses to destroy history; roll forward or restore a verified pre-release backup to a new schema. No live/shared database is migrated automatically by this release.

The free Render blueprint keeps both comment flags false and adds no paid resources. See [local release evidence](../evidence/saas027-comments.json); hosted CI/container acceptance and manual assistive-technology review remain separate gates.

<!-- current-documentation-index -->
Current implementation, release boundaries and rollout gates: [documentation index](../documentation-index.md). Release-specific counts and follow-ups above describe their recorded release.
