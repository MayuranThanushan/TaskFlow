# TaskFlow feature release roadmap

Prepared: 25 October 2026, from code baseline 18ba5f6. Audience: multiple companies using TaskFlow as SaaS. All versions below are proposed planning labels, not published releases or Git tags. The original MVP steps 01-23 remain historical and complete subject to their external release gates.

Read the [gap assessment](Product-Gap-Assessment.md) for evidence and the [SaaS architecture proposal](../docs/saas-architecture.md) for boundaries. New work uses SaaS-001 onward so it does not rewrite existing TF/MVP traceability.

## Release sequence

| Release | Outcome | Main scope | Release gate |
| --- | --- | --- | --- |
| R0 / 0.9.1 | Safe delivery baseline | Tenant/role ADR, accurate contracts, full-stack CI, telemetry, deployment/recovery evidence, modular UI cleanup | Reproducible candidate and passing real environment checks; no public readiness claim based on local mocks |
| R1 / 1.0 private pilot | Separate companies safely | Workspaces, company switching, roles/lifecycle, tenant enforcement, structured/security audit, edit conflicts, pilot quotas | Adversarial two-company isolation matrix passes for every route/query/export/job; migrated data preserves access |
| R2 / 1.1 | Self-service team onboarding | Durable email delivery, verified accounts/recovery, expiring email invitations, MFA option | Invitation/recovery abuse/replay/revocation tests and real staging mail delivery pass before open signup |
| R3 / 1.2 | Collaboration that reaches people | Inbox, notification preferences, reminders/digests, task comments, mentions and watchers | Replay/delivery-failure/removal/privacy tests pass; members receive relevant updates without notification floods |
| R4 / 1.3 | Visual and personal work management | Kanban, conflict-safe order/moves, My work, authorized search and saved views | Large board counts/paging are complete; list/board agree; pointer and keyboard alternatives work |
| R5 / 1.4 | Deeper planning and project artifacts | Tags/subtasks/dependencies/milestones/calendar/recurrence; private scanned attachments | Planning rules and storage quarantine/quota/download/deletion checks pass. Ship 1.4A planning and 1.4B files independently |
| R6 / 1.5 | Commercial SaaS operation | Plans/entitlements/seats, billing lifecycle, customer export/offboarding, integrations and support tools | Billing idempotency/reconciliation and customer-lifecycle checks pass before charging customers |
| R7 / 2.0 | Enterprise extension | Federated SSO/SCIM, stronger audit archive/retention, customer-specific control evidence | Named customer requirements, independent security review and recovery/access demonstrations accepted |

Small means a focused module; Medium spans API/UI/tests; Large crosses policy/schema/operations. These are relative sizing signals, not calendar promises. Re-estimate after the tenant migration prototype. Each release can have small patch deliveries; do not combine all rows into one unreviewable change.

SaaS-001 completed on 25 October 2026 as a design task: [ADR 0005](../docs/decisions/0005-commercial-saas-boundaries.md), [permissions](../docs/saas/permissions.md), [migration/cutover](../docs/saas/migration-cutover.md), and [24 planned threat cases](../docs/saas/tenant-threat-model.md). Tenant runtime and tests remain pending. SaaS-002 implementation and local verification completed on 25 October 2026; hosted gates remain open; [CI gate policy](../docs/ci-release-gates.md) records required hosted evidence. SaaS-003 typed contracts and drift checks are implemented; see the [contract workflow](../docs/contracts/README.md). SaaS-004 telemetry is implemented locally on 26 October 2026; see [observability](../docs/observability.md). SaaS-005 selected-host verification remains pending hosting; SaaS-006 UI modularization can proceed independently.

## R0 - release hardening

| Order / ID | Work | Dependencies | Acceptance criteria | Size |
| --- | --- | --- | --- | --- |
| 01 / SaaS-001 | Record company/workspace boundary, role matrix, private-project policy, identity strategy and tenant threat model | Gap assessment | Explicit user/company/project/guest definitions; cross-company negative cases; migration mapping and API cutover decision recorded | M |
| 02 / SaaS-002 | Establish authoritative CI baseline, including published-app real-browser checks | Existing CI and browser suite | Backend no-skip MySQL, frontend, operations and actual HTTP/browser mutation flows run in CI; dependency/secret/static scans and SBOM/provenance policy defined; attach reports | M |
| 03 / SaaS-003 | Complete typed API/security/error contracts and contract drift verification | 001 | Current request/response types including PATCH, session/CSRF security, query allowlists and errors accurately described; drift check fails an intentional mismatch | M |
| 04 / SaaS-004 | Add baseline service metrics, trace export and safe security-event vocabulary | 001 | Request errors/latency, DB readiness, worker-ready metric definitions and trace correlation available; no secret/high-cardinality tenant labels in fleet metrics | M |
| 05 / SaaS-005 | Exercise container/migration/HTTPS/restart/recovery on selected staging host | 002; hosting required | Run existing deploy smoke and restore rehearsal on target; prove secrets, keys, grants, rollback and strict public HTTPS; record results | M |
| 06 / SaaS-006 | Split shared UI primitives and feature hooks without changing access behavior | Current UI and 003 | Shared fields/confirmations/tables, consistent loading/error recovery; 95-test baseline retained; 42-scan baseline and real API journeys green | M |
| 07 / SaaS-007 | Capture representative load, quotas and operating objectives for pilot | 004; 005 for host measurements | Agreed tenant/user/data/concurrency fixture; p95/error/DB-lock baseline; proposed SLOs labeled until measured; backup/alert ownership assigned | M |

