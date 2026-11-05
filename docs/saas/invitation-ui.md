# Invitation onboarding and team management

SaaS-021, 5 November 2026. Company and project invitation screens are implemented as a default-off preview using the SaaS-020 API and private local/staging email capture.

Company Owners/Admins can open Invitations from company navigation or Team to invite Members/Guests, review paginated history, and confirm resend/revoke actions. Project invitations additionally require current project ownership; they grant Viewer/Editor access. Role descriptions appear beside the form. Management shows active members, reserved seats, available capacity, expiry, delivery status and resend cooldown. Captured mail is labelled as a preview, never as sent email. Writes use the displayed revision, prevent duplicate submissions and preserve failed drafts. Lost access hides recipient history.

Recipients open `/invitations/accept#workspaceId=...&invitationId=...&token=...`. The fragment is immediately replaced with a clean route and retained only in memory for the current application tab. It never enters router state or browser storage. Keep that tab open through signup/signin. After reloading or closing it, reopen the original captured link. If email verification opens another tab, return to the original invitation tab and sign in/refresh verification there. Wrong-account, missing, expired, revoked or used links show safe recovery actions without disclosing private scope details.

Company, project and role previews require a verified intended-recipient account. Access is granted only after explicit acceptance commits successfully. The UI does not infer membership from login or preview. Account/scope changes cancel pending requests; acceptance links cannot be replayed for another grant.

## Local/staging configuration checklist

- Apply migration ten, `20261105141627_WorkspaceInvitations`, and the API, reader and invitation-worker grants described in [invitation operations](invitations.md). This release adds no migration.
- Enable backend `WorkspaceReads__Enabled`, `WorkspaceWrites__Enabled`, `WorkspaceLifecycle__Enabled` and `Invitations__Enabled` for the preview. Review the independent account-security/email-flow flags and [verification setup](account-security.md) before onboarding unverified accounts.
- Set `VITE_WORKSPACES_ENABLED=true`, `VITE_WORKSPACE_LIFECYCLE_ENABLED=true` and `VITE_INVITATIONS_ENABLED=true` before building the frontend. Vite flags are build-time settings; rebuild after changing them.
- For portable Compose deployment, use those same three VITE variables plus `TASKFLOW_WORKSPACE_READS_ENABLED`, `TASKFLOW_WORKSPACE_WRITES_ENABLED`, `TASKFLOW_WORKSPACE_LIFECYCLE_ENABLED` and `TASKFLOW_INVITATIONS_ENABLED`. All default to false in `.env.deploy.example`. Runtime backend flags remain independent of frontend visibility.
- Deploy the API and frontend together: invitation responses now include `resendAvailableAtUtc`, and policy capacity includes `reservedMemberSeats`. The contract remains 53 paths and 71 operations.
- Use the explicit private capture worker command and allowlist from the invitation operations guide. No real provider or recipient delivery is enabled. Do not publish captured links, private files or browser recordings containing tokens.

## Verification and remaining gates

Local verification covers 280 backend tests, 146 frontend tests, 35 operations tests, production frontend builds, and an isolated published HTTPS browser journey with 16 desktop/mobile accessibility scans. The browser journey exercises signup, verification, wrong-account recovery, explicit Guest/Viewer acceptance, resend rotation, revoke confirmation and used-link recovery. See [release evidence](../evidence/saas021-invitation-ui.json) and `tests/browser/invitations.cjs`.

This is local evidence. Hosted CI/CodeQL, container execution, selected-host deployment, production email, manual assistive-technology review and mandatory tenant cutover remain open. Next is SaaS-022 optional MFA, recovery codes and privileged-action policy.
