# Proposed multi-company SaaS architecture

Status: proposal for the post-MVP roadmap, 25 October 2026. User-confirmed audience: multiple companies using TaskFlow. This does not replace the [accepted MVP architecture](architecture.md) until corresponding decisions/migrations/features are implemented. See the [gap assessment](../Documents/Product-Gap-Assessment.md) and [release tasks](../Documents/Feature-Release-Roadmap.md).

## 1. Keep a modular monolith

Retain React, ASP.NET Core, Identity and MySQL. Add explicit Workspaces/Identity, Projects/Tasks, Audit, Collaboration/Notifications, and Billing/Entitlements module boundaries. Introduce a separately hosted .NET worker when durable side effects arrive; it can share the source/DB while using separate credentials and runtime limits. A broker, Redis, Elasticsearch, microservices and Kubernetes are not prerequisites for the first SaaS pilot.

Proposed runtime: browser -> single-origin API -> scoped application modules -> MySQL; the mutation transaction also appends business audit and an outbox row. The worker claims committed outbox records -> creates inbox/delivery records -> sends through an email adapter. Private object storage/scanning comes with attachments. Provider-specific integrations stay behind adapters.

A shared database/schema with tenant-keyed rows is the proposed pilot strategy. It reduces initial operating complexity but makes tenant enforcement a first-class invariant. Customer-specific databases or regional deployments are later options driven by actual requirements, not an assumption that a shared schema meets every isolation/residency need. This follows the distinction between identity and tenant-scoped authorization in [Microsoft's multitenancy guidance](https://learn.microsoft.com/en-us/azure/architecture/guide/multitenant/considerations/identity).

## 2. Company and identity boundaries

One Workspace represents one company/customer. Accounts remain global so one person can belong to several companies without duplicate credentials. WorkspaceMembership grants entry into a company; ProjectMembership grants access to a particular private project. Joining a company does not automatically grant all its projects.

Proposed workspace roles: Owner, Admin, Member and Guest. Proposed project roles: Owner, Editor and Viewer; existing Member maps to Editor. Guest is a company-level restriction, not a shortcut around project permissions. Roles map to named server capabilities, not UI-only checks. A platform support operator is a separate privilege boundary and is never synthesized from ordinary project/workspace membership.

| Action | Default policy |
| --- | --- |
| Manage company settings and membership | Workspace Owner/Admin; sensitive changes require reauthentication/MFA according to policy |
| Manage billing/close company/transfer last ownership | Owner or explicit billing capability; last active owner protected |
| Read private project/task/comment/file | Active workspace membership AND explicit project read access |
| Edit tasks/comment/change board status | Project Owner/Editor plus active workspace membership |
| Edit project/manage project members/delete project or task | Project Owner, unless an explicit additional capability is approved |
| Review company security/membership audit | Restricted workspace audit capability; redact private-project content where the reader lacks project access |
| Review task/project history | Project read access; private titles/bodies remain protected |
| Support access to company content | Explicit, time-bounded, reasoned break-glass procedure with audit; no ordinary operator global-read UI |

Admins are not silently granted private project content. Governance transfer or emergency custody requires a distinct audited policy; it must not become an undocumented self-grant bypass. Initially require project-owner handover for normal departure, preserve suspended owners' retained records and define an explicit recovery path for orphaned projects. Suspending a user must block company access immediately even if handover is incomplete. Removing someone from one company must not disable their unrelated company memberships or global account.

## 3. Tenant context on every data path

Prefer explicit workspace-scoped resource routes for the new contract, for example /api/v2/workspaces/{workspaceId}/projects and /api/v2/workspaces/{workspaceId}/tasks/{taskId}. Identity bootstrap/profile can remain global. The version/path is a proposed cutover contract; choose it in SaaS-001 before implementation. Do not merely add an optional tenant header to existing globally scoped queries.

The API resolves and authorizes workspace membership on each relevant operation, then passes an immutable WorkspaceContext to services. A route/header/cookie selection alone is not trusted authorization. Every task/project lookup and mutation includes workspace scope. Inaccessible resources stay 404; visible forbidden actions stay 403. Check access before returning stale-version details so conflicts cannot become an existence oracle.

Add WorkspaceId to tenant-owned roots and child rows used by queries/workers/exporters. Use tenant-first indexes and composite foreign keys/uniqueness where appropriate: a task's project and audit target cannot point into another company. Global Users is the deliberate exception; a global user FK alone never proves company/project assignment eligibility. Keep active membership/assignee checks and existing concurrency locks.

EF query filters are useful defense in depth, not the entire security design. Review raw SQL, IgnoreQueryFilters, background scopes, aggregates, exports and support tools explicitly. A missing tenant context fails closed; no default workspace fall-through or unscoped administrator repository. Request services stay scoped; never cache one user's active company in a static/singleton variable.

Frontend resource/cache keys become userId + workspaceId + query. Company switching aborts reads, clears company drafts/selections and discards late responses. An already-authorized in-flight write may still commit in its original company; its completion must not populate or navigate the new company. Capture context before transport and do not retarget an old mutation to the newly selected workspace. Changing companies need not log the global account out.

The same isolation applies to notification recipients, outbox payloads, worker leases, object keys, saved views, download grants, billing customer mappings and exported artifacts. Do not use a company ID alone as an authorization secret. Avoid unbounded tenant-ID labels in metrics; use restricted traces/logs for specific-company investigations.

## 4. Data model additions

These are proposed logical modules, not existing tables or an approved physical migration:

| Stage | Additions | Important constraints |
| --- | --- | --- |
| Company foundation | Workspaces, WorkspaceMemberships; WorkspaceId on existing tenant data; Project/Task Revision | Unique workspace/user membership; explicit active/suspended/guest state; tenant-aware parent references; revision checked atomically |
| Audit | Versioned AuditEvents or an additive audited-event upgrade plus SecurityEvents | Company/actor/subject/entity/correlation/outcome/time; append-only normal writes; intentional handling for global sign-in events |
| Async delivery | OutboxMessages, DeliveryAttempts, consumer deduplication/leases | Stable event and recipient keys; available/leased/completed/dead-letter states; bounded retries |
| Invites | Invitations and token/delivery metadata | Company + normalized email + scope rules; hashed one-use token; expiry/revocation; roles cannot exceed inviter capability |
| Collaboration | Notifications, NotificationPreferences, TaskSubscriptions, Comments/CommentRevisions | Company/project/recipient scope; unique event/recipient key; immutable attribution and defined comment removal policy |
| Board/planning | Board positions/rank, SavedViews, Labels, TaskLabels, checklist/subtask/dependency/milestone records | Canonical task records; deterministic ordering; acyclic parent/dependency rules; tenant consistency |
| Files | Attachments, UploadSessions, scan/lifecycle records | Random object keys; quarantined until accepted; storage quota and authorized short-lived downloads |
| Commercial | WorkspaceSubscription, EntitlementVersion, SeatUsage, BillingEvents | Provider customer belongs to one company; signature verification/dedup; server-side feature enforcement |

Keep decimal-string IDs at browser boundaries and UTC timestamps. Use existing Identity components/token providers rather than inventing token cryptography. Evaluate managed federation when enterprise identity is needed; no identity-provider switch is required merely to start the company model.

## 5. Legacy migration and rollout

1. Inventory existing owners/projects/members and record a deterministic mapping. Default prototype: create a legacy/personal workspace per distinct project owner and put that owner's projects in it. This preserves private ownership; it does not invent a verified legal company.
2. Add corresponding workspace memberships for existing collaborators without granting any additional project access. Treat existing cross-owner collaborators as explicitly scoped legacy guests unless migration policy chooses otherwise. Reconcile resulting capabilities against the old policy; do not silently downgrade current task editing or broaden access.
3. Add nullable tenant fields, backfill with a recorded mapping, validate every parent/child relationship and reject unresolved rows. Preserve existing account/project/task/activity IDs.
4. Add constraints/indexes, then require tenant context. Switch the frontend and every backend entry point together. Legacy URLs can redirect only after an authorized, unambiguous mapping; old endpoints must not remain an unscoped bypass.
5. Rehearse on a restored disposable snapshot: compare authorized record sets, counts, activity and member/task assignment invariants before/after. New-company creation is an explicit action; group projects into real companies only through an approved transfer flow.
6. Current readiness requires exact compiled/applied migration-set equality. Therefore a new migration can deliberately make the old binary not-ready. Plan a coordinated migration/restart window for the pilot; do not promise rolling zero-downtime or blind binary rollback until a compatible-schema strategy is designed and tested. Add/backfill/contract phases do not automatically override this existing safety check.
7. Keep a tested recovery snapshot and forward-fix plan. Schema rollback may require recovery rather than replaying an old binary. Do not destroy retained history to simplify the backfill.

## 6. Audit has three separate audiences

Project activity is a collaboration feed. Company audit explains accountable business/admin changes. Operational logs describe technical behavior. Security events cover authentication, authorization/admin changes and abuse signals. They can correlate through event/trace identifiers without exposing the same payload to every audience.

Proposed company audit envelope: eventId, schemaVersion, workspaceId, actorType (user/system/integration), nullable actorUserId, subjectUserId when relevant, entityType/entityId, action, outcome, occurredAtUtc, correlation/trace ID and an allowlisted change summary. Preserve identity references after removal. A display-name snapshot is optional PII with policy; current UI member names must not be misrepresented as immutable historical identity.

Status/priority/assignee/deadline deltas can be structured. For free-text descriptions/comments, initially record which fields changed instead of copying all sensitive content into SIEM/audit records. Project titles or other snapshots require the same content authorization as the project itself. Never record invitation/reset tokens, passwords, MFA secrets, cookies, auth headers or arbitrary request/exception payloads.

Failed sign-in may have no authenticated actor and no selected company. Keep global authentication/security events in a restricted scope rather than copying a person's sign-ins into every company they belong to. Company event authorization and redaction are explicit. A successful authentication event means the server accepted credentials; it does not prove a browser received its cookie.

Mutation success audit and local outbox state commit with business data. Security attempts that fail before a mutation have their own outcome event. Deleted-project events must remain reviewable by an authorized audit path while ordinary project/task routes remain inaccessible. A privileged audit query may deliberately bypass soft-delete filtering, but must always retain tenant scope and redaction.

Application append-only guards and local file rotation are not tamper-proof storage. Restrict normal database privileges to prevent audit update/delete; the current schema-wide UPDATE grant must be replaced with table-specific grants, not an ineffective table-level deny layered beneath a broader grant. Governed purge uses a separate explicit retention process. External protected archives, collector health and integrity evidence are required before claiming tamper resistance. Retention/export/deletion periods are product/customer decisions, not copied from another vendor. Guidance: [OWASP security logging](https://cheatsheetseries.owasp.org/cheatsheets/Logging_Cheat_Sheet.html).

## 7. Durable invitations and notifications

Implement a transactional outbox when side effects begin. An outgoing domain event is not a parsed activity-summary string. Persist event intent with the mutation, then let a worker create recipient inbox rows and delivery attempts. Use leases, retry/backoff, dead-letter visibility and idempotent consumer keys. Multiple workers must not generate duplicate inbox rows; external mail can still be duplicated after ambiguous provider timeouts, so use provider idempotency/message keys where available and do not promise exactly-once delivery. See [AWS outbox guidance](https://docs.aws.amazon.com/prescriptive-guidance/latest/cloud-design-patterns/transactional-outbox.html).

Invitation states are Pending, Accepted, Revoked and Expired; resend invalidates/rotates the old token under a defined policy. Store only token hashes as invitation credentials. Prefer ID-only outbox messages and generate authorized mail content at dispatch. If a retryable delivery payload must contain a bearer link, protect it separately at rest, restrict worker access and expire/scrub it; never leave reset/invitation URLs in ordinary logs or long-lived plaintext queue records. Bind acceptance to the intended verified email/account and current company/project/role permissions. Validate inviter authority at creation and acceptance according to policy. Recheck expiry, revocation, capacity and membership in one transaction; token consumption cannot create duplicate membership. The invite landing page must not leak private company/project content to an unauthenticated holder.

Use generic password-recovery responses, expiring bounded tokens and safe reset/revocation semantics following [OWASP recovery guidance](https://cheatsheetseries.owasp.org/cheatsheets/Forgot_Password_Cheat_Sheet.html). Public self-service invitations depend on email proof/recovery, not just on sending a URL.

Notification eligibility is evaluated at event creation/materialization and again before send/read. Removal or suspension suppresses queued private content and invalidates deep-link access; already-sent email cannot be recalled. Default to assignments, mentions and meaningful subscribed-task updates. Suppress self-notifications; support read/unread, digest frequency and channel/event preferences. Reminders carry the expected task/deadline version so reschedule/completion/deletion makes old jobs obsolete. Start with inbox polling/refetch; add SSE/SignalR only after measured need and reauthorize subscriptions on membership changes.

Use separate runtime/migration/worker/retention privileges and update deploy/recovery fixtures when new tables appear. Worker errors and backlog depth need alerts, not silent fire-and-forget handlers.

## 8. Conflict-safe editing and Kanban

Use app-managed Project/Task revisions supported by MySQL. For the proposed initial contract, writes carry expectedVersion and stale writes return 409 with a safe reload/compare path; reads expose version as a string. This is deliberately simpler than claiming a strong ETag for a project detail DTO that also includes changing task aggregates. If If-Match/ETags are chosen instead, define representation versions and 412/428 behavior coherently. Keep Identity's existing concurrency handling and project membership locks. See [EF Core concurrency guidance](https://learn.microsoft.com/en-us/ef/core/saving/concurrency).

Kanban is another view of the same tasks, initially the existing ToDo/InProgress/Done states. Board queries need per-column counts and bounded paging/cursors, not the first 100 table rows. A server move command validates company/project/task/version and neighbor IDs, then atomically updates status/rank/audit/outbox. Bound rank rebalance work and reconcile stale optimistic UI moves. List, board, dashboard and notifications must converge on committed state.

Provide click/tap Move to controls and keyboard movement with focus/live announcements alongside dragging, per [W3C dragging guidance](https://www.w3.org/WAI/WCAG22/Understanding/dragging-movements). Manual status selection must remain available. WIP indicators start advisory. Custom workflow/transition engines are a later product decision, not necessary to ship a useful board.

## 9. Operating and product boundaries

Pilot quotas apply before billing: members/invites/projects/tasks/storage/events and outbound mail budgets. Enforce them server-side with atomic checks. Later billing maps entitlements to a company, not to a browser flag; signed webhook events are deduplicated and reconciled. A failed payment should not silently delete retained business data.

Select measured service objectives and alert owners for latency/error rate, unavailable DB, oldest outbox age, failed delivery, security-event collector lag, quota breaches and backup/recovery status. Do not equate local demo timings with production capacity. Performance review should measure project-lock contention, tenant-specific indexes, substring search and large offset paging before introducing caches/search infrastructure. Multi-instance operation also requires shared keys, coordinated limits/jobs and an explicit forwarding policy.

The current modern UI remains the starting point. Add real company switching, team/settings, invitations, inbox and board/list views incrementally using shared components. Replace the current 100-project activity selector with server search/paging. Add company audit routes/permissions; keep existing current-page export accurately scoped. Preserve field errors, duplicate-write guards, cancellation behavior and identity isolation.

Attachments require private storage, validation/scanning/quarantine, safe filenames, scoped upload/download grants, quotas and cleanup/recovery behavior before release; see [OWASP upload guidance](https://cheatsheetseries.owasp.org/cheatsheets/File_Upload_Cheat_Sheet.html). SSO/SCIM, residency, advanced reports, automation and AI features stay behind customer need and trustworthy permissions/audit. No provider, price, certification, legal retention period or production SLA is assumed here.
