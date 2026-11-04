# Account verification, recovery and reauthentication

SaaS-019, 4 November 2026. TaskFlow now supports explicit email verification, generic password recovery, password reset/change and five-minute reauthentication. Global identity remains separate from company authorization. Password change and reauthentication work with the existing session configuration. Email-based workflows are a default-off Development/Staging preview using encrypted queued requests and the private local capture sink; no SMTP/provider or real customer sending is enabled.

## User flows

The sign-in page links to password recovery and email verification. A new Account security page is linked from Profile; it shows verification state, provides current-password proof and changes the password. Password inputs clear after success and the form is discarded when the active account changes. In-flight form requests are cancelled on unmount/account switch. New passwords use the existing 12-character/uppercase/lowercase/digit/symbol policy and a confirmation input. A password reset/change signs out every session; the user signs in again without automatic login.

Verification and reset email links carry the account reference and protected token in the URL fragment. The frontend immediately replaces that URL and retains the token only in component memory. It never stores the token in local/session storage or shows it in the page. Actions require an explicit button submission with CSRF-protected JSON; opening a link does not confirm/reset the account. Reloading after fragment removal requires reopening the original private email link. Expired/used/foreign/malformed links offer a request-new-link path.

Registration remains a separate create/sign-in flow; use the Verify your email link to request confirmation. Confirmation is optional by default for compatibility with existing accounts. Review a migration/onboarding plan before requiring it. Email services report unavailable when the preview is disabled; current-password change and proof remain available.

## API and security contract

| Operation under `/api/v1/auth` | Access | Result |
| --- | --- | --- |
| GET `capabilities` | Public | Whether email preview/confirmed-email enforcement is enabled. |
| GET `security` | Authenticated | Only the current account's confirmation and recent-proof expiry. |
| POST `request-verification` | Public + CSRF + auth rate limit | Empty 202 for eligible, absent, already-confirmed, cooling-down or capped subjects; 503 when email preview is disabled. |
| POST `forgot-password` | Public + CSRF + auth rate limit | Same empty 202 for known/unknown/unverified/cooling-down/capped subjects; 503 when disabled. |
| POST `confirm-email` | Public + CSRF + auth rate limit | 204 once; generic 400 for invalid/expired/replayed/wrong-purpose/wrong-account links. |
| POST `reset-password` | Public + CSRF + auth rate limit | 204 once; invalid links/passwords receive bounded validation errors. |
| POST `change-password` | Authenticated + CSRF + auth rate limit | Current-password proof, password update and global session invalidation. |
| POST `reauthenticate` | Authenticated + CSRF + auth rate limit | Current-password proof; renews a session carrying five-minute recent-authentication metadata. |

All endpoints retain no-store, transport policy, safe Problem Details and trace IDs. Inputs reject unknown/duplicate body fields. Passwords/tokens are write-only in OpenAPI; account IDs remain decimal strings. Profile DTO stays compatible (ID/name/email only); verification metadata has its own self-only endpoint. Inventory is now 48 paths / 65 operations. The application does not return mail payloads, token strings or job readers through HTTP.

ASP.NET Core Identity's Data Protection token providers are registered with a one-hour lifespan. An additional protected action envelope binds account ID, purpose and a server-clock expiry; this supports deterministic expiry tests and never extends the inner framework token. Reset and confirmation take a current user-row lock, reload the account after acquiring it and verify the framework token against the current security stamp. Successful reset/change/confirmation and their safe security audit commit together. Confirmation rotates the stamp too, so it cannot be replayed. Concurrent confirmation/reset cannot both consume the same stamp. Password-policy/invalid-token failures roll back. Pending/leased account jobs are cancelled in the same invalidation transaction; logout also cancels them.

The existing cookie validator checks the security stamp on every request. Old device sessions fail on their next request immediately after reset/change/confirmation. No session is issued by reset/confirmation. Wrong current-password attempts use Identity's lockout-on-failure path and are audited without passwords. MFA-enabled accounts are excluded from this preview's recovery/proof flows; no MFA bypass is introduced. MFA recovery remains SaaS-022.

