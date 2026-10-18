# TaskFlow ordered delivery backlog

Prepared: 18 October 2026. Steps 01 through 03 are complete: [decision register](../docs/decisions/README.md), [API amendment](../docs/contracts/api-baseline.md), [database amendment](../docs/contracts/database-baseline.md), and [toolchain matrix](../docs/toolchain.md). Exact package pins, NuGet lockfiles, EF tooling and provider compatibility checks are in place. The repository/scaffold foundation is complete, including a local main branch, formatting rules, template cleanup and a minimal frontend shell. Steps 04 through 23 remain pending. Live MySQL verification remains pending steps 05/06. No remote issues or deployments have been created.

Read [Architecture-Plan.md](Architecture-Plan.md) first. The sequence below refines the existing implementation plan into small, reviewable steps. Existing TF identifiers are retained as traceability references, not renumbered issues. Work from top to bottom; complete the relevant frontend, API, database, and tests together.

## Ordered tasks

| Step | Task and concrete output | Depends on | Completion evidence | Original backlog |
| --- | --- | --- | --- | --- |
| 01 | Record baseline decisions: cookie/CSRF/session lifecycle, roles, ownership, retention, deadlines, ID representation, PATCH semantics, text limits, progress DTO | — | Decision records and API/schema amendments resolve open choices in the architecture plan | TF-001 |
| 02 | Inspect installed tools; verify and pin compatible .NET, EF Core, MySQL provider/server, Node, React and build tooling | 01 | Version matrix, SDK pin, package locks, reproducible tooling instructions | TF-001, TF-002 |
| 03 | Create local repository foundation, ignore rules, four backend projects, frontend shell, README and example configuration | 02 | Backend builds; frontend type-checks/builds and starts; no secrets committed | TF-001 |
| 04 | Add API conventions: dependency injection, Problem Details, validation, trace logging, OpenAPI, health checks and local frontend proxy | 03 | A frontend-to-API health request works; malformed requests and a controlled failure return safe JSON | TF-001 |
| 05 | Add Identity and all five logical entities, EF mappings, explicit delete rules, indexes and initial migration | 04 | Clean MySQL database migrates successfully; uniqueness and foreign-key checks pass | TF-002 |
| 06 | Establish isolated MySQL integration fixtures, unit/frontend test runners and initial CI configuration | 05 | Build, meaningful constraint/API checks, frontend checks run reproducibly; production DB cannot be selected by test setup | TF-013 |
| 07 | Implement register/login/me/logout, CSRF bootstrap/validation, Identity policies, generic login errors, rate limiting, session invalidation | 06 | AUTH-001–008; CSRF failure, previous-cookie replay after logout, unauthenticated JSON 401, duplicate email tested | TF-003 |
| 08 | Build application shell, auth screens, protected navigation, central API client and session-expiry handling | 07 | Register → login → refresh → logout works in browser; field errors and failed login are usable | TF-008 |
| 09 | Prepare an early deployment configuration and run a smoke check in the selected environment | 08 | HTTPS, deep links, API routing, MySQL readiness, cookies/CSRF and persisted protection keys verified; actual publishing/provider costs handled when selected | TF-014 |
| 10 | Add reusable project-access policy and transactional activity-writing foundation | 07 | Unit/integration tests distinguish owner/member/outsider and rollback mutations if activity saving fails | TF-005, TF-012 |
| 11 | Deliver project creation, authorized listing, detail API and corresponding React forms/pages | 08, 10 | Owner derives from session; pagination is deterministic; second user cannot list/read private projects | TF-004, TF-009 |
| 12 | Deliver project editing and confirmed deletion, including retention/visibility rules | 11 | Owner can edit/delete; member cannot; deleted project/tasks disappear from reads and totals while internal history remains | TF-004, TF-009 |
| 13 | Deliver member listing, add-by-existing-user-ID and removal UI/API | 12 | Duplicate membership rejected; owner cannot be removed; outsider blocked; removed user loses access on next request | TF-005, TF-009 |
| 14 | Deliver task create/list/detail with priority, optional assignee/deadline, forms, and database-backed activity | 13 | TASK-001/002/004/008/009/010; default ToDo; eligible owner/member assignments; data persists after refresh | TF-006, TF-010 |
| 15 | Deliver task editing, presence-aware clearing, status updates and owner-only confirmed task deletion | 14 | Omitted versus null behavior verified; all valid statuses work; invalid values blocked; deleted tasks hidden; activity committed atomically | TF-006, TF-010 |
| 16 | Complete member-removal assignment cleanup and concurrent assignment protection | 15 | Removal unassigns tasks atomically; concurrent assignment/removal cannot leave an ineligible assignee; removed user cannot mutate tasks | TF-005, TF-006 |
| 17 | Deliver server-side task title search, combined filters, allowlisted sorting and pagination UI | 15 | Combined filters, ordering ties, null deadlines, semantic priority order, boundaries and access-scoped counts tested | TF-007, TF-010 |
| 18 | Deliver dashboard totals and project progress in API and UI; refresh affected views after mutations | 16, 17 | Empty project 0%; completed tasks excluded from overdue; UTC boundary cases, duplicate membership joins, deletion scope and counts verified | TF-011 |
| 19 | Deliver paginated project activity reader and UI; finish event coverage across mutation paths | 18 | ACT-001–004; newest-first stable ordering; actor/resource/timestamp correct; historical entries immutable through public API | TF-012 |
| 20 | Deliver current-user display-name editing and profile screen | 08 | Name validation and persistence tested; attempts to alter IDs/email/roles rejected | TF-003, TF-008 |
| 21 | Complete browser journeys, accessibility/responsive checks and focused security/performance review | 19, 20 | E2E-001–004 pass; keyboard/focus/errors usable; no sensitive logs; common operations measured against SRS target | TF-013 |
| 22 | Finalize release configuration, migration procedure, backup/recovery notes, health checks, production smoke verification | 09, 21 | Production build and migration rehearsal succeed; deployed core workflows pass after restart; required CI checks pass | TF-014 |
| 23 | Finish README, accepted architecture/ERD, OpenAPI, test commands, demo evidence and known limitations | 22 | Another developer can start from a clean checkout and reproduce setup/tests; all MVP acceptance criteria checked | TF-015 |

