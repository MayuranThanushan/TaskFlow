# Company and project invitation lifecycle

SaaS-020, 5 November 2026. The API now supports pending invitations, intended-recipient preview/acceptance, resend, revoke and effective expiry. This is a default-off API foundation; invite/manage/accept screens are now available in [SaaS-021](invitation-ui.md). The invitation mail command uses the existing private local/staging capture sink. No SMTP/provider, public signup launch or real recipient sending is enabled.

## Authority and grant rules

| Scope | Manager | Requested grant | Acceptance |
| --- | --- | --- | --- |
| Company | Active Owner/Admin | Member or Guest | Adds that company membership only; no private project access. |
| Project | Active company Owner/Admin who currently owns the project | Editor or Viewer | Adds Guest company membership if absent, then the explicit project grant. An existing active company role is preserved. |

Project email invitations compose company administration and private-project ownership, as required by the permission matrix. Project owners without company administration can continue adding existing active company members through the existing project-membership API. Company administration does not override private project ownership. Company invitation listing includes only company invitations; project listing requires both company Owner/Admin and current project ownership. Managers must have a verified email to create/resend. Owner/Admin and project Owner cannot be granted by invitations. Existing active company members cannot receive another company invite; active project grants/ownership cannot be overwritten. Suspended/Removed company membership cannot be reactivated through acceptance. Explicit lifecycle reactivation/re-admission policy remains authoritative.

The intended recipient must sign in with the exact Identity-normalized verified email. A valid token alone grants nothing. Preview and accept do not require prior company membership: they authorize through the scoped invitation, verified account, token and current inviter. Mixed-company IDs, wrong accounts, unverified recipients and invalid tokens return a non-disclosing unavailable response. Preview reveals the company/project name and role only to that intended recipient. It creates no membership. Acceptance rechecks current inviter membership/role and current project ownership/deletion under the company gate. Removed/suspended/demoted inviters and transferred/deleted projects invalidate affected invitations immediately for authorization, reservation and capture purposes.

## API

All routes require a current stamped session, the three workspace rollout flags and `Invitations:Enabled=true`. Mutations also require CSRF and the actor governance rate limiter. The default flag returns 404; no legacy API bypass is introduced.

| Method | Path under `/api/v2/workspaces/{workspaceId}` | Purpose |
| --- | --- | --- |
| GET | `/invitations?page=1&pageSize=20` | Company invites, Owner/Admin only. |
| GET | `/invitations?projectId=<ID>&page=1&pageSize=20` | One project's invites, current project owner only. |
| POST | `/invitations` | `{ email, role, projectId? }`; returns 201 metadata. |
| POST | `/invitations/{invitationId}/resend` | `{ expectedVersion }`; returns updated metadata. |
| POST | `/invitations/{invitationId}/revoke` | `{ expectedVersion }`; returns 204. |
| POST | `/invitations/{invitationId}/preview` | `{ token }`; verified intended recipient only. |
| POST | `/invitations/{invitationId}/accept` | `{ token }`; consumes invitation and commits grants. |

There are six new operations on five paths: current inventory 53 paths / 71 operations. IDs and versions are decimal strings. Lists are bounded to pageSize 100 and offset 10000. Unsupported/repeated query keys and unknown/duplicate body properties are rejected. POST preview keeps token material out of URL/query logs. Normal session/CSRF, transport, no-store, safe Problem Details and trace behavior apply. Failure codes include invitation.unavailable/conflict/forbidden/email_unverified/resend_limited and quota.exceeded. No token/hash/ciphertext/lease is returned by creation, listing or resend.

A successful acceptance returns the scoped preview DTO and rotates invitation state to Accepted. A repeated/competing acceptance fails and creates no second grant or success event. Current resource access is still checked independently by existing project/task APIs.

## Tokens, state, capacity and audit

Tokens contain 32 cryptographically random bytes, encoded as 43 base64url characters. Acceptance stores/checks a SHA-256 digest using fixed-time equality. Plain tokens exist only inside a purpose-bound encrypted delivery payload and private captured mail. The payload binds workspace ID, invitation ID and stable delivery MessageId. Expiry is seven days from original creation using database UTC. Resend rotates the token and message ID, invalidates any old delivery lease and increments the invitation revision, without extending expiry. At most three resends are allowed, with a one-minute cooldown. Creation is capped at 20 per company per rolling hour and 1000 retained invitations. Local capture also retains its per-company hourly cap.

Pending invitations reserve a seat only if the intended email is not already an active company member. One pending normalized email per company is enforced by a nullable unique key across both scopes. Every invitation writer uses the same company row gate as membership/resource lifecycle writes. Admission and member reactivation count valid pending reservations. Acceptance releases its own reservation inside the transaction before admitting the member, so the last reserved seat can be consumed without exceeding 25. Multiple project invitations to the same email cannot reserve duplicate pending seats. A stale inviter/deleted project/expired invite is excluded from reservations immediately. The existing policy DTO's activeMembers remains a count of active members, not active-plus-reserved seats; SaaS-021 adds a separate reservedMemberSeats capacity count and UI presentation.

States are Pending, Accepted, Revoked and Expired. List responses compute Expired/Revoked when a stored Pending row is no longer valid; the next authorized create in that company materializes invalid pending states and clears the unique key. GET does not mutate history. Explicit terminal transitions clear the digest, encrypted payload, lease and pending-email key. Expired or revoked authority is never revived by resending or accepting an old token. Effective expiry does not depend on an expiry scheduler being alive.

