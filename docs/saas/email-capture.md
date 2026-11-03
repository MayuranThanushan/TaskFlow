# Email abstraction, templates and local capture

SaaS-018 local foundation, 3 November 2026. The application exposes `IEmailSender` with an explicit recipient-bound `EmailRequest`, stable message ID, workspace, closed template and typed inputs. The separate worker has an offline preview command. This release has no SMTP/provider transport, automatic business-email enqueue, inbox UI or real sending. Staging delivery here means delivery to a private local capture sink; real staging mail/provider/bounce integration remains an acceptance gate before SaaS-023.

## What ships

Three versioned-in-code preview templates cover company invitation, assigned task and workspace welcome. Each renders branded HTML and equivalent plain text with a fixed subject. Dynamic HTML is encoded, body parts are UTF-8/base64, and address/control/header validation rejects injection or multiple recipients. Links are built from a deployment-controlled root origin and closed routes; request data cannot supply an arbitrary URL. HTTP links are accepted only for a Development loopback origin; Staging requires HTTPS. Invitation previews are not redeemable invitation flows: SaaS-020/021 still own token authorization and accept UI. The current templates include an explicit local-preview footer.

`LocalEmailSender` implements the abstraction without network I/O. It is registered only by the explicit worker preview command, not the API request pipeline or checkpoint daemon. The existing outbox facts are not recipient authority and are not converted into mail. No database migration, new HTTP route, API contract or dependency is added. The worker's normal checkpoint behavior is unchanged.

A future workflow must authorize the recipient and current workspace/project membership, apply preferences/consent/expiry, protect account/invitation tokens and enqueue recipient-bound intent atomically with its business transaction. Do not call this preview CLI from a controller, or reinterpret checkpoint history as send requests. The preview CLI is an operations/developer command with filesystem privileges, not a tenant-authorized public endpoint. Its workspace ID is an explicit synthetic preview input, not proof of customer access.

## Local checklist

- [ ] Restore and publish the worker from the repository root:

```powershell
dotnet restore TaskFlow.slnx --locked-mode
dotnet publish TaskFlow-BE/TaskFlow.Worker -c Release --no-restore -o .local/releases/worker
```

