# SaaS-023: onboarding acceptance and SendGrid staging

12 November 2026. The local release adds a shared invitation send budget and an explicit, allowlisted SendGrid adapter. Email is still disabled by default. Production activation, actual recipient delivery and authenticated bounce/complaint callbacks have not been verified. This release does not certify the public pilot for commercial onboarding.

## What changed

Company invitation creation and resend now share 20 admitted sends per rolling hour, across administrators, projects and API processes. Admission uses the existing company transaction gate and append-only Created/Resent transitions, using database UTC. Revoking or accepting an invitation does not erase usage. Failed/racing admissions roll back token, revision, payload, audit and reservation changes. The existing one-minute resend cooldown, three-resend maximum, seven-day expiry, 1000 retained invitation cap and account/IP mutation limits still apply.

An exhausted company budget returns `429 invitation.send_limited` with a bounded numeric `Retry-After` (1–3600 seconds). The frontend explains that resends count toward the same budget, keeps a creation draft and does not retry writes automatically. Revocation and acceptance remain available when the send budget is full. The budget query reads existing indexed company history and returns at most 20 timestamps; no migration or additional runtime grants are required.

`Email:Mode=SendGrid` selects the HTTPS Mail Send adapter in the separate worker. It accepts only Staging, an HTTPS public link origin, a configured sender and an exact recipient allowlist (up to 100 addresses). `--capture-email` continues to use only the local file sink. The new `--send-email <private-file>` is an explicit, single-request provider test and does not open a database or listener. Batch commands remain `--account-mail-once` and `--invitations-once` with their existing separate least-privilege database identities and shared API key ring.

SendGrid credentials never go in frontend variables, browser requests, Docker build arguments or Git. API processes do not need the SendGrid key. No new SDK/package is required. Requests go to the fixed `https://api.sendgrid.com/v3/mail/send` endpoint with a ten-second deadline, no automatic redirects and no internal retry loop. Provider response bodies are never read or logged because they may echo private fields.

Click/open/subscription tracking is disabled for transactional links. The existing escaped HTML/plain-text templates retain their fragment-based invitation and account-security links. Provider messages use a transactional footer; local previews keep their explicit no-email-sent footer. A cloud invitation must name an invitation ID; the legacy query-token preview shape cannot be sent through SendGrid.

## Acceptance is not delivery

Only HTTP 202 is recorded as `ProviderAccepted`. This is a terminal queue state, separate from local `Captured`; it clears the encrypted send payload and lease, but never verifies an account, accepts an invitation or creates membership. Invitation UI says “Accepted by email provider (delivery not confirmed).” Existing state columns already support this value; there is no twelfth migration. Deploy the API and regenerated frontend contract together before enabling the provider worker.

429 and temporary transport/5xx failures use existing bounded queue retries, at most five attempts. Provider Retry-After is clamped to 5–3600 seconds. Other rejections, including 401/403, dead-letter without repeated sending. Operator codes are `email.provider_accepted`, `email.provider_rate_limited`, `email.provider_unavailable`, `email.provider_auth_failed` and `email.provider_rejected`. No provider body, recipient, token or API key is included in these codes.

SendGrid receives the stable message ID as `custom_args.taskflow_message_id` for correlation. This is **not** a provider deduplication guarantee. A timeout/crash after provider acceptance but before database acknowledgement can produce a duplicate after lease recovery. Tokens remain single-use and role/authority checks still apply. Revocation during a network send cannot retract the external email; the API invalidates its link.

## Configure your existing SendGrid account

1. In SendGrid, verify the sender identity or authenticate your domain. Create a restricted API key with Mail Send permission, rather than account administration access.
2. Keep the key private. SendGrid's current free offer is a 60-day trial, not permanent free hosting. Do not upgrade or attach payment details under the existing free-only authorization; inspect the account's actual trial/limits before sending.
3. In PowerShell at `D:\TaskFlow`, run the helper with your real verified sender, an operator-owned test inbox and the actual HTTPS staging origin:

```powershell
./scripts/Set-SendGridSecrets.ps1 -FromEmail 'VERIFIED-SENDER@example.com' -AllowedRecipient 'YOUR-TEST-INBOX@example.com' -PublicOrigin 'https://YOUR-STAGING-HOST'
```

The script prompts for the API key with hidden input and creates `.local/sendgrid-secrets` with private permissions and UTF-8 secret files. It never overwrites an existing directory or sends email. Choose another `-OutputDirectory` if one already exists. Keep its `.test-message.json` private too. This setup check is not an actual membership grant.

4. Publish the worker and run one explicit test, only when you intend to send to that allowlisted inbox:

