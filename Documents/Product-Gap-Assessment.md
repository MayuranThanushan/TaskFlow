# TaskFlow product and SaaS gap assessment

Review date: 25 October 2026. Baseline: commit 18ba5f6. Product direction confirmed by the user: multiple companies using TaskFlow as SaaS. This assessment proposes future work; it does not implement or ship the features below.

## Conclusion

TaskFlow is a capable project-based MVP, not yet a multi-company SaaS platform. Keep its modular .NET/React/MySQL architecture and working project/task flows. First establish company/workspace isolation, accountable administration and dependable delivery, then add invitations, notifications and Kanban on that foundation. Adding a tenant selector to the current UI would not establish tenant isolation.

The existing project-access policy is a useful security control. The absence of a company model is a product gap for the new SaaS requirement, not evidence that the existing private-project policy is bypassable.

## Scope and evidence

The review inventoried 121 tracked implementation, test and operations source files and 39 Markdown documents, and inspected all seven original Word specifications; generated EF designer/snapshot files were inspected separately for concurrency metadata. Focused code review covered entities, schema/configuration, account/session handling, every controller, project authorization/transactions, activity/query services, frontend routes/client/resource lifetimes, logging, CI, containers, migration/recovery and browser tests. Local secret files, database content, dependencies/build output and production systems were excluded.

The original Word scope explicitly deferred invitations, notifications and Kanban. The latest UI already has an activity center and current-page JSON export: those are not missing features. Test counts are existing recorded evidence: 122 backend, 95 frontend, 16 operations checks; the latest UI review has 42 synthetic-API screen/viewport scans. This planning review did not rerun suites, load tests, penetration tests, deployed-host checks or a live SIEM ingestion test.

## What to preserve

- Owner/member authorization, inaccessible-resource 404s, CSRF-protected cookie sessions, stamp-based logout revocation and strict HTTPS defaults outside Local/Development.
- Transactional mutations plus activity writes; deterministic member-removal/assignment protection.
- Stable string IDs, UTC timestamps, validation/error conventions, server filtering/sorting/paging and soft-delete retention.
- Responsive forms/table/inline task status, dashboard, project-scoped activity timeline and safe current-page JSON download.
- Locked dependencies, real-MySQL fixtures, migration/readiness checks, release receipts, restricted-account recovery rehearsal and privacy-aware request logging.

## Findings and gaps

P0 = gate for admitting multiple companies/public release. P1 = next core product release. P2 = growth capability. P3 = customer-driven/enterprise extension. Priorities concern the new SaaS product, not an assertion that every missing capability is a present defect.

