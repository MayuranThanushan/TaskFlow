# Company audit console and bounded exports

SaaS-014, 1 November 2026. The company audit screen now offers intersected actor/action/entity/date/outcome filters, retained history for deleted projects, URL-preserved paging and a bounded JSON export. No schema migration or dependency is added; the five existing migrations, including StructuredAudit, remain required. All three workspace backend flags and the existing frontend company/lifecycle flags remain default off.

## Access and history

Only an active company Owner/Admin can open the console. Governance returns the same stable IDs and safe metadata as the existing governance reader. It never reveals a private project's title, content or deletion state. Project activity additionally requires current project ownership or an unrevoked grant in the same company. Company administration grants no override. The two sources are selected explicitly; IDs are unique within a source, not across both histories. Existing project/activity and basic governance endpoints remain compatible.

Deleted projects retain their audit events. Deletion alone does not revoke a project grant. A former collaborator whose grant is revoked cannot read that history, and suspended/removed company members cannot use the console or download. Deleted tasks remain discoverable by TaskDeleted action or Task subject filters within a permitted project. This release supplies review only: no restore, undelete, purge or public security-event access.

The project history query bypasses EF soft-delete filters deliberately. Every company, project, owner-affiliation, task-parent and unrevoked-grant predicate is explicit, including subqueries where IgnoreQueryFilters can propagate. No raw Details, project/task title, description, email, password, header or cookie is selected into the export DTO. Safe versioned metadata follows the SaaS-013 allowlist. Legacy rows remain LegacyUnknown without invented subjects/deltas; subject filters match only known version 1 metadata. Unknown versions remain withheld.

## Filters and pagination

`GET /api/v2/workspaces/{workspaceId}/audit/console` accepts:

| Parameter | Meaning |
| --- | --- |
| source | Governance (default) or Projects |
| actorId | Exact positive signed-BIGINT account ID, transmitted as a string |
| action | Exact canonical action name, at most 80 ASCII letters |
| entityType | Project, Task, ProjectMembership, Workspace or WorkspaceMembership |
| entityId | Exact subject ID; membership subjects use parent:account |
| fromUtc / toUtc | Inclusive ISO timestamps with Z or explicit offset; ordered UTC years 1000?9999 |
| outcome | Succeeded or LegacyUnknown; omit for all supported/unknown metadata |
| resourceState | All (default), Active or Deleted projects; only All for Governance |
| page / pageSize | Defaults 1/20, size 1?100, offset at most 10,000 |

Filters combine with AND. Unsupported/repeated parameters and invalid bounds return validation Problem Details. Times are rounded inward to database microsecond precision. Order is CreatedAtUtc descending, then event ID descending. Counts and page results use a repeatable-read request snapshot; browsing across requests is not an immutable snapshot. Narrow filters rather than scanning arbitrarily deep offsets. Action/entity matching follows database string comparison rules; use canonical names. Pagination preserves applied filters and browser history restores the filter form. Draft edits are separate from applied filters.

## Export protocol

1. `POST .../audit/console/exports` with `{ "filters": { "source": "Projects", "resourceState": "Deleted" } }` verifies current access and validates filters. It captures the largest matching event ID and returns an authenticated-encrypted Data Protection ticket, five-minute expiry and maximumRecords 1000. Page fields do not increase the export bound.
2. `POST .../audit/console/exports/download` with `{ "ticket": "..." }` verifies integrity, expiry, actor and route company. It rechecks active company role and every current project grant in a new repeatable-read snapshot, then regenerates matching rows with IDs at or below the captured watermark.
3. The JSON response contains generatedAtUtc, items, maximumRecords and an explicit truncated flag. The browser downloads taskflow-audit.json. The newest 1,000 records are included; if truncated, narrow the date range. Decimal-string IDs retain full precision. JSON avoids spreadsheet formula execution associated with CSV exports.

Both POSTs require the existing session and paired CSRF token. They share the per-account governance limiter (10 requests/minute/process, no queue); a normal export takes two requests. Responses are no-store. Tickets stay in request bodies, never URLs, logs, local storage or filenames. Tickets grant no authority and can be replayed only until expiry; every download repeats authorization. Invalid/foreign/expired tickets return 404. Current permission denial returns 403 or membership-hidden 404. In-flight authorized snapshots follow existing read semantics; revocation is applied on subsequent reads.

This is a ready-on-request, bounded synchronous export protocol, not a durable/background queue. No audit file, job record or customer data is cached server-side. Current authorization can reduce the export after ticket creation. The watermark excludes newly allocated higher-ID events; it is not a frozen content snapshot, a cryptographic archive or a promise about commit order of concurrent lower-ID transactions. There is no delayed delivery, resumable download or jobs dashboard. Durable workers/outbox remain SaaS-017; larger exports should use that foundation, encrypted storage, quotas and retention rather than increasing these bounds.

Use the existing persistent shared Data Protection key ring for replicas; otherwise an instance cannot read another instance's tickets. Existing fleet/selected-host key, rate-limit, logging, recovery and privacy requirements still apply. Security-event export remains an operations-only reader and has no company endpoint.

## Verification and release

Backend MySQL tests exercise intersected filters, legacy outcomes, deleted history privacy, cross-company/non-admin denial, revoked grants, suspension, ticket tampering/expiry, string IDs, higher-ID exclusion, the 1,000-record bound and strict query/body/CSRF validation. Frontend tests check preserved filters during paging, applied-vs-draft export scope, truncation feedback and denied downloads without files. A published HTTPS journey exercises the actual filtered table, expanded deltas, downloaded JSON and desktop/mobile accessibility checks.

Run the existing full backend, frontend and contract checks. In a disposable preview instance run companies.cjs, lifecycle.cjs, structured-audit.cjs and then audit-console.cjs using the same synthetic fixture. See [browser setup](../../tests/browser/README.md) and [release evidence](../evidence/saas014-audit-console.json). Hosted preview CI, selected-host workload measurements, human accessibility review and coordinated tenant cutover remain release gates; no live database or service is changed by local verification.

Next: SaaS-015 expected-version conflicts and draft-preserving conflict resolution.