SaaS-002 requires a successful hosted run and repository protection configuration; the GitHub remote is now configured. SaaS-005 requires a selected staging host. Local code, contract and tenancy work can continue while those decisions are pending. Do not create cloud accounts, publish or incur provider charges merely because a row lists them.

## R1 - company workspaces, integrity and audit

| Order / ID | Work | Dependencies | Acceptance criteria | Size |
| --- | --- | --- | --- | --- |
| 08 / SaaS-008 | Add Workspace/WorkspaceMembership and staged legacy backfill | 001, 003 | Reviewed add/backfill/enforce migration; one unambiguous workspace per project; legacy IDs preserved; access reconciliation and restore plan verified | L |
| 09 / SaaS-009 | Enforce tenant membership and scope on every read path | 008 | Projects/tasks/dashboard/activity/member lists and counts cannot leak another company; guessed IDs and mixed workspace URLs return safe denial | L |
| 10 / SaaS-010 | Enforce tenant scope on writes and relational references | 009 | Cross-company assignee/project/member/event references rejected; tenant-aware FK/index constraints; bypass/raw-SQL paths reviewed; existing removal races still pass | L |
| 11 / SaaS-011 | Add company creation/switching and workspace-aware client/resource keys | 009, 010, 006 | Switching clears data/drafts and ignores late old-company results; no queued write is retargeted to the newly selected company; project URLs carry context | M |
| 12 / SaaS-012 | Implement company roles, project viewer/editor/owner, suspension, leave and ownership transfer | 010, 011 | Workspace admin is not silently a reader of every private project; last owner protected; removed/suspended users lose future reads/writes; assignee cleanup and transfer audited | L |
| 13 / SaaS-013 | Add versioned structured business audit and restricted security events | 010, 012, 004 | Changes capture safe field deltas and correlation; login/recovery/membership/admin events have subject/outcome; rollback produces no success audit; legacy events identified explicitly | L |
| 14 / SaaS-014 | Add company audit console, filters, deleted-resource review and bounded export jobs | 013 | Search by actor/action/entity/date/outcome; authorization/redaction preserves private project boundaries; project deletion remains auditable; export access rechecked at download | L |
| 15 / SaaS-015 | Add Project/Task expected-version conflicts and conflict-resolution UI | 010, 003 | Two tabs editing a stale entity cannot silently overwrite; 409 preserves draft and offers reload/compare; invalid cross-company access is checked first | M |
| 16 / SaaS-016 | Add tenancy regression suite, pilot quotas and privacy/retention settings | 011-015, 007 | Two companies, one shared user, guest, outsider, suspended user and support operator tested; quota enforcement is atomic; isolation/load/migration reports accepted | L |

R1 is a controlled pilot, not an invitation to open unrestricted public signup before R2. Keep company onboarding operator-controlled until verified identity and mail delivery are ready. Baseline quotas start here; they must not wait for paid billing.

## R2 - secure onboarding and invitations

| Order / ID | Work | Dependencies | Acceptance criteria | Size |
| --- | --- | --- | --- | --- |
| 17 / SaaS-017 | Add outbox, delivery records, worker leasing/retry/dead-letter support | 013, 016 | Business transaction and event enqueue commit together; restart/replay and competing workers tested; no exactly-once external-delivery claim | L |
| 18 / SaaS-018 | Add email abstraction, templates, local mail capture and staging delivery | 017 | Sender isolated from HTTP latency; retry-safe message IDs, bounce/failure visibility, private secrets and per-company sending limits; no real email from tests | M |
| 19 / SaaS-019 | Add email verification, password recovery/change and reauthentication flows | 018 | Framework tokens expire/are bounded, reset replay fails, generic recovery response resists enumeration, session revocation follows password change; accessibility/error handling pass | L |
| 20 / SaaS-020 | Add tenant/project-scoped invitation API and lifecycle | 012, 019 | Pending/accepted/revoked/expired states, hashed single-use token, correct verified email, seat reservation, safe resend, duplicate/race/revocation checks | L |
| 21 / SaaS-021 | Add invite/accept/manage-team UI for new and existing accounts | 020, 011 | Clear company/role/project preview; intended return path after signup/login; expired/wrong-account links recover safely; no access until accept commits | M |
| 22 / SaaS-022 | Add optional MFA/recovery-code management and privileged-action policy | 019 | Enroll/verify/disable/recover tested; secret/recovery codes excluded from audit/logs; company-admin MFA requirement can be enforced with safe recovery | L |
| 23 / SaaS-023 | Verify onboarding delivery, abuse controls and tenant acceptance | 017-022, 005 | Replay, resend throttling, revoked inviter, wrong tenant, expired token and mail outage tested; real staging delivery and support runbook complete | M |