| ID | Priority | Current finding and evidence | Impact | Proposed resolution |
| --- | --- | --- | --- | --- |
| G01 | P0 | No Workspace/Organization entity or tenant key on Projects, Tasks, ActivityLogs or memberships: [entities](../TaskFlow-BE/TaskFlow.Domain/Entities/Entities.cs), [schema](../TaskFlow-BE/TaskFlow.Infrastructure/Persistence/Configurations/EntityConfigurations.cs). | Cannot express a company boundary or safely scope future company administration. | Workspace model, membership and enforced tenant scope across DB/API/UI/jobs/files/exports. |
| G02 | P0 | Only project Owner/Member roles; owner identity is immutable inside [transactions](../TaskFlow-BE/TaskFlow.Infrastructure/Projects/ProjectTransactions.cs). | No company admin, guest/read-only policy, owner handover or deprovisioning lifecycle. | Separate workspace roles from project permissions; explicit transfer, suspension, last-owner and leave policies. |
| G03 | P0 | Project row locks serialize mutations, but Project/Task have no client expected-version contract: [transactions](../TaskFlow-BE/TaskFlow.Infrastructure/Projects/ProjectTransactions.cs), [task requests](../TaskFlow-BE/TaskFlow.Api/Controllers/TasksController.cs). Identity concurrency stamps do exist. | Two editors can overwrite a changed title/description without a stale-edit warning. | App-managed entity revisions and conflict-aware forms/inline updates; keep membership locks. |
| G04 | P0 | [Account service](../TaskFlow-BE/TaskFlow.Infrastructure/Identity/AccountService.cs) implements register/login/profile/logout only; no verification/recovery flows or configured email-token delivery. | Cannot safely self-serve recovery or prove control of invitation email addresses. | Verified email, reset/change password and reauthentication, using framework token providers and durable delivery. |
| G05 | P0 | Containers, hosted CI and chosen-host smoke remain unexecuted in the [runbook](../docs/release-runbook.md). | Local verification cannot establish public-service readiness. | Real CI/container/host rehearsal, HTTPS/proxy/secrets checks and production rollback/recovery evidence. |
| G06 | P0 | ActivityLog has actor/project/task/action/details/time, but no general entity reference, schema version, correlation or comprehensive before/after contract. Detail JSON already records assignment and status information: [writer](../TaskFlow-BE/TaskFlow.Infrastructure/Projects/ProjectTransactions.cs), [reader](../TaskFlow-BE/TaskFlow.Infrastructure/Projects/ActivityService.cs). | Cannot reliably explain all changed fields or correlate future company-wide actions. | Versioned structured audit events and field allowlists; retain legacy summaries as legacy events. |
| G07 | P0 | [Activity query filter](../TaskFlow-BE/TaskFlow.Infrastructure/Persistence/Configurations/EntityConfigurations.cs) hides all events whose parent project is deleted. Individual deleted-task events remain visible in active projects. | Company administrators cannot review project-deletion history through current routes. | Separate privileged tenant audit query that can include deleted resources without reopening their ordinary content routes. |
| G08 | P0 | [File sink](../TaskFlow-BE/TaskFlow.Api/Logging/SiemFileLoggerProvider.cs) is allowlisted operational NDJSON; no company/actor/security-outcome audit service or proven external retention. | Successful request status alone is not an attributable company security audit. Rotated local files are not tamper-evident evidence. | Separate security events, company-scoped audit authorization, external collection/alerts and documented retention. |
| G09 | P0 | No durable outbox/worker, event consumers, reminder scheduler or email sender in tracked services. | Directly adding email to request handlers would introduce lost/duplicate-message and latency risks. | MySQL-backed transactional outbox, worker leases, retries, deduplication and delivery visibility. |
| G10 | P1 | [Membership](../TaskFlow-BE/TaskFlow.Api/Controllers/ProjectMembersController.cs) accepts an existing user ID; no Invitation entity/routes. | Company onboarding is manual and cannot safely invite a new or unverified account. | Email invitation lifecycle: accept, reject/cancel, expiry, resend and role/project scope. |
| G11 | P1 | No inbox, notification preferences, subscriptions or delivery records. | Users must repeatedly inspect task/project screens to discover changes. | In-app inbox first, then email preferences, assignment/mention updates, reminders and digests. |
| G12 | P1 | Three fixed task states, no persisted board rank/columns; table inline status exists. | No visual workflow or stable manual prioritization. | Accessible three-column Kanban with per-column paging/counts and conflict-safe moves; custom workflows later. |
| G13 | P1 | Task lists are project-scoped; dashboard shows counts and one recent-project preview. | No cross-project My work, due/overdue queue, persisted views or global authorized search. | Tenant-scoped personal queues and saved views using the same access predicates as existing task queries. |
| G14 | P1 | No comments, mentions or watcher entities. | Decisions/discussion happen outside the task and cannot drive focused alerts. | Task conversations with edit/deletion policy, sanitized text, scoped mentions and subscriptions. |
| G15 | P1 | Soft deletion exists but no archive, trash, restore or ownership-transfer API. | Users cannot recover accidental deletion or manage inactive work and staff departure. | Archive vs delete semantics, bounded restore window and explicit ownership transfer. |
| G16 | P1 | [Activity hub](../TaskFlow-FE/src/components/Workspace.tsx) lists the first 100 projects; [export](../TaskFlow-FE/src/features/activity/ActivityPage.tsx) covers only the current page. | Neither is a searchable company audit console or complete asynchronous export. | Server-side tenant audit filters/cursor paging and permission-checked export jobs; keep current-page export clearly labeled. |
| G17 | P1 | Frontend feature files combine queries/forms/actions and [CSS](../TaskFlow-FE/src/workspace.css) layers many overrides; OpenAPI documents some dynamic payloads incompletely. | Feature growth will increase change risk and contract drift. | Incremental shared form/dialog/table primitives, feature hooks, split components and accurate typed contracts. |
| G18 | P1 | Project mutations lock a parent project; queries use offset paging and substring search; [indexes](../TaskFlow-BE/TaskFlow.Infrastructure/Persistence/Configurations/EntityConfigurations.cs) are limited to the current model. | Hot projects and larger tenant datasets may contend or scan; no demonstrated capacity failure is claimed. | Representative tenant/load measurements, tenant-first indexes, bounded export/jobs, keyset feeds and query-plan review before optimization. |
| G19 | P1 | Local request logs/readiness exist, but no application metrics/tracing exporter, error-budget dashboard, tenant-aware quota or fleet limiter. | Cannot manage noisy neighbors, delivery backlog or service objectives across companies. | Measured SLOs, central traces/metrics, per-tenant quotas and shared limiter when multiple replicas are introduced. |
| G20 | P2 | No tags, subtasks, dependencies, recurring tasks, milestones or calendar. | Limited planning depth beyond single independent tasks. | Incremental planning model after collaboration/board adoption; cycle-safe dependencies and timezone-safe recurrence. |
| G21 | P2 | No attachments/object-store pipeline or file scanning/download authorization. | Cannot collaborate on artifacts; adding naive uploads would expand security/storage costs. | Private object storage, validation/quarantine/scanning, quotas, expiring authorized downloads and deletion lifecycle. |
| G22 | P2 | No subscription/entitlement model, seat accounting, billing webhooks or customer administration. | Cannot operate a paid multi-company product consistently. | Pilot limits immediately; versioned plan entitlements, seats and provider-verified idempotent billing before charging customers. |
| G23 | P2 | No webhooks, public API credentials, calendar integration or tenant-scoped connector settings. | Work cannot connect reliably to customers' tools. | Scoped/revocable credentials, signed webhook delivery with retries, and outbound destination controls. |
| G24 | P3 | No MFA setup UI, enterprise federation or SCIM. Current password/session controls are useful but narrower: [auth configuration](../TaskFlow-BE/TaskFlow.Api/Extensions/AuthenticationExtensions.cs). | Cannot satisfy stronger customer identity requirements. | MFA/recovery codes early in SaaS onboarding; OIDC/SAML broker and SCIM when demanded by enterprise customers. |
| G25 | P1 | [CI](../.github/workflows/ci.yml) has build/test/deployment smoke, but no scheduled dependency/secret/static-security scan or SBOM job. [Release receipts](../scripts/release_manifest.py) provide checksums, not publisher signatures. | Dependency drift and provenance risks are not continuously checked by the current pipeline. | Add scoped vulnerability/secret/static checks, dependency update policy, SBOM and deployment artifact provenance/signing according to the chosen host. |