InvitationTransition records are append-only and contain scoped IDs, actor, action, revision, UTC and correlation only. Create/resend/revoke/accept also append a versioned Invitation subject to the existing company audit and metadata-only transactional checkpoint queue. Those audit/export records contain no email address, token, hash, ciphertext or private titles. Manager-only invitation DTOs contain the recipient address for operational management; treat it as private account data. Storage-level composite references prevent a company invite from naming another company's project. The migration uses the existing nullable-scope project index, preserving legacy compatibility.

## Migration and local/staging setup

- [ ] Apply migration ten, `20261105141627_WorkspaceInvitations`, through the migration identity. It creates empty invitation/journal tables and a scoped project reference; it does not backfill invites or force legacy project scope non-null.
- [ ] Apply updated [API grants](../../deploy/database-roles.sql.example). Active member reactivation now checks invitation reservations even when the invitation flag is off, so the migration/grants must precede application deployment.
- [ ] Enable only in an isolated, reviewed preview with verified synthetic accounts:

```powershell
$env:WorkspaceReads__Enabled = 'true'
$env:WorkspaceWrites__Enabled = 'true'
$env:WorkspaceLifecycle__Enabled = 'true'
$env:Invitations__Enabled = 'true'
```

- [ ] Publish the worker and provision the separate [invitation-mail grants](../../deploy/invitation-mail-roles.sql.example), a private connection secret, the shared API Data Protection ring (application `TaskFlow`) and an ACL-restricted capture directory:

```powershell
dotnet restore TaskFlow.slnx --locked-mode
dotnet publish TaskFlow-BE/TaskFlow.Worker -c Release --no-restore -o .local/releases/worker
$env:DOTNET_ENVIRONMENT = 'Staging'
$env:TASKFLOW_SECRETS_DIRECTORY = 'D:\TaskFlow\.local\invitation-mail-secrets'
$env:DataProtection__KeysPath = 'D:\TaskFlow\.local\shared-api-keys'
$env:Email__Mode = 'LocalCapture'
$env:Email__CaptureDirectory = 'D:\TaskFlow\.local\mail-capture'
$env:Email__PublicOrigin = 'https://localhost:15173'
$env:Email__AllowedRecipients__0 = 'teammate@example.test'
$env:Email__PerCompanyHourlyLimit = '20'
dotnet .local/releases/worker/TaskFlow.Worker.dll --invitations-once
```

Capture is supported only in Development/Staging, not Production. Staging requires exact recipient allowlisting and HTTPS links; Development permits synthetic `.test` recipients. The command checks exact migrations and never migrates or opens a listener. It processes at most 25 due claims; schedule or rerun bounded batches under operations ownership. No automatic daemon/profile is enabled. Use the [SaaS-021 screens](invitation-ui.md) or typed API client to create/preview/accept. Captured links use `/invitations/accept#workspaceId=...&invitationId=...&token=...`; that frontend route now captures the fragment into tab memory and replaces the URL. Never put those secrets in logs, commits or support attachments.

The worker uses a short shared company lock to synchronize with lifecycle writers, then a delivery-row lock. It rechecks current inviter/project authority before leasing, releases all locks before capture, uses a 60-second random lease and a 20-second handler deadline, and checks workspace/message/lease/state/expiry after capture. A resend/revoke/accept during capture fences out acknowledgement. A private stale mail artifact can still exist; its old token is rejected by the API. This is at-least-once dispatch with stable-ID local capture deduplication, not exactly-once external sending.

Temporary failures retry after 5/10/20/40 seconds, at most five claims per delivery round; a resend is a new bounded round. Closed failure codes and delivery state remain visible to managers. Captured/dead-letter/cancelled delivery clears its encrypted payload. Delivery failure does not grant access or silently accept an invitation. The restricted worker can read recipient-bearing invitation rows and narrow membership/project authorization columns, update delivery fields and append transitions; it cannot change requested roles, token hashes, recipients, source state, membership, private project content, account credentials or history. [MySQL locking-read privileges](https://dev.mysql.com/doc/refman/8.4/en/innodb-locking-reads.html) permit the company shared lock without granting workspace update authority.

## Backup recovery

After verifying imported counts/history and before publishing recovered keys, the restore tool now atomically rotates account session stamps, revokes restored Pending invitations and cancels Pending/Leased account-mail jobs when those tables exist. Digests, pending-email keys, encrypted payloads and leases are cleared. An older snapshot therefore cannot revive a link revoked after that snapshot. Accepted memberships already in the snapshot are preserved. Operators must review restored access, then issue new authorized invitations/recovery requests; do not resume old token queues. Older pre-invitation backups remain supported. The recovery operation belongs in the restricted operator recovery receipt, rather than being misattributed to an invitation's original human actor.

## Verification and release gates

MySQL tests cover intended verified recipient, concurrent single use, project privacy/Guest+Viewer grants, wrong company/account, token rotation/revision/cooldown, expiry/inviter suspension, duplicate races, the last reserved seat, no suspension reactivation, audit rollback, append-only journal, composite scope and revoke during capture. A published HTTPS rehearsal uses actual separate API/worker grants, local captured links, resend/replay/accept, private-project read/denied Viewer writes and negative privileges. See [release evidence](../evidence/saas020-invitations.json).

No real provider/recipient sending, provider callbacks, production activation, hosted CI/CodeQL/container certification, tenant cutover, distributed fairness or automatic retention is claimed. Pending and terminal recipient addresses remain private retained metadata; archive/purge policy and key custody require selected-host review. SaaS-021 now supplies [invite/accept/manage-team screens](invitation-ui.md); next is SaaS-022 optional MFA and recovery policy.
