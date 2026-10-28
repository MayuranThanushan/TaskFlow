# Company and project lifecycle

SaaS-012, 28 October 2026. Company roles, project Viewer/Editor access, suspension, removal, departure, ownership transfers and a redacted governance audit are implemented as a default-off rehearsal. Customer rollout still requires coordinated migration, mandatory scope, reviewed host configuration and full tenant acceptance.

## Gates and compatibility

All lifecycle endpoints require **WorkspaceReads:Enabled**, **WorkspaceWrites:Enabled** and **WorkspaceLifecycle:Enabled**. All three default to false. The frontend also requires build-time `VITE_WORKSPACES_ENABLED=true` and `VITE_WORKSPACE_LIFECYCLE_ENABLED=true`.

Enabling the backend lifecycle flag closes **all legacy v1 project/task/member/dashboard/activity resource APIs**, including writes, with 404. Authentication, CSRF, profile and health remain available. The lifecycle UI redirects old resource URLs to company selection. This prevents legacy routes from bypassing suspension and Viewer permissions. Do not enable lifecycle alongside old binaries or concurrent legacy writers. Keep traffic closed while reconciling a migrated snapshot and switching matching frontend/API versions.

**Turning lifecycle off is not a data rollback:** it reopens legacy resource APIs with their old policy. After lifecycle mutations, maintain the cutover or restore the coordinated snapshot during maintenance. Nullable compatibility columns, operator custody recovery, invitation/rejoin flows, non-null enforcement and full SaaS-016 acceptance remain open. Do not run legacy-owner regrouping against intentionally created/transferred companies.

## Roles and privacy

Every request needs active membership in the explicit company. Company Owner/Admin never implies private-project access. Owner/Admin can manage eligible company affiliations; Admin can manage only Member/Guest and cannot appoint Admin/Owner or alter another Admin/Owner. Members can see an active ID/display-name directory (role/state values are redacted to null); Guests see collaborators only within shared projects. No team directory exposes email.

Private-project ownership remains authoritative in Projects.OwnerId. Owners manage project settings, grants, deletion and transfer. Editors can create/edit/assign tasks and change status. Viewers can read project/tasks/collaborators/activity but cannot mutate tasks or receive assignments. Company Guests can be project Editor/Viewer but cannot create or own projects. Legacy project `Member` remains an Editor-compatible wire/storage alias during migration; new UI grants explicitly choose Editor or Viewer. Company and project roles never combine across companies.

Project owners add only existing active company members. Adding a Viewer is atomic; there is no intermediate Editor grant. Explicit role changes and grants use server-derived scope/actor. Self-promotion is denied. Generic project mutation callbacks still cannot change ownership; transfers use a separate authorized boundary.

## Endpoints

All suffixes below start at `/api/v2/workspaces/{workspaceId}`. Writes require paired CSRF cookie/header, strict bodies and no query parameters. Read pagination uses canonical `page`/`pageSize`, defaults 1/20, maximum size 100; unsupported/repeated queries are rejected.

| Method | Suffix | Permission / behavior |
| --- | --- | --- |
| GET | /members | Owner/Admin: retained directory; Member: active directory; Guest denied |
| GET | /audit | Owner/Admin only; paginated stable IDs and governance changes |
| PATCH | /members/{userId}/role | Eligible administrator; Owner grant/revoke requires password |
| PATCH | /members/{userId}/state | Eligible administrator; Active/Suspended/Removed; Owner changes use transfer/demotion first |
| POST | /leave | Self; transfer active owned projects first; protect last Owner |
| POST | /ownership | Owner plus password; active non-Guest recipient becomes Owner, actor becomes Admin |
| PUT | /projects/{projectId}/members/{userId} | Project Owner; Editor/Viewer change for existing active grant |
| POST | /projects/{projectId}/ownership | Project Owner plus password; active non-Guest recipient becomes Owner; actor becomes Editor |

The existing scoped POST project-members request additionally accepts Editor/Viewer when lifecycle is enabled; Member remains its compatibility default. Roles use exact enum spellings and decimal-string IDs. Unknown fields, numeric IDs and workspace/actor injection are rejected. Missing/inactive membership or inaccessible private resources return 404; visible forbidden actions return 403; invariant conflicts return 409. Conflict responses never enumerate private project titles.

## Ownership and password proof

Never demote or remove the last active company Owner. Owner departures also require password proof and no active owned projects. Owner suspension/removal is deliberately a two-step workflow: password-protected transfer/demotion first, then ordinary membership management. A transfer to self, suspended/removed member, ineligible Guest or an existing company Owner is rejected. Project transfer does not make the old owner a company administrator; company transfer does not transfer private projects.

