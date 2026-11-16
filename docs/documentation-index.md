# TaskFlow documentation index

Reviewed for SaaS-033 on 16 November 2026. This index is the current entry point for every Markdown guide below. Original MVP plans, ADRs and numbered release records retain their release-time counts and follow-ups; they are evidence and design history, not a claim that the latest implementation still has those gaps.

## Current implementation and next work

- SaaS-029: [delivery reliability and private queue-health checks](saas/delivery-reliability.md), verified locally. Hosted worker activation, selected-host locking and human alert routing remain separate gates.
- SaaS-030: [board read model](saas/board-read-model.md), full filtered status counts and independently paged columns. The original SaaS-030 release added two GET routes without a migration (71 paths / 93 operations then); the current binary also needs SaaS-031 migration seventeen. The typed client/read foundation is extended by the SaaS-032 board screen.
- SaaS-031: [rank and atomic move/reorder API](saas/board-moves.md), captured task/neighbor revisions, atomic audit/outbox and bounded 500-card rebalance. Canonical columns now default to rank/asc; release-time API inventory was 72 paths / 94 operations.
- SaaS-032: [responsive accessible project board](saas/board-ui.md), lazy-loaded columns, independent paging, keyboard/click/drag confirmation, optimistic rollback and explicit conflict/focus recovery.
- SaaS-033: [My work, due/overdue queues and authorized search](saas/my-work.md), company-scoped compact summaries and URL-backed filters. Current inventory: 73 paths / 95 operations.
- Next: SaaS-034 saved views and persisted list/board/filter preferences.
- The database currently requires seventeen migrations through BoardRank. Follow [deployment repair](deployment-repair.md) for schema mismatch/system-schema errors; never use sys as the application database or publish database/email secrets.
- Local verification is recorded in [SaaS-033 evidence](evidence/saas033-my-work.json). Hosted CI, selected-host capacity, customer acceptance and commercial readiness must be assessed independently. No paid resource, real email activation or automatic migration is added by SaaS-033.

## Common workflows

| Need | Guide |
| --- | --- |
| Set up and run manually | [Repository README](../README.md), [database setup](database-setup.md), [logging/startup](logging-and-startup.md) |
| Understand company access and writes | [SaaS architecture](saas-architecture.md), [permissions](saas/permissions.md), [workspace reads](saas/workspace-reads.md), [workspace writes](saas/workspace-writes.md) |
| Build against the API | [Typed contract workflow](contracts/README.md), [API amendments](contracts/api-baseline.md), [board reads](saas/board-read-model.md) |
| Test or prepare a release | [Testing](testing.md), [CI gates](ci-release-gates.md), [release runbook](release-runbook.md), [backup/recovery](backup-recovery.md) |
| Operate hosted pilot | [Free hosting](free-hosting.md), [deployment repair](deployment-repair.md), [delivery reliability](saas/delivery-reliability.md), [pilot operations](pilot-operations.md) |
| Prioritize product work | [Release roadmap](../Documents/Feature-Release-Roadmap.md), [original gap review](../Documents/Product-Gap-Assessment.md) |

## Complete Markdown guide inventory

All listed guides were checked for release/navigation context. Current workflow/contract pages were updated for SaaS-033; original release evidence and ADR decisions remain identifiable as historical. Every guide links back here so its recorded next step is not confused with the current product backlog.

