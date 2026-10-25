# 0005: Commercial SaaS company boundaries

Status: adopted design baseline for SaaS-001. Date: 25 October 2026.
Implementation: not implemented. The current MVP continues under decisions 0001-0004 until the coordinated tenant cutover. This record supersedes their project-only permissions for the future v2 contract, not current runtime behavior.

## Context and outcome

TaskFlow is intended to be sold to multiple companies. Project membership alone cannot provide customer isolation, company administration or safe billing. Adopt the following contract before adding collaboration features.

## Decisions

1. A Workspace is one customer/company security boundary. A global Identity User can belong to multiple companies; removing one membership never disables unrelated memberships. WorkspaceMembership has Owner/Admin/Member/Guest role and Active/Suspended state. Project ownership remains authoritative in Projects.OwnerId; non-owner ProjectMembership has Editor/Viewer role. Guests receive only explicitly granted project access, including Editor where intended. All projects are private initially.
2. Retain the React/.NET/MySQL modular monolith. Use one shared schema with mandatory WorkspaceId on tenant data, tenant-first indexes and composite parent references. This choice requires adversarial isolation tests; it is not a claim of physical isolation. Dedicated databases/residency are later customer-driven options.
3. Combine active workspace membership with project permissions on every request. Company administration never implies private-project readership or permission to self-add. Adopt the [permission matrix](../saas/permissions.md). No platform operator receives company data access through ordinary user roles.
4. Use `/api/v2/workspaces/{workspaceId}/projects`, `/projects/{projectId}`, `/projects/{projectId}/members`, `/projects/{projectId}/tasks`, `/tasks/{taskId}`, `/dashboard` and `/activity` under that workspace prefix. Workspace administration uses `/api/v2/workspaces/{workspaceId}` and its `/members` collection. `/api/v2/workspaces` lists only the caller's active memberships; creation is controlled during pilot. Global session/CSRF/profile stay on existing v1 identity routes initially; do not maintain two authentication stacks. SaaS-003 defines DTOs and exact HTTP operations.
5. Missing authentication yields 401. Unknown, inactive or inaccessible workspace/resource yields 404, including mixed-company IDs. A forbidden operation on a visible resource yields 403. Validate access before version conflicts, exports or counts. Tenant context is mandatory and immutable per operation; never infer a default company for a tenant query.
6. Retire v1 project/task/dashboard/activity/member data endpoints at cutover with uniform 410 responses after authentication, without resource lookups. No unscoped compatibility adapter. Deploy UI and API together. Old browser resource links resolve only through authenticated, unambiguous server mapping; otherwise show a company-selection/not-found flow. Identity endpoints remain supported.
7. Use application-managed integer revision persisted in MySQL, decimal-string read versions and required `expectedVersion` on edits/deletions. A stale authorized mutation returns 409, preserving the frontend draft. SaaS-003/015 define creation and aggregate token details. Existing parent-row locking remains necessary for membership/assignment races.
8. Tenant reads/writes, aggregates, SQL bypasses, jobs, object keys, recipient selection, caches and exports share the boundary. EF filters alone are insufficient. Switching companies discards old reads/drafts; an authorized old write may still commit in its captured original company, never be redirected to the new company.
9. Separate project activity, company business audit, restricted security events and technical logs. Business mutation and audit commit together. Company audit redacts private-project content unless project read access exists. Normal runtime audit permissions must exclude UPDATE/DELETE without broader schema grants overriding them. Retention deletion uses a separate governed identity. Local rotating logs are not tamper-proof archives.
10. Adopt the [migration and cutover plan](../saas/migration-cutover.md) and [threat-model acceptance cases](../saas/tenant-threat-model.md). No production data migration is authorized by this design record alone; implementation and recovery rehearsal belong to SaaS-008 onward.

## Commercial release boundary

R1 is an operator-onboarded private pilot with atomic server quotas, not public self-service. Verified identity/recovery and durable invitations arrive in R2. Do not charge until R6 entitlement, billing reconciliation and offboarding gates pass. Hosting, prices, numerical quotas, retention durations and service commitments remain unselected; do not invent customer promises. Customer contracts/privacy terms and independent security review require appropriate owners before a commercial launch.

## Tradeoffs and completion

Explicit tenant routes and a coordinated maintenance window cost migration effort but eliminate ambiguous legacy access. Global identity simplifies multi-company use but demands membership checks independent of login. Private projects limit company-admin visibility deliberately; orphan recovery is restricted rather than an implicit admin bypass.

SaaS-001 is complete as a design task: definitions, permissions, API strategy, legacy mapping and negative cases are recorded. None of the future isolation cases is represented as passing. SaaS-002 establishes delivery evidence; SaaS-003 makes the contract executable before SaaS-008 changes the schema.