Sensitive role/leave/ownership routes are limited to ten requests per minute per authenticated actor in each API process and return 429 with Retry-After. They never automatically replay writes. Existing account lockout also applies; selected-host edge/distributed throttling remains an operational gate. Sensitive writes verify the current password **for that operation**, rather than trusting cookie renewal timestamps. Identity lockout checks run in a separate short-lived context so failed-password counters survive governance rollback. Passwords never enter governance/project audit or routine request logs and are cleared from UI state after completion, failure, cancellation or unmount. Two-factor-enabled accounts fail closed on this password-only proof; this release does not implement MFA/SSO or claim to satisfy a future MFA reauthentication policy.

## Transactions, departure and history

All writers acquire the existing workspace gate before current actor membership, project and affected rows. Role/state admission is checked again after waiting. This conservatively serializes company writes and owner-count decisions. Suspension, leave/removal, Viewer downgrade and concurrent assignment cannot restore an ineligible assignment after the lifecycle change commits. Failed audit persistence rolls back the membership, grants, assignments and events together.

Suspension clears assignments on active tasks, including completed tasks that are not deleted, while retaining project grants for possible reactivation. Reactivation does not restore assignments. Leave/removal additionally tombstones project grants using RevokedAtUtc and retains the company membership as Removed. Removed memberships cannot be reactivated through the suspension endpoint; a future consent-based invitation/rejoin workflow must not restore old grants automatically. Re-adding a revoked project collaborator resets the grant to the explicitly chosen role. Deleted task/project history and unrelated companies remain unchanged. Active owned projects must be transferred before ordinary departure/removal or demotion to Guest. Suspension may leave project custody unresolved; admins receive no implicit recovery access. No custody recovery/support break-glass endpoint is shipped.

WorkspaceAuditLogs records successful committed governance changes with actor/target/company/project IDs, action, old/new role or state, and UTC time. It never stores private names, task bodies, passwords or arbitrary request text. Failed requests remain in existing security/request diagnostics rather than being represented as committed changes. No-op changes create no event. Project activity also records role changes, transfers, assignment cleanup and grant revocations. Audit survives departure and soft deletion.

Migration `20261028143630_WorkspaceLifecycle` adds retained grant revocation, Removed membership state, audit storage and the two new project actions. It preserves the six existing scoped FKs and adds three explicitly managed audit scope FKs for actor membership, target membership and project. These complement the modeled global identity/workspace FKs; model-drift checks alone cannot verify custom SQL constraints. The audit project scope FK remains compatibility-stage SQL because its project reference is nullable. Adopt these names explicitly during later EF enforcement. Migrate with the one-shot migration identity, not EnsureCreated or web startup. MySQL partial DDL recovery requires reviewed forward repair or coordinated restore; Down is not production recovery.

The DbContext rejects updates/deletes to both history entities. The [runtime SQL grant template](../../deploy/database-roles.sql.example) now gives the application SELECT/INSERT on both histories and explicit UPDATE grants only on mutable tables; migration history remains read-only. Operators must apply/review actual host grants after migration. Test identities retain broader rights to provision/clean their disposable schemas; their privileges are not a production policy assertion.

## Rehearsal

After a reviewed migration/backfill on an isolated disposable target, enable matching flags in separate API/frontend terminals:

```powershell
# API
$env:WorkspaceReads__Enabled = 'true'
$env:WorkspaceWrites__Enabled = 'true'
$env:WorkspaceLifecycle__Enabled = 'true'
dotnet run --project TaskFlow-BE/TaskFlow.Api

# Frontend
$env:VITE_WORKSPACES_ENABLED = 'true'
$env:VITE_WORKSPACE_LIFECYCLE_ENABLED = 'true'
npm run dev --prefix TaskFlow-FE
```

For a preview image, pass both public build arguments to the app build; default Docker builds keep both false. Supply all three backend flags only in the isolated deployment override. Do not treat build arguments as runtime switches or put credentials in VITE_ variables.

See [verification evidence](../evidence/saas012-lifecycle.json) and the [browser setup](../../tests/browser/README.md). Nine lifecycle MySQL HTTP cases plus five Viewer policy cases extend the existing suite; frontend tests cover confirmations, pending-write locks, Owner password handling, role visibility, Viewer task controls and legacy redirects. The published lifecycle journey uses a restricted synthetic fixture affiliation only until real invitations exist.

Next is SaaS-013 versioned business audit and restricted security events. Invitations follow later in R2. Selected-host privilege verification, fresh hosted CI/CodeQL/container results, invitation/rejoin/custody workflows, non-null scope and final company cutover remain explicit gates.