Recovery sends only for confirmed accounts; unverified accounts can request verification instead. Requests are serialized by the account row, with at most one accepted account-mail request per minute and three per rolling hour across purposes/processes. Responses are identical for suppressed/absent/accepted accounts and have a 350 ms minimum response floor. This reduces simple timing differences, not a constant-time or distributed abuse guarantee. The existing IP auth limiter (10 mutations/minute) also applies. Existing registration still reports duplicate addresses; broader registration-enumeration policy remains a separate product decision.

Reauthentication requires a new password check and records a timestamp inside the stamped HttpOnly session. The status route reports an expiry only for the next five minutes, and the UI expires its display. It is session-specific; it does not become a bearer token or company grant. Existing sensitive company actions still require their current-password proof. Future privileged routes must enforce recent proof server-side if they adopt this policy; the UI label alone grants nothing.

Framework reference: [Microsoft account confirmation and recovery guidance](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/accconfirm?view=aspnetcore-10.0).

## Migration and rollout checklist

- [ ] Apply migration nine, `20261104163117_AccountSecurityMail`, using the migration identity before deploying either API or worker. It creates an empty account-mail table and does not confirm old accounts or enqueue historical mail.
- [ ] Apply the updated [API grants](../../deploy/database-roles.sql.example). Logout now cancels pending account mail even when email preview is off, so these grants are required. API can insert/read jobs and cancel state/payload/lease fields; it cannot increment attempts or dispatch them.
- [ ] For the optional capture process, provision a different [account-mail worker identity](../../deploy/account-mail-roles.sql.example). It may read/update the account-mail table and read migration history; it cannot read accounts/password hashes/security/project data, enqueue mail, delete jobs or access tenant checkpoint rows. Keep the checkpoint worker's existing grants unchanged.
- [ ] Provision private API/worker key and secrets directories. Both use Data Protection application name `TaskFlow` and the same `DataProtection:KeysPath`. Protect the complete ring and backups; filesystem key rings require private ACLs and the selected-host key-at-rest policy. Queue payload encryption is only as strong as that key custody.
- [ ] On an isolated Development/Staging API, explicitly enable the preview:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Staging'
$env:AccountSecurity__EmailFlowsEnabled = 'true'
# Optional only after planning existing-account onboarding:
$env:AccountSecurity__RequireConfirmedEmail = 'true'
$env:DataProtection__KeysPath = 'D:\TaskFlow\.local\account-keys'
# Start the published API with its separate runtime connection and TLS configuration.
```

RequireConfirmedEmail takes effect only with an enabled Development/Staging preview. Production/UAT/unknown environment names cannot enable mail capture; use standard Staging for the isolated UAT rehearsal. False/default leaves existing login behavior compatible. Password change and reauthentication do not need the mail flag.

- [ ] Publish the worker and configure a private secrets directory containing only its restricted connection. The key ring must match the API. Configure the local sink and exact staged recipient allowlist:

```powershell
dotnet restore TaskFlow.slnx --locked-mode
dotnet publish TaskFlow-BE/TaskFlow.Worker -c Release --no-restore -o .local/releases/worker
$env:DOTNET_ENVIRONMENT = 'Staging'
$env:TASKFLOW_SECRETS_DIRECTORY = 'D:\TaskFlow\.local\account-mail-secrets'
$env:DataProtection__KeysPath = 'D:\TaskFlow\.local\account-keys'
$env:Email__Mode = 'LocalCapture'
$env:Email__CaptureDirectory = 'D:\TaskFlow\.local\mail-capture'
$env:Email__PublicOrigin = 'https://localhost:15173'
$env:Email__AllowedRecipients__0 = 'teammate@example.test'
$env:Email__PerCompanyHourlyLimit = '20'
dotnet .local/releases/worker/TaskFlow.Worker.dll --account-mail-once
```

This is a separate bounded process (at most 25 jobs), without a web listener or migrations on startup. It checks exact compiled/applied migrations. Run it again after eligible backoff or under a reviewed local/staging scheduler. The normal checkpoint daemon and `--capture-email` preview command retain their prior roles. Do not reuse the API/checkpoint connection for account-mail dispatch.

## Queue, failure review and privacy

`AccountEmailJobs` is global account work, not a workspace 0 tenant event. The source binds a stable GUID MessageId and account ID to an encrypted typed payload containing the recipient and action token. The worker validates the protected binding, account-only template and zero workspace before capture. It needs no account/password read permission. Local artifacts are partitioned as `account-<ID>/<MessageId>` and cannot collide with company capture namespaces; limits count each global account separately. The existing option name `PerCompanyHourlyLimit` also caps each account capture partition; producer admission remains the stricter three/hour control.

Claims use short READ COMMITTED transactions, `FOR UPDATE SKIP LOCKED`, a random 60-second lease and database UTC. The worker commits before rendering/capture; no DB lock is held during the 20-second bounded handler. It checks state/token/expiry again under a row lock after consumption. Shutdown leaves the lease to expire. Expired claims recover; five lifetime claims exhaust the job. Temporary capture failures retry after 5/10/20/40 seconds; capture quota retry times are honored. Unsupported/corrupt payload, disabled sink or blocked recipient becomes a visible dead letter. Sender/provider exception text is discarded. Captured/Cancelled/DeadLetter erase ProtectedPayload; expired jobs are cancelled without delivery when visited. No replay/resurrection command is exposed: request a new authorized link after the cooldown.

A crash after capture but before acknowledgement reuses the same ID and the sink receipt; the database records Captured after retry. This is at-least-once dispatch with idempotent local capture, not exactly-once provider delivery. Invalidation during an in-flight capture can still leave a private artifact with a now-invalid link; stale acknowledgement cannot revive the cancelled job and the server rejects the old stamp. Attempt count, state and last closed failure code remain for operator review; immutable security audit records request and successful account changes. This release does not add a detailed delivery-attempt UI or real bounce/webhook processing.

Use a bounded metadata-only operator query; never dump ProtectedPayload or join account email/password fields into logs:

```sql
SELECT Id, UserId, Kind, State, Attempts, ExpiresAtUtc, LastCode
FROM AccountEmailJobs WHERE State IN ('Pending','Leased','DeadLetter')
ORDER BY Id LIMIT 100;
```

Worker event 3401 reports only a finalized count. Exit 0 means the batch completed, not that all jobs were captured or a customer was notified. Review State/LastCode. Check private configuration, grants, keys and capture ACLs on failure. Keep requests, captured `.eml`, tokens, keys, cookies and private CNF files outside logs/CI artifacts/version control. Terminal DB payload clearing does not erase local `.eml`; apply the reviewed short capture retention policy. Metadata archival, unattended scheduling, selected-host alerting and provider acceptance remain operational gates.

## Verification and remaining gates

MySQL/API tests cover protected enqueue, generic suppression/cooldown, concurrent single use, account/purpose/expiry binding, reset replay, old-password rejection, global session revocation, current proof/expiry, default-off behavior, optional login enforcement, corrupt/expired jobs, bounded retries/failure sanitization and competing workers. Frontend tests cover generic requests, fragment clearing, explicit confirmation, input/expired-link recovery, disabled service, proof and protected navigation. Published HTTPS verification runs the actual restricted worker with the shared key ring, confirms captured links, checks reset/replay/change and scans desktop/mobile accessibility/layout. See [release evidence](../evidence/saas019-account-security.json).

Real provider delivery, authenticated bounce/suppression handling, production activation, hosted CI/CodeQL/container runs, tenant cutover and manual assistive-technology review are not closed by local evidence. Next is SaaS-020 tenant/project-scoped invitation API/lifecycle; it must enforce verified intended recipient, single-use hashed tokens, current inviter authority, seat reservations and revocation independently of these global account tokens.