Step 09 depends on selecting a suitable environment. Local feature development can continue if hosting selection is pending; actual deployment verification remains required before release. Step 20 can be implemented after step 08, but the table keeps a clear single-task execution order. Do not create external repositories/issues or publish a site merely because they appear in this backlog.

## Milestones

1. **Foundation:** steps 01–06 — reproducible builds, schema, test infrastructure.
2. **Authenticated shell:** steps 07–10 — working account journey and permission/activity foundations.
3. **Project collaboration:** steps 11–13 — private projects and owner-managed membership.
4. **Task management:** steps 14–17 — complete task workflows and safe querying.
5. **Complete MVP:** steps 18–20 — dashboard, progress, history and profile.
6. **Release:** steps 21–23 — verified application, deployment and reviewer documentation.

## Per-task definition of done

- Behavior matches the SRS and accepted decisions; relevant API/schema documentation is updated.
- Server validates input and enforces resource permissions; errors follow the API contract.
- Required schema changes have reviewed migrations; data and activity commit consistently.
- Relevant automated checks pass, including failure paths appropriate to the change.
- UI handles loading, empty, validation, error and success states; the complete feature is manually verified.
- Changes are reviewed, documented and ready for a focused commit, without secrets or generated artifacts.

Tests, pagination conventions, errors and activity writes begin with their features. The final quality phase completes coverage and checks cross-feature behavior.

## Effort and immediate next task

The original phase estimates add up to **33–51 developer working days**, while the same document also says 5–8 weeks. These are not equivalent at a five-day workweek: 33–51 days is about 7–11 working weeks. Use the daily estimates only as a rough learning-project budget, then re-estimate after the authenticated shell is working; no calendar deadline is assumed.

**Steps 01 through 03 are complete. Next: step 04, API conventions, health checks and frontend development proxy.** The existing TaskFlow-FE/TaskFlow-BE layout is preserved. Live MySQL verification remains pending steps 05/06.
