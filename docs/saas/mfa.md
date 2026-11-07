# MFA, recovery codes and privileged actions

SaaS-022, 7 November 2026. Optional authenticator enrollment, MFA sign-in, recovery-code management and administrator step-up enforcement are implemented. Enrollment and administrator enforcement are separate default-off runtime flags. This release adds migration eleven, `20261107131844_AccountMfa`.

## User journey

Open Profile -> Account security -> Manage MFA and recovery codes. Verify your email first. Enter your current password, add the displayed private key manually to a TOTP authenticator app, then enter your password and a code from that app to confirm. Setup lasts ten minutes and is bound to the initiating session and account security stamp. Merely opening setup does not enable MFA or replace an existing authenticator. Starting a new setup replaces the previous pending setup.

The authenticator uses a 160-bit random key, HMAC-SHA1, six digits and a 30-second interval. The server accepts the current interval and one adjacent interval on either side, with strictly increasing consumed intervals. Codes already used for sign-in or another successful action cannot be reused: wait for a new code and keep device/server clocks synchronized. Manual enrollment avoids sending secrets to an external QR service. QR scanning and phishing-resistant WebAuthn/passkeys are outside this release.

After confirmation, save the ten recovery codes immediately. Each has 128 random bits and works once with your password. They are shown only after enrollment or explicit replacement; leaving/refreshing the page hides them. Store them offline or in a secure password manager. TaskFlow does not put them in browser storage, router state, URLs, logs, audit or email. Replacing codes invalidates all previous codes. Replacing the authenticator also replaces the recovery set. Other sessions are invalidated; the current proven session is renewed and CSRF is refreshed.

At sign-in, choose Authenticator code or Recovery code under Additional verification. No authenticated or privileged session is issued after password alone for an enrolled account. Disabling the enrollment flag later does not weaken existing MFA sign-in. Wrong passwords/factors have generic sign-in responses, the existing IP rate limit and account lockout after five failures. There is no remembered-device bypass.

If you lose your device, sign in with one recovery code and immediately open MFA settings. For five minutes, that stamped recovery session plus your current password can begin replacement without consuming a second code. This works even when you used your last recovery code. Confirm a code from the replacement app before credentials change. Until confirmation, the existing authenticator remains active, and recovery sign-in cannot satisfy administrator proof. If that window expires, a fresh valid factor is needed; the UI does not extend it. Save the new recovery set after confirmation.

Disabling optional MFA requires current password plus a fresh authenticator or unused recovery code, explicit confirmation, and signs out every session. Active company Owners/Admins cannot disable MFA while the administrator requirement is enabled. Restore the device instead. No support or company-admin HTTP endpoint can remove another account's MFA. Losing every factor requires a separately reviewed operator identity-verification/recovery procedure; never clear the MFA flag as a support shortcut.

Password change and reauthentication for an enrolled account require current password and a fresh authenticator code. Email password recovery remains restricted to the existing local/staging mail preview: a valid current email reset token alone cannot bypass MFA. It additionally needs an authenticator or unused recovery code, retains MFA, consumes proof atomically, and revokes all sessions. Unknown/suppressed/eligible mail requests retain the generic response. MFA email-reset failures are bounded by lockout; stale email tokens cannot be used to trigger factor checks.

## Privileged-action policy

`Mfa:RequireForCompanyAdministrators=true` requires active company Owner/Admin actors to have authenticator proof younger than five minutes for scoped company mutations, including POST audit exports. Read-only navigation and account-security/restoration remain accessible. Password-proven reauthentication renews this proof only when accompanied by a fresh authenticator code. A recovery-code sign-in never supplies it. Ownership/password-proof actions for an MFA account also require fresh authenticator session proof, even when the deployment-wide administrator requirement is off.

The policy is deployment-wide, not a per-company switch. Current membership is checked server-side; original company/project authorization, owner invariants and private-project boundaries still apply. API startup rejects administrator enforcement unless all three workspace rollout gates are enabled, so legacy writes cannot remain as an alternative path. This does not complete mandatory tenant cutover or selected-host acceptance.

## Migration and configuration checklist

