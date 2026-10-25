# Tenant threat model and acceptance catalog

Status: design acceptance criteria for [ADR 0005](../decisions/0005-commercial-saas-boundaries.md). All cases below are **planned, not executed**. Existing MVP tests do not establish company isolation.

Assets: private project/task content, identity/session secrets, membership/ownership, business audit, exports/files, billing and recovery snapshots. Trust boundaries: browser/API, global identity/company authorization, application/MySQL, API/worker/provider, company/support, runtime/migration/retention identities. Threat actors include outsiders, malicious members, admins exceeding authority, compromised sessions, replaying workers and mistaken operators. Database administrator compromise is outside application row isolation; restrict and audit infrastructure access separately.

Fixture: companies A/B; Alice owns A, Bob owns B; Sam is Editor in A and Viewer in B; Gina is A Guest/Editor in one project only; Vera is A Viewer; Seth is suspended in A but active in B; Oscar is authenticated outsider; Pat is a support operator without content grants. Include two private projects in A, deleted project/task history, an orphaned project and an unassigned task. Tests must assert response shape, stored state and side effects, not just status codes.

| Case | Attack or failure | Required result | Implementation owner |
| --- | --- | --- | --- |
| TEN-01 | Oscar guesses A workspace/project/task IDs | 404; no content/count/existence detail; anonymous request 401 | 009 |
| TEN-02 | Sam supplies B task under A route, for GET/PATCH/DELETE | 404; no write, conflict payload, audit success or outbound side effect | 009/010 |
| TEN-03 | A list/dashboard/search/activity uses B filter, cursor or entity ID | No B rows/counts/metadata; malformed context rejected consistently | 009/014/033 |
| TEN-04 | A task is assigned to B-only user or attached to B project | Rejected before mutation; composite parent constraints block direct invalid references | 010 |
| TEN-05 | Admin adds self to private project or reads its audit body | No content access or implicit grant; governance audit redacted | 012/014 |
| TEN-06 | Gina reads another A project, Vera writes, or Guest creates project | Safe denial; project Editor guest retains editing only in granted project | 012 |
| TEN-07 | Seth reuses old cookie after A suspension | A access denied from current membership; B remains usable | 012 |
| TEN-08 | Concurrent assignment/removal/suspension or ownership demotion | Serializable policy outcome; no ineligible active assignee or zero active company Owners | 010/012 |
| TEN-09 | Caller requests stale version of inaccessible resource | Access denial before conflict details; authorized stale edit gets 409 with draft preserved | 015 |
| TEN-10 | Switch A to B while reads/writes complete late | No A data rendered in B; write retains captured A context and is never retargeted | 011 |
| TEN-11 | Missing context, pooled service, raw SQL or IgnoreQueryFilters path | Fail closed; explicit scope on every query/mutation; no singleton tenant leakage | 009/010 |
| TEN-12 | Read deleted project via ordinary route or old v1 tenant endpoint | Ordinary resource 404; retired authenticated v1 route uniform 410; identity endpoints remain usable | 009/011/014 |
| TEN-13 | Read company export after permission removal; tamper with export/object ID | Read/download reauthorizes; no other company's artifact/content; bounded export | 014/040 |
| TEN-14 | Mutation fails after audit/outbox insertion; worker replays event | No success event for rollback; committed business/audit/outbox atomic; consumer dedup | 013/017 |
| TEN-15 | Recipient removed before queued notification/mail is delivered | Recheck eligibility before send/read; suppress unauthorized queued content | 023/029 |
| TEN-16 | Wrong-email, expired, revoked or replayed invite; revoked inviter | No membership/token reuse or private preview leak; acceptance checks atomic | 020/023 |
| TEN-17 | Pat supplies arbitrary company ID or uses support role as reader | Denied absent explicit bounded grant; recovery/break-glass independently authorized/audited | 012/046 |
| TEN-18 | Audit tampering, raw descriptions/secrets in SIEM, global login copied to tenants | Runtime grants prohibit audit update/delete; safe fields only; global security access restricted | 004/013 |
| TEN-19 | Forged/replayed billing webhook maps another company or exceeds seats | Signature/idempotency/server mapping enforced; no client tenant override; atomic entitlements | 042/043 |
| TEN-20 | Restart migration or restore legacy data with deleted-only owner/shared collaborator | Stable mapping/IDs/access preserved; no inferred merges; failures stop cutover | 008/016 |
| TEN-21 | Large tenant monopolizes queries, jobs, invites or storage | Atomic quotas and bounded pages/work/retries; measured fair operating limits | 007/016/041 |
| TEN-22 | CSRF, stolen session or forged role field attempts privileged change | Existing CSRF/session checks retained; role fields ignored/rejected; sensitive reauthentication enforced | 003/012/019 |
| TEN-23 | Unscoped cache, job payload or provider callback targets wrong workspace | Persisted authorized scope and server-side provider mapping; no content crossing tenants | 011/017/043 |
| TEN-24 | Backup/export/log access bypasses application authorization | Restricted storage/credentials, governed retention and rehearsed recovery; no public artifacts | 005/014/045 |

## Evidence required to close isolation gates

For SaaS-016, enumerate every route, query, SQL bypass, background entry point and artifact type; map it to applicable cases. Run live MySQL integration tests and real API/browser journeys with the fixture above, including synchronized race tests. Assert business/audit/outbox state after rejection/rollback. Record revision, migrations, configuration, commands and reports. Cases for later features remain release-specific gates; no R1 claim that not-yet-built mail/storage/billing is verified.

Residual risks: shared database compromise affects all customers; authorized users can copy visible content; already sent mail cannot be recalled; in-flight writes authorized before revocation can finish; backups retain historical data under the defined retention policy. Numerical quotas, production SLOs, host configuration, retention durations and incident owners must be settled in their roadmap tasks before launch. This catalog is a threat-informed starting point, not a penetration-test or compliance certification.