## R3 - notifications and conversations

| Order / ID | Work | Dependencies | Acceptance criteria | Size |
| --- | --- | --- | --- | --- |
| 24 / SaaS-024 | Add in-app notification materialization/read/unread API | 017, 012 | Unique recipient/event delivery key; membership checked when producing and reading; count/list consistent; no revoked-company title disclosure | M |
| 25 / SaaS-025 | Add inbox UI and per-user/per-company channel/event preferences | 024, 018 | Read/unread, bulk mark-read, accessible deep links and preference controls; self-action noise suppressed; disabled channels honored | M |
| 26 / SaaS-026 | Add due reminders, digest scheduling and delivery status | 025 | UTC scheduling with user timezone/DST tests; rescheduled/completed/deleted tasks suppress stale reminders; leases/retry prevent duplicate inbox rows | L |
| 27 / SaaS-027 | Add comments and safe mention parsing/resolution | 013, 024 | Text sanitized/escaped; edit/deletion history policy defined; only visible same-project recipients resolvable; no member enumeration across companies | L |
| 28 / SaaS-028 | Add watchers and notification grouping/noise control | 027, 025 | Subscribe/unsubscribe affects future delivery; grouped events remain traceable; quiet/digest preference and recipient limits respected | M |
| 29 / SaaS-029 | Test revocation races and delivery reliability under load | 024-028 | Removal before send suppresses queued private content; worker crash/provider timeout/replay tested; backlog/error/dead-letter alerts actionable | M |

## R4 - Kanban, My work and saved views

| Order / ID | Work | Dependencies | Acceptance criteria | Size |
| --- | --- | --- | --- | --- |
| 30 / SaaS-030 | Add board read model with status counts and per-column pagination | 015, 016 | ToDo/InProgress/Done remain canonical; more than 100 tasks load without silently omitting cards; list and board share access predicates | M |
| 31 / SaaS-031 | Add server-owned rank and atomic move/reorder API | 030 | Status/rank/version/audit/outbox commit together; concurrent moves conflict/reconcile; bounded rebalance; unknown neighbor IDs and foreign-company cards rejected | L |
| 32 / SaaS-032 | Add responsive board UI, keyboard moves and non-drag controls | 031, 006 | Drag, keyboard and click-to-move work; optimistic error rollback; progress/counts update after commit; focus announcements and mobile overflow pass | L |
| 33 / SaaS-033 | Add tenant-scoped My work, due/overdue queues and authorized search | 009, 015 | One query across authorized projects, deterministic paging, unassigned semantics and inclusive date rules; no client-side cross-project fetch fan-out | M |
| 34 / SaaS-034 | Add saved views and persisted list/board/filter preferences | 033, 032 | Personal vs company-shared visibility explicit; saved filters reauthorize on use; deleted fields/projects handled safely; share links preserve context | M |
| 35 / SaaS-035 | Verify board scale, conflicts, accessibility and adoption | 030-034 | Representative large-company fixture, stale-move races, keyboard/pointer alternatives and common browser checks pass; baseline WIP indicators are advisory | M |

Custom workflow/column editors are deferred until actual usage justifies them. Do not duplicate task records for board cards or implement a board by loading only the first task-list page.

## R5 - planning and attachments