| Guide | Documentation role |
| --- | --- |
| [Project activity reader and UI - step 19](activity.md) | Current workflow and feature/reference guide |
| [API foundation](api-foundation.md) | Current workflow and feature/reference guide |
| [Accepted implementation architecture and ERD](architecture.md) | Current workflow and feature/reference guide |
| [Assignment cleanup and concurrent member removal - step 16](assignment-concurrency.md) | Current workflow and feature/reference guide |
| [Authentication and secure sessions](authentication.md) | Current workflow and feature/reference guide |
| [Private backups and fresh-target recovery](backup-recovery.md) | Current workflow and feature/reference guide |
| [SaaS-002: delivery and security verification](ci-release-gates.md) | Current workflow and feature/reference guide |
| [API contract amendments: implementation baseline](contracts/api-baseline.md) | Current workflow and feature/reference guide |
| [Database contract amendments: implementation baseline](contracts/database-baseline.md) | Current workflow and feature/reference guide |
| [Typed API contract and drift verification](contracts/README.md) | Setup, workflow or module entry point |
| [Dashboard totals and project progress - step 18](dashboard.md) | Current workflow and feature/reference guide |
| [Database implementation and setup](database-setup.md) | Current workflow and feature/reference guide |
| [0001: Structure and authentication](decisions/0001-structure-and-authentication.md) | Recorded architecture decision/history |
| [0002: Permissions, ownership and retention](decisions/0002-permissions-and-retention.md) | Recorded architecture decision/history |
| [0003: Data representation and API semantics](decisions/0003-data-and-api-semantics.md) | Recorded architecture decision/history |
| [0004: Runtime and database provider](decisions/0004-runtime-and-database-provider.md) | Recorded architecture decision/history |
| [0005: Commercial SaaS company boundaries](decisions/0005-commercial-saas-boundaries.md) | Recorded architecture decision/history |
| [Architecture decision register](decisions/README.md) | Recorded architecture decision/history |
| [Repair the Render database readiness failure](deployment-repair.md) | Current workflow and feature/reference guide |
| [Portable deployment and smoke verification - steps 09 and 22](deployment.md) | Current workflow and feature/reference guide |
| [Free public pilot hosting checklist](free-hosting.md) | Current workflow and feature/reference guide |
| [Frontend authentication - step 08](frontend-authentication.md) | Current workflow and feature/reference guide |
| [Documentation and demo handoff](handoff.md) | Current workflow and feature/reference guide |
| [Logging and local startup troubleshooting](logging-and-startup.md) | Current workflow and feature/reference guide |
| [Service metrics and trace export](observability.md) | Current workflow and feature/reference guide |
| [Pilot load and operating plan](pilot-operations.md) | Current workflow and feature/reference guide |
| [Profile display-name editing (step 20)](profile.md) | Current workflow and feature/reference guide |
| [Project access and transactional activity - step 10](project-foundation.md) | Current workflow and feature/reference guide |
| [Project membership - step 13](project-membership.md) | Current workflow and feature/reference guide |
| [Project workflows - steps 11 and 12](projects.md) | Current workflow and feature/reference guide |
| [Release, migration and recovery runbook (step 22)](release-runbook.md) | Current workflow and feature/reference guide |
| [Multi-company SaaS architecture](saas-architecture.md) | Current workflow and feature/reference guide |
| [Account verification, recovery and reauthentication](saas/account-security.md) | Current workflow and feature/reference guide |
| [Company audit console and bounded exports](saas/audit-console.md) | Current workflow and feature/reference guide |
| [SaaS-030: board read model, status counts and column pagination](saas/board-read-model.md) | Current workflow and feature/reference guide |
| [SaaS-027: task comments and safe mentions](saas/comments.md) | Current workflow and feature/reference guide |
| [Company creation and navigation](saas/company-navigation.md) | Current workflow and feature/reference guide |
| [SaaS-029: delivery reliability, revocation races and load verification](saas/delivery-reliability.md) | Current workflow and feature/reference guide |
| [Email abstraction, templates and local capture](saas/email-capture.md) | Current workflow and feature/reference guide |
| [Invitation onboarding and team management](saas/invitation-ui.md) | Current workflow and feature/reference guide |
| [Company and project invitation lifecycle](saas/invitations.md) | Current workflow and feature/reference guide |
| [MFA, recovery codes and privileged actions](saas/mfa.md) | Current workflow and feature/reference guide |
| [Legacy data mapping and v2 cutover](saas/migration-cutover.md) | Current workflow and feature/reference guide |
| [SaaS-025: company inbox and recipient preferences](saas/notification-inbox.md) | Current workflow and feature/reference guide |
| [SaaS-026: due reminders and daily inbox digest](saas/notification-scheduling.md) | Current workflow and feature/reference guide |
| [SaaS-024: recipient in-app notifications](saas/notifications.md) | Current workflow and feature/reference guide |
| [SaaS-023: onboarding acceptance and SendGrid staging](saas/onboarding-acceptance.md) | Current workflow and feature/reference guide |
| [Transactional outbox and checkpoint worker](saas/outbox-workers.md) | Current workflow and feature/reference guide |
| [Company and private-project permission contract](saas/permissions.md) | Current workflow and feature/reference guide |
| [Tenant regression, pilot capacity and review policies](saas/pilot-policy.md) | Current workflow and feature/reference guide |
| [Project/task revisions and conflict resolution](saas/resource-versions.md) | Current workflow and feature/reference guide |
| [Versioned business audit and platform security events](saas/structured-audit.md) | Current workflow and feature/reference guide |
| [SaaS-028: task watchers, grouping and noise controls](saas/task-watchers.md) | Current workflow and feature/reference guide |
| [Tenant threat model and acceptance catalog](saas/tenant-threat-model.md) | Current workflow and feature/reference guide |
| [Workspace expansion and legacy backfill](saas/workspace-expansion.md) | Current workflow and feature/reference guide |
| [Company and project lifecycle](saas/workspace-lifecycle.md) | Current workflow and feature/reference guide |
| [Workspace-scoped read preview](saas/workspace-reads.md) | Current workflow and feature/reference guide |
| [Scoped writes and relational references](saas/workspace-writes.md) | Current workflow and feature/reference guide |
| [Initial setup review](setup-review.md) | Current workflow and feature/reference guide |
| [Shared UI foundation](shared-ui.md) | Current workflow and feature/reference guide |
| [Task editing, status and soft deletion - step 15](task-mutations.md) | Current workflow and feature/reference guide |
| [Task filtering, sorting and pagination - step 17](task-queries.md) | Current workflow and feature/reference guide |
| [Task workflows - steps 14 and 15](tasks.md) | Current workflow and feature/reference guide |
| [Testing and continuous integration](testing.md) | Current workflow and feature/reference guide |
| [Toolchain baseline](toolchain.md) | Current workflow and feature/reference guide |
| [Environment and HTTP/HTTPS transport](transport.md) | Current workflow and feature/reference guide |
| [Step 21 verification review](verification-review.md) | Current workflow and feature/reference guide |
| [Workspace UI and UX refresh](workspace-ui.md) | Current workflow and feature/reference guide |
| [TaskFlow architecture plan](../Documents/Architecture-Plan.md) | Original plan/assessment with current implementation note |
| [TaskFlow ordered delivery backlog](../Documents/Delivery-Backlog.md) | Original plan/assessment with current implementation note |
| [TaskFlow feature release roadmap](../Documents/Feature-Release-Roadmap.md) | Product sequencing and current next work |
| [TaskFlow product and SaaS gap assessment](../Documents/Product-Gap-Assessment.md) | Original plan/assessment with current implementation note |
| [TaskFlow](../README.md) | Setup, workflow or module entry point |
| [TaskFlow frontend](../TaskFlow-FE/README.md) | Setup, workflow or module entry point |
| [Published-app browser review](../tests/browser/README.md) | Setup, workflow or module entry point |
| [SaaS-031: server-owned rank and atomic board movement](saas/board-moves.md) | Current move API, migration and reconciliation contract |
| [SaaS-032: accessible project board UI](saas/board-ui.md) | Current board navigation, movement, accessibility and recovery guide |
| [My work and authorized search](saas/my-work.md) | Current workflow and API reference |
