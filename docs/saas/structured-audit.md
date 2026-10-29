# Versioned business audit and platform security events

SaaS-013, 29 October 2026. Business history now has a versioned metadata envelope, safe field changes and request correlation. Platform security events have separate storage and an operations-only bounded NDJSON exporter. Company users, including Owners/Admins, have no security-event API.

## Business records and privacy

New ActivityLogs and WorkspaceAuditLogs rows use SchemaVersion 1, outcome Succeeded, a server-derived subject type/ID, correlation ID and an ordered changes array. Existing public activity/governance DTOs include an `audit` object; endpoint paths and permissions are unchanged. Project viewers retain only the project history they could already read. Company governance readers receive stable IDs and safe role/state changes, never private project/task names or content.

The capture allowlist includes project ownership/deletion, task status/priority/assignment/deadline/deletion, project grant role/revocation, company role/state and private text change markers. IDs remain decimal strings, including values above JavaScript's safe integer range. Private title/name/description values are never copied into the metadata: their changes are marked redacted with presence markers. Passwords, hashes, email, cookies, tokens, headers, IP addresses and request bodies are not audit deltas. Other entity fields are excluded by default. This is not a full-content snapshot or an automatic diff of arbitrary entities.

Capture retains pending field changes across the existing two-save mutation boundary: generated IDs are persisted first, then activity is inserted in the **same transaction**. Single-save lifecycle mutations capture their own changes before save. The business mutation and its audit commit or roll back together; no-op operations produce no event. Historical attribution and company/project privacy continue to follow the [lifecycle policy](workspace-lifecycle.md).

Request middleware creates an ambient correlation scope before authentication and application services. It uses the current W3C activity ID when available, otherwise a server-generated ID. The same value reaches X-Trace-Id, Problem Details, audit records and security events across fresh async DI scopes, even with tracing disabled. Incoming bodies, custom correlation headers and effective tenant/actor fields are never accepted as audit authority. Non-HTTP callers should establish an explicit AuditCorrelation scope for a multi-context operation.

## Legacy and forward compatibility

Migration `20261029150840_StructuredAudit` adds nullable metadata and SchemaVersion default 0 to existing business histories, plus a new SecurityEvents table. Existing rows remain unchanged: version 0 reads as `isLegacy: true`, `outcome: LegacyUnknown`, with no invented correlation or deltas. Historical action/actor/time and existing safe summaries remain available. New writers stamp version 1. A reader encountering an unknown version withholds its metadata payload and returns UnsupportedVersion rather than interpreting unreviewed changes.

Audit details on project activity and the basic company audit page show version/outcome, safe changes, subject and correlation. The UI never renders before/after values marked redacted. Legacy and unsupported versions are labelled explicitly. Full audit filtering, deleted-resource review and bounded company export jobs remain SaaS-014; the existing current-project-page download retains its existing scope.

## Platform security events

SecurityEvents stores version, action, outcome, actor/subject account IDs when known, subject kind, correlation, UTC time and optional static route/status. It has **no company affiliation** and no email/password/body/header/IP columns. A login for an unknown account has a null subject, never a hash or copy of the submitted email. Stable account IDs intentionally have no deletion cascade: security attribution survives future account lifecycle work without joining private account data into company audit.

| Event | Outcome / persistence |
| --- | --- |
| Registration | Account creation and its success event commit together; validation/duplicate denials use an independent event |
| Login | Succeeded means credentials accepted; the security record persists before session issuance. Denied/LockedOut do not issue a session |
| Logout | Security-stamp revocation and its success event commit together, then the response clears the cookie |
| ProfileChanged | A real name change and its event commit together; the actual name is withheld; no-op saves create no event |
| PasswordProof | Verification success/denial persists outside governance rollback, with no password. Success is not proof that the requested role/ownership operation committed |
| RequestRejected | Coalesced route/status signals for auth/access/rate/CSRF failures and company conflicts; the subject is the requesting account or Anonymous, not a claim that a requested target exists |

