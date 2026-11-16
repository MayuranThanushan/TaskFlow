# SaaS-034 - saved views and persisted preferences

In My work, a project task list, or a project board, expand **Saved views & preferences**. Apply your filters first, enter a name and save. Transient page numbers are excluded. Choose a saved view to open it, get its share link, replace it with the current filters, or confirm deletion. Saved list and board targets preserve that layout.

## Visibility and ownership

Personal views belong to their creator. Sharing a personal URL does not change visibility. Owners and admins can create and manage company-shared views; members can open shared definitions only when they can access the target project. A company admin has no override for private projects. Work views without a project apply to the caller's own authorized projects; "mine" always means the caller, not the creator.

Every list, resolve, save and preference request rechecks active company membership. Project targets are reauthorized. A saved filter never grants task/project access. An inaccessible or deleted project is not silently removed from a saved definition to broaden the result. Unknown/deleted filter fields are rejected when a view resolves. Supported filters match the current My work and task-list contracts; boards currently save canonical layout, without custom board filters.

## Defaults and conflicts

**Use as default** persists a self/company default for exactly one context: `work`, `tasks:{projectId}`, or `board:{projectId}`. On entering that screen without URL filters, the default is resolved with current access checks. Explicit URL filters take precedence. Reset stays reset for the current visit. Filters/layout persist as an explicit saved snapshot; there is no automatic saving of every navigation change.

A deleted, newly private or retargeted default reports unavailable. Choose another default or clear it. Requests from other accounts cannot read or change your preference. Updates, replacement, deletion and preference changes use captured revisions; stale writes return 409 and require review. New preferences use expectedVersion `0`. Writes are not automatically replayed after conflicts or network failures. Saving or opening a view does not change tasks.

## API and storage

- `GET/POST /api/v2/workspaces/{workspaceId}/views`
- `GET/PUT/DELETE /api/v2/workspaces/{workspaceId}/views/{viewId}`
- `GET/PUT /api/v2/workspaces/{workspaceId}/view-preferences/{context}`

Create/update fields: name (trimmed, maximum 100), surface work/tasks/board, optional projectId (required for task lists and boards), shared boolean and a bounded filter dictionary. Updates/deletes require expectedVersion; preference PUT accepts expectedVersion and nullable savedViewId. Read paging defaults to 20, maximum 100, name/ID order. Storage is capped at 100 definitions per creator/company. Inaccessible project definitions remain retained and count toward that limit; retention/cleanup is separate product work.

Workspace and actor membership rows lock before mutations, in the same order as lifecycle writes. Shared create/update/delete records redacted SavedView audit subjects and transactional outbox entries; names and filter text are excluded. Personal view/preference changes do not enter company-shared audit history. Preference references deliberately allow deleted defaults, which resolve as unavailable rather than preventing deletion.

Apply migration **20261116143627_SavedViews**, the eighteenth migration, using a migration account before deploying the binary. No hosted/shared database is migrated by this release?s local tests. Review runtime grants for SavedViews (SELECT/INSERT/UPDATE/DELETE) and ViewPreferences (SELECT/INSERT/UPDATE), using the existing least-privilege runtime account. Backups include both tables and their private filter text. Current contract: **76 paths / 102 operations**. Existing workspace read/write gates apply. Production defaults remain disabled unless explicitly configured.

## Local workspace 404 repair

The local `GET /api/v2/workspaces?page=1&pageSize=20` route existed but returned 404 when WorkspaceReads was disabled. Development now enables WorkspaceReads, WorkspaceWrites and WorkspaceLifecycle together. Vite development defaults to matching company navigation when its flags are unset. Explicit environment settings still override defaults; old processes need restarting. The local frontend environment template now enables the two company flags. Production build/server defaults stay opt-in.

See the [README checklist](../../README.md) for manual commands. Use the same Development environment and HTTP proxy target on both terminals. Do not point Vite at a different/old API process.

## Verification and next work

See [release evidence](../evidence/saas034-saved-views.json) for measured tests. The isolated browser review uses synthetic APIs, not hosted accounts. Selected-host migrations, runtime grants, CI, capacity and human assistive-technology acceptance remain separate gates. SaaS-035 verifies board scale, conflicts, accessibility and adoption.

[Current documentation index](../documentation-index.md)