- Stop writers for the reviewed release window and back up the database plus complete private Data Protection key ring.
- Apply migration eleven using the migration identity before deploying API/worker binaries. It creates an empty `AccountMfa` table and does not enroll or modify old accounts. Review the [release runbook](../release-runbook.md).
- Apply the new API-only grant from [database roles](../../deploy/database-roles.sql.example): SELECT/INSERT/UPDATE on `AccountMfa`. Check SHOW GRANTS. Readers and all mail/checkpoint workers must have no access to this table. No DELETE grant is needed.
- Deploy matching API/frontend/worker builds. The contract is now 58 paths / 76 operations. Optional login/reset/proof fields remain backward compatible for accounts without MFA.
- Keep the same private Data Protection application/ring on every API instance. Protect keys at rest according to the selected host's policy; retain old keys for active authenticator secrets and recovery backups. Missing/corrupt keys fail closed.
- Enable enrollment first, verify onboarding and factor recovery on synthetic accounts, then roll out the administrator requirement. Existing unverified accounts need a verification/onboarding plan before enforcement.

For this initialized local workspace, from PowerShell:

```powershell
Set-Location D:\TaskFlow
$env:Mfa__Enabled = 'true'
# Optional Development/Staging verification-email preview; configure capture separately:
$env:AccountSecurity__EmailFlowsEnabled = 'true'
.\scripts\Start-Local.ps1 -EnableSaasPreview
```

This rebuilds/migrates the development database and starts the stack. The mail worker is not started: follow [account-email capture](account-security.md) for its separate identity and private capture commands. Existing Vite must be restarted if feature flags changed. Local HTTP follows the application's transport settings; use HTTPS for hosted credentials and authenticator setup.

Only after recovery has been rehearsed and administrator onboarding completed:

```powershell
$env:Mfa__RequireForCompanyAdministrators = 'true'
# Restart the API with WorkspaceReads/WorkspaceWrites/WorkspaceLifecycle enabled.
```

For portable Compose, set `TASKFLOW_MFA_ENABLED=true` and, after review, `TASKFLOW_MFA_REQUIRE_ADMINISTRATORS=true`. These map to backend runtime settings and default to false in `.env.deploy.example`. Administrator enforcement also needs all three `TASKFLOW_WORKSPACE_*_ENABLED=true` settings. MFA has no separate Vite flag: self-only status controls enrollment UI. The company/invitation UI retains its existing build-time flags.

Do not downgrade/drop this table or set `TwoFactorEnabled=false` to roll back. An older binary cannot safely manage enrolled credentials. Roll forward with the retained encrypted credentials/key ring, or use a reviewed recovery plan. Turning enrollment off is safe; it does not disable MFA for existing accounts.

## Persistence, audit and backup recovery

Account-row locks serialize enrollment, sign-in consumption, reset, recovery rotation and disable. Secret state, recovery hashes, stamp changes and success audit commit before a session is issued. MFA secrets use user- and purpose-bound Data Protection encryption. Only user-bound SHA-256 recovery hashes are stored; raw recovery values are generated for one response. Audit contains stable account IDs, action/outcome and correlation only. No credential material enters company audit/outbox or request logging. Framework password lockout and CSRF/transport controls remain active.

Restoring an old database invalidates sessions, all recovery hashes and pending enrollments before publishing recovered keys. It keeps the active authenticator encrypted and blocks the restored current/next TOTP intervals to prevent recent-code replay. Wait up to about a minute for a fresh interval after restore. Previously consumed backup-era recovery codes must never revive. Review the restore receipt and existing mail/invitation invalidations before resuming traffic. A restore may remove your only recovery option if the device was already lost; rehearse recovery/key custody with the designated developer or operations team before production use.

## Validation and release gates

The [release evidence](../evidence/saas022-mfa.json) records actual local results. Tests cover RFC 6238 reference vectors, default-off and verified/session-bound enrollment, expiry, TOTP replay, concurrent recovery-code replay, lockout with correct passwords, credential/audit rollback, stamp revocation, factor-protected password reset/change, last-code restoration, and administrator enforcement. `tests/browser/mfa.cjs` exercises published HTTPS enrollment/sign-in/recovery/restoration and desktop/mobile accessibility using a synthetic account and restricted runtime grants. Secrets are excluded from browser reports and screenshots.

Hosted CI/CodeQL/container runs, real email-provider delivery, selected-host key custody/backup rehearsal, independent security review, manual assistive-technology review and mandatory tenant cutover remain gates. TOTP is not phishing-resistant; no NIST assurance-level/compliance certification is claimed. Next is SaaS-023 onboarding delivery, abuse controls and tenant acceptance; real staging delivery depends on a selected provider/host.

References: [Microsoft ASP.NET Core MFA](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/mfa?view=aspnetcore-10.0), [RFC 6238](https://www.rfc-editor.org/rfc/rfc6238), and [NIST replay-resistance guidance](https://pages.nist.gov/800-63-4/sp800-63b.html).