| Order / ID | Work | Dependencies | Acceptance criteria | Size |
| --- | --- | --- | --- | --- |
| 36 / SaaS-036 | Add labels/checklists/subtasks with scoped filters | 016, 034 | Parent/project/company consistency, soft-delete behavior and progress semantics tested; no accidental double counting in current dashboard | M |
| 37 / SaaS-037 | Add blocked-by/related task dependencies | 036 | Self/cyclic/cross-company edges rejected; deletion/restore and concurrent edits handled; accessible dependency explanation | M |
| 38 / SaaS-038 | Add milestones/calendar/recurrence in small increments | 026, 036 | Due-date semantics preserved; repeat instances have stable dedup keys and timezone/DST coverage; progress/history remain explainable | L |
| 39 / SaaS-039 | Add private storage, upload grants, quarantine/scanning and lifecycle jobs | 017, 016; storage/scanner decision | Type/size/signature limits, randomized keys and tenant scope; unscanned files unavailable; failure/quota/cleanup paths tested | L |
| 40 / SaaS-040 | Add attachment UI and authorized downloads | 039 | Filename escaping, revoked-access denial, expiring downloads, progress/error/cancel states, and comment/task lifecycle integration | M |
| 41 / SaaS-041 | Rehearse content recovery, retention and planning/storage load | 036-040 | DB/object consistency, failed scan, orphan cleanup, restore and project removal tested; cost/usage metrics available | M |

## R6/R7 - paid SaaS and enterprise

| Order / ID | Work | Dependencies | Acceptance criteria | Size |
| --- | --- | --- | --- | --- |
| 42 / SaaS-042 | Add versioned entitlements, seat/guest rules and atomic usage accounting | 016, 020 | Disabled feature cannot be enabled through direct API; duplicate invite/accept races cannot exceed seats; pilot limits migrate cleanly | L |
| 43 / SaaS-043 | Integrate chosen billing provider and signed/idempotent webhook processing | 042, 017; provider decision | Verify provider signature, amount/currency/customer mapping and event deduplication; out-of-order/retry/reconciliation tested in sandbox | L |
| 44 / SaaS-044 | Add plan/seat portal, renewal failure and cancellation/downgrade behavior | 043 | Billing owner permissions explicit; grace/read-only/export policy documented; customer data not silently deleted on failed payment | M |
| 45 / SaaS-045 | Add customer export, workspace closure and governed purge | 014, 041, 044 | Authenticated export/status/download; grace/retention policy honored; shared global user accounts and other companies unaffected | L |
| 46 / SaaS-046 | Add support diagnostics, fleet observability and multi-replica readiness | 007, 029, 044 | Tenant context safe in support tools; shared keys/limiter/job leasing verified; approved support access is attributable; recovery rehearsal current | L |
| 47 / SaaS-047 | Add scoped API credentials and signed outbound webhooks | 017, 042 | Least-privilege/revocable credentials, redacted logs, destination/SSRF controls, delivery retries and per-company limits | L |
| 48 / SaaS-048 | Add enterprise OIDC/SAML through an identity integration strategy | 022, 046; customer demand | Issuer/subject mapping, tenant binding, account linking, logout and enforced sign-in policy tested; no automatic cross-company joining by email domain | L |
| 49 / SaaS-049 | Add SCIM lifecycle and enterprise audit/archive policies | 048, 014 | Provision/deprovision respects company membership/private projects; immutable external archive and access/retention evidence tested | L |
| 50 / SaaS-050 | Address customer-specific residency/dedicated deployment/retention needs | 049; explicit customer requirements | Architectural and operational evidence meets named requirements; do not claim jurisdictional compliance from generic feature presence | L |
| 51 / SaaS-051 | Complete independent review and enterprise acceptance pack | 048-050 | Applicable security-control traceability, remediation/retest, recovery and privileged-access demonstration accepted | L |

## Rules for every release

1. Implement a vertical slice: schema/API/policy/UI/events/tests/docs, with migration and rollback/restore implications reviewed.
2. For every tenant-scoped feature test outsider, other company, shared multi-company user, guest, removed/suspended member and stale client. Include workers, exports, storage keys and billing webhooks.
3. Enforce tenant context before returning existence-sensitive conflict/data details. Preserve private-project visibility within a company.
4. Commit mutation, business audit and outbox enqueue atomically where applicable. External delivery is asynchronous and retryable.
5. Keep string IDs/UTC/error/CSRF contracts. Publish accurate contracts; do not let unsupported old endpoints bypass the new tenant boundary.
6. Confirm usability with real keyboard/mobile flows. Mocked UI reviews supplement real API/database browser checks; they do not replace them.
7. Use flags for incomplete features. Hide unavailable controls. Every side effect has duplicate/retry/idempotency behavior and an observable failure state.
8. Roll out to a pilot company first; monitor and reconcile before expanding. Each release needs a clean source receipt, evidence and explicit known limitations.

## Recommended next execution

SaaS-001 design is recorded; finish the SaaS-002 hosted gates and proceed with SaaS-003 typed contracts before the tenant migration prototype. Produce a tested legacy-to-workspace mapping before implementing invitation emails. Adopted boundaries and remaining proposals are in the architecture document; hosting/mail/billing providers remain decisions at the point they are needed. This roadmap itself creates no external accounts/issues/releases and changes no runtime behavior.