```powershell
dotnet publish TaskFlow-BE/TaskFlow.Worker -c Release --artifacts-path .local/build/sendgrid-worker /p:RestoreLockedMode=true -o .local/releases/sendgrid-worker
$env:DOTNET_ENVIRONMENT = 'Staging'
$env:TASKFLOW_SECRETS_DIRECTORY = (Resolve-Path .local/sendgrid-secrets).Path
try {
    dotnet .local/releases/sendgrid-worker/TaskFlow.Worker.dll --send-email .local/sendgrid-secrets/.test-message.json
    if ($LASTEXITCODE -ne 0) { throw 'SendGrid test did not receive provider acceptance. Review the safe status code.' }
} finally {
    Remove-Item Env:TASKFLOW_SECRETS_DIRECTORY -ErrorAction SilentlyContinue
    Remove-Item Env:DOTNET_ENVIRONMENT -ErrorAction SilentlyContinue
}
```

This command uses the network and sends real mail; do not rerun it blindly. Check both the receiving inbox/spam folder and SendGrid Email Activity. A 202 alone does not complete delivery acceptance. Record only safe timestamps/statuses and a private operational correlation reference; never attach the payload, token or API key.

5. For actual staged invitation/recovery queues, follow the existing [invitation](invitations.md) and [account security](account-security.md) setup. Use the correct restricted database identity, the exact matching migration set (thirteen as of SaaS-025) and the shared Data Protection ring. Merge the SendGrid settings into each worker's private secret directory, keeping the two database identities separate. The worker must not use API/root database credentials. On the Staging API, explicitly enable the relevant existing rollout flags; require confirmed accounts according to the staged onboarding plan.

The current Render Free/TiDB profile deliberately keeps email and workers off. Do not enable these workers on TiDB until the locking/lease behavior is certified. Do not rename the public Production app to Staging just to bypass the transport restriction. No paid background worker or scheduler is provisioned by this change.

## Support runbook

| Symptom | Operator action |
| --- | --- |
| `email.configuration_invalid` / disabled | Check Staging, mode, exact allowlist, HTTPS origin and private sender/key settings. Do not dump configuration. |
| `email.provider_auth_failed` | Pause sending, inspect Mail Send key permission and verified sender in SendGrid; rotate compromised credentials. Reissue authorized jobs after correction. |
| `email.provider_rejected` | Review SendGrid's restricted dashboard for account/sender/suppression problems. Do not put provider diagnostics in public logs. |
| Rate limited | Honor the bounded retry time. Do not create new identities/invitations to bypass the budget. |
| Temporary outage | Existing jobs retry with the same message reference and stop after five attempts. Monitor Pending/DeadLetter age; never mark a failed send delivered manually. |
| ProviderAccepted but no inbox mail | Check Email Activity, spam and suppressions. Acceptance is not delivery. Do not bypass unsubscribe/suppression controls. |
| Wrong account / expired link | Ask the intended recipient to sign in with their verified address; authorized manager may resend/reissue within limits. Never send tokens to support. |
| Revoked inviter / removed member | Restore access only through the explicit company lifecycle policy. Old links cannot reactivate removed or suspended membership. |
| Compromised key or broad mis-send | Stop the worker, set `Email:Mode=Disabled`, revoke the key, review private provider evidence and affected invitations, then resume only with a new least-privilege key and reviewed allowlist. |

Keep provider credentials, recipient addresses and message-level evidence in restricted operations storage. Existing recovery cancels restored pending mail and rotates account stamps; see [backup/recovery](../backup-recovery.md). Do not replay pre-recovery queues or delete company audit history to reset rate limits.

## Release acceptance checklist

- [x] Provider request construction, no-network guards, exact allowlist, escaped templates, tracking disabled, status mapping, timeout/cancellation and bounded Retry-After tested without real sending.
- [x] MySQL tenant tests cover one-use/concurrent acceptance, wrong company/account, expired tokens, revoked authority, audit rollback, seat reservations and send-budget races.
- [x] Temporary outage/recovery, stable retry IDs, five-attempt exhaustion, revoked inviter before dispatch and provider acceptance without access grants are automated.
- [ ] Operator-owned inbox delivery verified using the real SendGrid account and shared API/worker key configuration.
- [ ] End-to-end real verification/recovery/invitation acceptance recorded against the selected host.
- [ ] Authenticated bounce/complaint callback ingestion, replay protection and suppression handling implemented and verified before production email rollout. No unsigned callback endpoint is exposed in this release.
- [ ] Hosted CI/container and database-locking compatibility gates pass, with selected-host backup/key-custody rehearsal and named operations ownership.

SaaS-023 local implementation can be reviewed independently; the unchecked external gates remain open. Next planned product feature is SaaS-024 notification materialization/read/unread API, subject to the existing release dependencies.

Sources: [SendGrid Mail Send API](https://www.twilio.com/docs/sendgrid/api-reference/mail-send/mail-send), [sender verification](https://www.twilio.com/docs/sendgrid/ui/sending-email/sender-verification), [API-key permissions](https://www.twilio.com/docs/sendgrid/ui/account-and-settings/api-keys), [trial/pricing](https://www.twilio.com/en-us/products/email-api/pricing).
