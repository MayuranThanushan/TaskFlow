# Project/task revisions and conflict resolution

SaaS-015, 2 November 2026. Projects and tasks expose a decimal-string `version`. Scoped editing, status changes and soft deletion require the revision the caller actually loaded. A stale request returns 409 without committing a mutation or success audit. The editor keeps the draft and offers a fresh comparison before an explicit retry.

## Revision and transaction boundary

Migration `20261102150936_ResourceVersions` adds non-null BIGINT Version columns to Projects and Tasks with default 1. Existing rows, including deleted records, start at 1; no old revision history is fabricated. New records also start at 1. Every tracked Project/Task modification increments its persisted revision independently of UpdatedAtUtc, including ownership transfers and task assignment cleanup caused by role downgrade, suspension or grant revocation. A no-op does not advance the revision or append an audit event. Task creation/member management does not change the project's version unless project fields change; dashboard totals are not part of the project editor's revision.

EF marks both columns as concurrency tokens, so UPDATE predicates include the original Version. The SaveChanges boundary derives the increment from EF's original persisted value, never a client-written version. Project/task request bodies still reject Version, owner/parent/scope injection. Application mutation and business history remain in the same transaction. Rejected writes produce no success record; the existing bounded security rejection signal may record the 409. Exhausting signed BIGINT revisions rejects the write rather than wrapping.

The HTTP action filter only captures precondition input. The transaction boundary first obtains its existing company/membership/project locks and authorizes the requested operation. It then loads the scoped target and checks the revision before mutation, assignment validation or no-op acceptance. Foreign-company, deleted and inaccessible resources therefore remain 404; a visible Viewer lacking write permission remains 403 regardless of a missing/invalid/stale revision. No current record, revision or private snapshot is included in a conflict response. Fresh comparison uses a separate normal authorized GET.

Background/operator code must preserve the same resource contract when accepting stale commands. EF concurrency predicates protect competing tracked contexts; arbitrary SQL or ExecuteUpdate does not automatically advance Version. Operational repair/backfill must run in maintenance with no active clients, or explicitly update Version and satisfy transaction/audit policy. This release does not add a universal SQL trigger or durable command queue.

## Wire protocol

Read/create/update ProjectDto, ProjectDetailsDto, TaskDto and TaskStatusDto include `version` as a positive canonical decimal string. Never convert it to a JavaScript number or derive it from a timestamp. IDs and revisions can exceed Number.MAX_SAFE_INTEGER.

Send `X-Expected-Version: 7` with scoped project PUT/DELETE and task PATCH/status PATCH/DELETE. This is an application revision precondition, not an HTTP ETag/If-Match contract. It is checked on the specified route's resource, not a company-wide revision. Create operations, membership management and the dedicated password/invariant-protected governance workflows do not require this header.

| Result | Behavior |
| --- | --- |
| Current revision | Existing mutation rules apply; success returns the new version, or 204 for deletion |
| Missing header on scoped mutation | 428, code resource.version_required |
| Empty, repeated, noncanonical, negative, wildcard or out-of-range header | 400, code resource.invalid_version, after access checks |
| Stale revision | 409, code resource.conflict; draft may be compared with a separately authorized GET |
| Revision exhausted | 409, code resource.version_exhausted; operator intervention required |

Legacy v1 writes accept the header optionally during the already documented compatibility window. When supplied, it has the same validation and conflict behavior. An unversioned v1 caller retains its previous behavior; this is explicit cutover debt, not a claim that every legacy integration now prevents lost updates. The current frontend sends loaded revisions for edits/status/deletion in either mode. Lifecycle preview still closes legacy resource routes. Requiring revisions for every external integration belongs to the coordinated tenant/API cutover; do not enable unrestricted SaaS onboarding while this compatibility window remains open.

Success is never retried automatically with a fresh revision. The API client retains the existing bounded CSRF retry using the same revision and request body; network/server/conflict failures are not replayed. Unknown body fields remain rejected. OpenAPI describes the header, optional-v1/required-v2 behavior, version properties, error code and statuses without changing route inventory (39 paths / 55 operations).

## Editor experience

Project/task forms keep their original revision and draft across a conflict. Saving pauses, while the user can still edit or cancel. Compare latest version reads the current authorized record and shows the draft and saved values side by side. A denied/deleted/missing latest read leaves the draft intact and offers no rebase/reload result.

Keep my edits explicitly adopts the latest revision and copies only untouched fields from the latest record into the form. Edited fields remain the user's draft, even if that means intentionally choosing their value over a newer same-field edit. The user reviews the rebased form and saves separately. Discard draft and load latest explicitly replaces the draft. Another intervening edit can produce another conflict; neither choice performs an automatic write.

Task patches compare with the adopted baseline, so untouched newer priority/assignee/deadline values are not sent back as old values. An untouched microsecond deadline stays omitted even when the local datetime input displays only milliseconds. Status is changed through its separate action, also guarded by the task revision. Row status/detail actions expose a reload path after failure. A stale delete remains rejected until the user refreshes/reviews the latest record and confirms deletion again. Company/account navigation still unmounts private forms and ignores late results.

## Deployment and verification

Stop all old writers, take the existing verified backup/key-ring snapshot and apply the sixth migration with the migration identity. Verify both columns exist, old rows are Version=1, the six-entry migration history is correct and existing scope FKs/data counts are preserved. Deploy only the matching backend and compiled frontend, then perform readiness, auth, two-editor and cleanup smoke checks before reopening traffic. Do not overlap old writers that do not advance Version with this release. Maintain a shared persistent Data Protection key ring and the existing separate runtime/migration/backup/security-reader grants; a custom column-limited runtime grant must permit Version updates. The repository runtime template already permits table updates.

MySQL DDL can commit partially. Production recovery is maintenance plus forward repair or the established restore rehearsal, not running Down while traffic is live. Existing cached scoped clients without the header fail with 428 and must reload. All company/lifecycle flags remain default off. Local verification never migrates the user's running development/customer database.

Verification includes live MySQL same-revision competing editors, no-op and stale/no-audit checks, required/invalid inputs after authorization, independent EF-context concurrency, task status/deletion, cleanup revisions, high string revisions in the UI and a populated synthetic-backup migration rehearsal. Published HTTPS journeys use real two-tab project/task editors, explicit comparison/rebase, rejected stale deletion/status, desktop/mobile accessibility scans and the previous company/audit journeys. Default-off legacy browser journeys are rehearsed separately. See [evidence](../evidence/saas015-resource-versions.json) and [browser instructions](../../tests/browser/README.md). Hosted CI/CodeQL/container, selected-host performance/operations, human accessibility review and tenant cutover remain release gates.

Next: SaaS-016 tenant regression coverage, pilot quotas and privacy/retention controls.