Login Succeeded is server credential acceptance, not proof of client cookie delivery. HTTP delivery and database commit cannot be one transaction. On a critical security-write failure, login issues no new session, registration rolls back the account, and logout rolls back stamp revocation. Existing sessions are not globally revoked just because a different login failed. Password-proof records survive a later business conflict/rollback; they remain distinct from successful governance audit.

RecoveryRequested/RecoveryCompleted action codes are reserved for future R2 account-recovery integration. No recovery route or fabricated recovery events are shipped here. Membership/admin successes are in company/project business audit; denied requests produce bounded platform signals. Account recovery, MFA/SSO and security-event retention/pseudonymization policy remain explicit future work.

Critical authentication/verification events are durable synchronous writes. Rejected-request signals use a fixed 1,024-slot in-memory window, at most one signal per slot per minute. Slots can collide and suppress additional signals; Coalesced=true means **not an exact attempt count**. No raw IP is retained as a partition identifier. Existing request metrics/logs remain the request-count source. A failed coalesced sink does not convert a denied response into success; event 3201 security.sink_unavailable alerts operations. Critical writer failures log event 3200 security.persist_failed with action/type and server correlation only. Neither log copies exception text or credentials. Selected-host ingress/distributed throttling, monitoring and storage/retention objectives still require operational review.

## Database roles and operations export

Apply the reviewed [database grant template](../../deploy/database-roles.sql.example) after migration, with writers stopped. Runtime grants are explicit table grants: INSERT on SecurityEvents, SELECT(Id) only for EF generated-key retrieval, and no security payload SELECT/UPDATE/DELETE. Both business histories allow SELECT/INSERT and deny runtime amendment/deletion. Migration history remains runtime read-only. Do not retain an old database-wide SELECT/UPDATE grant or inherited broad role.

The separate taskflow_security_audit identity has SELECT on SecurityEvents only; it cannot read project content or update history. Migration and backup operators remain separately privileged. There is no platform-admin role encoded in ordinary company cookies and no public security-event reader. Verify actual SHOW GRANTS and negative queries on the selected host; local test evidence does not configure that host.

Provision a private operations credential file and an existing access-restricted output directory. For example:

```powershell
python scripts/export_security_events.py `
  --mysql /path/to/mysql `
  --defaults-file /private/security-reader.cnf `
  --schema taskflow --after-id 0 --limit 100 `
  --output /private/security-export-001.ndjson
```

The exporter performs one SELECT ordered by Id, maximum 1,000 rows, and writes a new UTF-8 NDJSON file. Resume with the last exported ID; IDs stay strings. It refuses overwrite, invalid identifiers/cursors, source-controlled destinations and common web roots. Restrict/encrypt the directory and move exports only through approved operations channels. It never prints record payloads, credential contents or private MySQL diagnostics. Do not use app runtime credentials for export. This is a restricted operator tool, not a company export endpoint or an unbounded background job.

## Deployment and verification

The additive migration is required for new binaries in every environment, even when company preview flags are off. Use the migration identity and matching release bundle; verify readiness and authentication smoke checks before reopening traffic. Existing company/lifecycle gates remain default off. Preserve the [migration/recovery procedure](workspace-lifecycle.md); do not treat Down as production recovery or retrofit historical success/deltas.

Local verification includes actual MySQL transaction/failure tests, cross-company and platform-reader denials, unknown/legacy schema handling, tracing-disabled correlation, frontend redaction and the bounded exporter safety checks. A published HTTPS rehearsal uses the actual runtime/operations grant template and checks negative SQL privileges, export and sensitive canaries. Another rehearsal upgrades a populated synthetic backup and confirms old events remain version 0 before exercising legacy journeys. See [evidence](../evidence/saas013-structured-audit.json).

No live development/customer database was migrated by this implementation. Fresh hosted CI/CodeQL/container results and selected-host privilege/monitoring verification remain gates. Next is SaaS-014: the company audit console's filters, deleted-resource review and bounded exports.