## Product benchmark: patterns, not mandatory feature parity

Formal security/accessibility guidance and common product capabilities are different. Treat ASVS and WCAG as verification inputs; treat competitor capabilities as evidence of user expectations. No compliance certification is claimed.

| Reference | Relevant observed pattern | Recommendation for TaskFlow |
| --- | --- | --- |
| [Asana people management](https://help.asana.com/s/article/managing-people-in-organizations) | Email-based organization/team/project invitations and membership management. | Give invitations an explicit company and project scope instead of making users exchange IDs. |
| [Linear notifications](https://linear.app/docs/notifications), [inbox](https://linear.app/docs/inbox) | An in-app inbox, subscriptions and configurable notification channels/timing. | Use an inbox with preferences and sensible grouping; do not email every activity event. |
| [Linear custom views](https://linear.app/docs/custom-views) | Saved filtered views across authorized work. | Add My work and reusable views over the existing server filtering foundation. |
| [Atlassian boards](https://www.atlassian.com/software/jira/guides/boards/tutorials), [Kanban guidance](https://www.atlassian.com/agile/tutorials/how-to-do-kanban-with-jira) | Board columns represent workflow states; limits help reveal excessive work in progress. | Start with the existing three states and advisory WIP limits, not a custom workflow engine. |
| [Asana audit events](https://developers.asana.com/docs/audit-log-events) | Organizational security events include login, user and invitation changes; audit API access is a separately controlled capability. | Separate company security audit from ordinary project activity; no assumption that Asana's plan/tier choices apply here. |

## Verification guidance used

- [Microsoft multitenant identity](https://learn.microsoft.com/en-us/azure/architecture/guide/multitenant/considerations/identity): authentication must be combined with authorization within tenant scope. Tenant context is not established by an unvalidated browser selector.
- [EF Core concurrency](https://learn.microsoft.com/en-us/ef/core/saving/concurrency): detect stale writes using an original concurrency value; serializing a later request does not identify its old editing baseline.
- [OWASP logging](https://cheatsheetseries.owasp.org/cheatsheets/Logging_Cheat_Sheet.html): define attributable security events/correlation without recording credentials or arbitrary sensitive payloads.
- [AWS transactional outbox](https://docs.aws.amazon.com/prescriptive-guidance/latest/cloud-design-patterns/transactional-outbox.html): commit business data and outgoing event state together; consumers still need duplicate handling.
- [OWASP recovery](https://cheatsheetseries.owasp.org/cheatsheets/Forgot_Password_Cheat_Sheet.html): design token lifecycle and account-enumeration resistance before adding reset email.
- [W3C dragging guidance](https://www.w3.org/WAI/WCAG22/Understanding/dragging-movements): drag-and-drop needs a non-drag pointer alternative as well as keyboard access.
- [OWASP file upload guidance](https://cheatsheetseries.owasp.org/cheatsheets/File_Upload_Cheat_Sheet.html): validate, isolate and authorize uploads/downloads; postpone attachments until that pipeline exists.
- [OWASP ASVS](https://asvs.owasp.org/): select and trace applicable security controls; passing a local UI scan is not equivalent to security verification.

## Recommendation

Use the release scope in the [feature roadmap](Feature-Release-Roadmap.md) and the proposed [SaaS architecture](../docs/saas-architecture.md). Begin with SaaS-001 and SaaS-002: company-boundary/permission decisions and a CI/deployment evidence baseline. Tenant implementation can proceed locally while external hosting gates are pending, but no multi-company/public release should be declared ready until its isolation and operational gates pass.
