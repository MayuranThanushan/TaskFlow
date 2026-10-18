# Documentation and demo handoff

Step 23, 18 October 2026. The implemented MVP is ready for a local developer/demo handoff. Public release acceptance remains conditional on the external gates below. No hosting target has been selected and no remote repository is configured.

## Developer entry points

1. Follow the [clean-checkout HTTPS setup](../README.md). Provision MySQL explicitly; ignored .local credentials and binaries are not transferable setup assets. Register your own synthetic accounts; there are no default credentials.
2. Read the [accepted architecture and ERD](architecture.md), [decision register](decisions/README.md) and [API snapshot/contracts](contracts/README.md). Markdown amendments resolve differences with the original Word documents.
3. Run [backend/frontend/operations checks](testing.md) with mandatory MySQL enabled. Use the isolated [browser suite](../tests/browser/README.md) only against a disposable environment. Backend safety checks refuse non-test database names; browser setup has additional explicit disposable-environment guards.
4. For a combined production artifact, use the [release runbook](release-runbook.md). Migrate explicitly before startup and verify both liveness and readiness. Use [backup/recovery](backup-recovery.md) before changing retained data.

## Demo walkthrough (15 minutes)

Use a fresh development database or an isolated disposable demo database. Create three synthetic accounts through registration: owner, member and outsider, each with an example.test email and a distinct private password meeting the displayed policy. Use separate browser profiles/private windows for simultaneous identities. Do not reuse credentials from evidence or production. Logout invalidates all sessions for that account.

| Order | Action | Expected result |
| --- | --- | --- |
| 1 | Register the owner, sign in and open the overview | Empty scoped totals; account ID available for collaboration |
| 2 | Register/sign in as member and copy their account ID | ID remains a string; display name can be edited in Profile |
| 3 | As owner, create a project named Demo launch | Project appears in list and detail; progress starts at 0% |
| 4 | Add the member by account ID in project members | Owner appears once; new Member can open the project |
| 5 | Create three tasks: Draft brief, Review brief, Publish brief | Tasks start ToDo; assign Review brief to member, set High priority and a past deadline |
| 6 | As member, edit Draft brief and move it to InProgress, then Done | Changes persist after reload; one of three active tasks is complete (33% project progress) |
| 7 | Search for brief; combine status/priority/assignee filters; change sort | Server-scoped list and counts match filters; clear filters to recover all tasks; pagination is visible with enough data |
| 8 | Refresh overview | Three tasks, one Done, matching status totals; unfinished past-due Review brief is overdue |
| 9 | Open project activity and change date bounds | Newest-first events show actor/action/time; empty range and reset work |
| 10 | As member, inspect project/task controls; as outsider, open the copied project/task URLs | Member has task editing but no owner deletion/membership controls; outsider receives inaccessible/not-found state without data |
| 11 | As owner, remove member after confirming | Member loses access; Review brief becomes unassigned; retained history remains |
| 12 | Confirm task deletion, then project deletion | Deleted task leaves active counts while its activity remains; deleted project leaves lists and direct links become inaccessible |
| 13 | Edit owner display name, reload, sign out and revisit a protected URL | Name persists; signed-out navigation returns to sign-in |

For short demos, use the existing automated pagination evidence rather than creating 21 tasks by hand. Also show one blank form submission and recovery after fixing it. The full browser suite exercises API denial, validation/retry and stale member-removal cases beyond this walkthrough. If reusing a database, register new synthetic accounts and label the project uniquely; do not reset shared data or delete retained history to make a demo look empty.

## Acceptance and traceability

Each row is checked against the original SRS and its accepted amendments. "Local pass" refers to implementation and recorded automated/manual verification, not customer sign-off or hosted deployment.

| SRS / acceptance area | Status and implementation evidence |
| --- | --- |
| FR-AUTH-001 through 003: register, login, logout | Local pass: [secure sessions](authentication.md), AuthTests and browser E2E-001; no automatic sign-in after registration; replay denied after logout |
| FR-AUTH-004: current profile | Local pass: [profile](profile.md), AuthTests.Profile and browser profile suite; only display name editable |
| FR-PROJ-001 through 005: project CRUD | Local pass: [projects](projects.md), ProjectApiTests and E2E-001/002; owner-only edits and confirmed soft deletion |
| FR-PROJ-006: membership | Local pass: [membership](project-membership.md), ProjectApiTests and E2E-004; owner-managed add/remove, no owner removal |
| FR-TASK-001 through 009: CRUD/status/priority/assignment/deadlines | Local pass: [task reads](tasks.md), [mutations](task-mutations.md), ProjectApiTests.Tasks/TaskMutations/AssignmentConcurrency; optional assignment and UTC deadline semantics |
| FR-TASK-010 through 012: search/filter/sort/page | Local pass: [task queries](task-queries.md), ProjectApiTests.TaskQueries; combined filters, tie ordering, nullable deadlines and large decimal IDs |
| FR-DASH-001 through 004: totals/progress/overdue/refresh | Local pass: [dashboard](dashboard.md), ProjectApiTests.Dashboard; persisted authorized scope, empty 0%, Done excluded from overdue |
| FR-ACT-001 through 003: events, details, reader | Local pass: [activity](activity.md), ProjectApiTests.Activity/ProjectFoundationTests and ACT-001 through 004; append-only, transactional, retained task history |
| FR-API-001 through 005: REST/validation/status/errors/docs | Local pass: [contracts](contracts/README.md), ApiFoundationTests and feature HTTP tests; OpenAPI snapshot plus explicit amendment contracts |
| Cross-user access, SRS section 6 | Local pass: ProjectAccessPolicyTests, feature HTTP tests and E2E-002/004; 404 inaccessible vs 403 visible forbidden |
| NFR-001 performance | Local demo measurement passes recorded target: [review](verification-review.md); single-process local sample, not production capacity validation |
| NFR-002 security | Focused local pass: CSRF/cookies, lockout/rate limiting, permission/race/logging checks and dependency review; independent penetration testing not performed |
| NFR-003 reliability; NFR-008 integrity | Local pass: transactions, concurrent member removal, FK/retention checks, migration mismatch readiness and [clean-release recovery evidence](evidence/step23-recovery.json) |
| NFR-004 usability; NFR-007 compatibility | Local Edge responsive/keyboard review passes at 320/390/768/1440; 30 axe scans, no detected violations/overflow; two incomplete decorative-arrow checks manually dispositioned. Other browsers require verification |
| NFR-005 maintainability; NFR-006 testability | Layered code, locked dependencies, decision records, isolated fixtures; 110 backend / 92 frontend / 16 operations checks pass locally |
| Setup, migrations and demonstrability, SRS section 12 | Setup commands, explicit migration tooling, demo steps and evidence supplied; published HTTPS workflows/restarts pass locally. Clean checkout requires external tool/database provisioning; Docker recipe not executed here |
| Release/CI acceptance, delivery steps 09/22 | Pending external gates: Docker, hosted CI and selected-host production verification |