- [ ] Use a private capture directory outside `wwwroot`, the published app and any served/static directory. The recommended local location is `.local/mail-capture`. On Windows, provision a directory whose ACL allows only your developer/service identity and designated operations owner; new files inherit it. On Unix, the implementation creates private directories (0700) and files (0600). Ancestor reparse points/symlinks are refused. Do not use a shared/network filesystem or a publicly mounted volume.
- [ ] Set the explicit offline configuration in this terminal:

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
$env:Email__Mode = 'LocalCapture'
$env:Email__CaptureDirectory = Join-Path (Get-Location) '.local/mail-capture'
$env:Email__PublicOrigin = 'http://localhost:5173'
$env:Email__PerCompanyHourlyLimit = '20'
```

The default mode is Disabled. Only exact `Development` or `Staging` environments can capture; Production, UAT and unknown names fail closed. Use the standard Staging environment for the UAT capture rehearsal. Development permits synthetic `.test` addresses, plus any exact configured allowlist entries. Staging requires an explicit allowlist even for `.test` recipients. These checks apply before any files are created. No real address is needed for this release.

- [ ] Copy the synthetic request example to a private, ignored file and capture it:

```powershell
Copy-Item deploy/email-capture.request.json.example .local/email-preview.json
dotnet .local/releases/worker/TaskFlow.Worker.dll --capture-email .local/email-preview.json
# Repeat unchanged: email.already_captured; no duplicate artifact or quota charge.
dotnet .local/releases/worker/TaskFlow.Worker.dll --capture-email .local/email-preview.json
```

Use a new `messageId` (32 hexadecimal GUID characters) for a new logical message. Reuse the same ID on retries. Input files are limited to 16 KiB, reject unknown fields and require a named template. Do not put recipients, tokens or connection strings in command-line arguments. The command does not require database connectivity or invoke the migration check; normal worker operation still does.

- [ ] Open `<capture directory>/<workspace ID>/<message ID>/message.eml` in a trusted local mail viewer. It contains multipart plain-text/HTML. Its companion receipt contains schema version, message/workspace/template IDs, UTC capture time and integrity fingerprints, without recipient/body/token. Both are private operational data. Never upload capture files or request inputs as CI artifacts, attach them to support tickets or expose the directory through a web server. The repository ignores `.local`, `mail-capture` directories and `.eml` files; other configured locations require equivalent controls.

## Staging capture checklist

Use an operations-owned private directory and the separate worker identity. Set configuration through the private key-per-file secrets directory already supported by the worker, or scoped service environment. Keep secrets and captures outside the app image and public assets. There is no email provider/API credential in this implementation.

```powershell
$env:DOTNET_ENVIRONMENT = 'Staging'
$env:Email__PublicOrigin = 'https://staging.example.test'
$env:Email__AllowedRecipients__0 = 'teammate@example.test'
dotnet .local/releases/worker/TaskFlow.Worker.dll --capture-email .local/email-preview.json
```

Set Mode, CaptureDirectory and limit as above too. Allowlist entries match complete addresses (case-insensitive), never domains/wildcards or substring matches. An alias is a different address. Staging captured mail remains a local artifact, not provider acceptance or recipient delivery. Choose an actual deployed root origin when implementing working flows; company UI remains default off and invitation routes are future work.

## Idempotency, limits and failure review

The local sink serializes writes per workspace using an exclusive OS file lock, coordinating independent processes on the same local filesystem. It writes `.eml` and receipt under a private temporary directory, then renames the directory as one commit. Cancelled/pre-commit failures do not publish a receipt; interrupted `.pending-*` directories are not counted as captures. A retry after commit reads the same receipt and validates the MIME hash. Changed recipient/template/body/origin with the same workspace/message ID returns `email.id_conflict` without overwriting mail. An identical ID in another workspace is independent.

The capture limit is a per-workspace UTC calendar-hour cap, 1-1000, default 20. It counts committed receipts across process restarts, not attempts. Identical successful retries remain allowed at the cap. A new capture at the cap returns `email.company_limit` and the next UTC-hour retry time. Limits use the host clock; synchronize it. A 10,000-receipt-per-workspace local storage bound prevents unbounded scans. These are local capture limits, not production/provider quotas, distributed tenant fairness or transactional admission for future email jobs.

The command prints a bounded JSON status event (3400) with a closed code/status and optional retry time, without addresses, subject, body, tokens, paths or raw exceptions:

| Result | Exit | Operator action |
| --- | --- | --- |
| `email.captured`, `email.already_captured` | 0 | Inspect the private local artifact if needed; no customer delivery occurred. |
| `email.company_limit` | 1 | Retry the same ID at `retryAtUtc`. |
| `email.capture_unavailable` | 1 | Check permissions, free space, exclusive lock contention and artifact/receipt integrity privately; retry the same ID with bounded backoff. Do not delete a successful receipt to force resend. |
| `email.capture_storage_limit` | 1 | Review retention/archival before further capture. |
| `email.disabled`, `email.configuration_invalid` | 2 | Review mode, environment, private absolute path, origin and limit. |
| `email.invalid`, `email.request_unreadable` | 2 | Fix the private input; no raw parse diagnostics are printed. |
| `email.recipient_blocked` | 2 | Review the exact staged recipient; do not loosen to a wildcard. |
| `email.id_conflict` | 2 | Investigate logical-message identity; use a new ID only for a new authorized message. |

The preview command performs one attempt and never retries automatically. Lock contention or temporary filesystem failure can be retried by the caller; production durable email scheduling/retry is not wired here. Capture success is `Captured`, never `Delivered`, `Bounced` or provider Accepted. No bounce callback, verified provider webhook, suppression list or delivery-status UI is claimed. Future provider integration must authenticate callbacks, correlate stable message/channel/recipient IDs, handle ambiguous acceptance idempotently and enforce tenant limits before any real send.

Do not remove receipt directories during a retry horizon: deletion loses both deduplication and hourly accounting. Operators may review stale `.pending-*` directories only while the sink is stopped. Captures may contain sensitive tokens; use synthetic data, least-privilege ACLs and a short reviewed retention period. This local filesystem commit is not a database-backed transactional email queue, cross-host coordination or power-loss durability guarantee. No automatic purge/backup of sensitive mail is introduced.

## Verification

The email tests cover templates/escaping, multipart structure, closed origins, addresses/header injection, scope/identity/token validation, disabled environments, staging allowlists, restart deduplication and conflicts, persisted quotas, competing senders, corrupt artifacts, lock failure, cancellation and safe bounded CLI output. No SMTP/network transport is available to tests.

Run the published-process rehearsal with synthetic requests:

```powershell
python scripts/verify_email_capture.py --worker .local/releases/worker/TaskFlow.Worker.dll
```

It validates two independent worker processes, quota admission, restart/idempotency, cross-company separation, MIME parsing, staging/production guards and redacted status output, then removes its own private synthetic artifacts. CI runs this rehearsal and uploads only its safe boolean/count report. See [release evidence](../evidence/saas018-email-capture.json). Hosted CI, Linux/container execution and selected-host storage/ACL tests remain separate gates.

Next is SaaS-019 account verification/recovery/change and reauthentication, with appropriately scoped framework tokens and additional account-security templates. Real provider credentials, authenticated bounce/failure handling and actual staging recipient delivery remain open before onboarding acceptance (SaaS-023).