E2E-001 (new account/project/task lifecycle), E2E-002 (cross-user isolation), E2E-003 (validation and recovery), and E2E-004 (member permissions) passed in the published-app browser suite. ACT-001 through 004 are covered by mutation/reader integration checks and browser history review. The SRS section 12 application criteria are locally demonstrated; final release approval must include the pending environment gates.

## Evidence supplied

- [Step 21 review](verification-review.md) and [machine-readable browser review](evidence/step21.json): published HTTPS app, real Edge journeys, responsive/accessibility checks, security review and modest local performance sample. Its historical backend count is 109; step 22 adds the unknown-migration readiness check for 110.
- [Step 22 operations review](release-runbook.md) and [original rehearsal](evidence/step22.json): migrations, permissions, backup/recovery and rollback. The original report records an in-progress candidate, not the final clean artifact.
- [Final clean-artifact recovery report](evidence/step23-recovery.json): source bab434a00993b7e1d83e09abe00001d945d0cae4, sourceDirty false. Repeated recovery passed all table business-data fingerprints, key hashes, cookie invalidation, least-privilege checks, compatible rollback and roll-forward. Stamp/timestamp rotation is deliberately excluded from business-data fingerprints. Local backup/restore timings are not an operational RTO guarantee.
- [OpenAPI snapshot](contracts/openapi.json): 17 paths, 24 operations exported from the Development host; no database startup was needed to generate it. Production keeps the document unavailable.

Detailed TRX/JUnit, coverage, browser screenshots, binaries and private backups remain ignored local artifacts; committed JSON is sanitized and contains no credentials/cookies/keys. Reproduce those artifacts with the documented suites. Evidence predates this documentation commit; it validates the named code artifacts rather than implying a new hosted release.

## Handoff verification

Machine-readable summary: docs/evidence/step23.json (relative to the repository root).

Step 23 repeated locked NuGet restore, EF tool restore and a full backend build in a separate git-archive checkout of bab434a with no ignored local configuration copied: zero warnings/errors. Frontend npm ci and production build passed in the same isolated checkout. This used the installed workstation tools/package caches; it was not a new-machine provisioning or Docker test. The HTTPS exporter produced repeatable normalized output with certificate verification enabled and rejected an intentionally missing route. All 125 local links in changed Markdown resolved; git diff --check passed. The existing backend TRX was rechecked for 110 passes/zero skips and all 16 operations checks were rerun successfully. No application code changed, so the 92-test frontend suite and full live backend suite retain their step 22 evidence rather than implying new executions.

## Limitations and remaining release gates

The MVP has no email verification/password recovery, email/password editing, invitation emails, ownership transfer, self-leave, global administrator UI, comments, attachments, notifications, Kanban, realtime collaboration or advanced reporting. Membership requires an existing account ID. Authentication rate limits are process-local and logout invalidates every device session. The current deployment model is a single API origin; multi-instance operation needs a reviewed shared deployment policy.

The following must be completed in the selected environment before public release:

1. Connect the repository and run all configured GitHub jobs, including mandatory live MySQL and deployment smoke. Local actionlint validation is not a hosted run.
2. Execute the Linux container build, explicit migration and recreate/restart smoke on a working Docker host. Local verification used a Windows published app and migration bundle.
3. Select hosting, provision DNS/TLS and private MySQL, pin reviewed release images, mount private runtime/migration secrets and persist/protect data-protection keys. Verify routing, cookie security, readiness, account isolation, workflows and restart on that host.
4. Apply reviewed least-privilege grants, schedule encrypted off-host backups and alerts, and rehearse recovery/rollback against the target platform. Proposed 24-hour RPO/60-minute RTO and retention are planning values, not achieved production SLAs.
5. Record the host/CI results and obtain stakeholder acceptance. Browser compatibility beyond reviewed Edge, production capacity and external security testing remain outside the recorded local evidence.

No further numbered implementation step is assumed. The next work is closing these environment-specific release gates when a target is available.
