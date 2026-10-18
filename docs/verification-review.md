# Step 21 verification review

The published Production app passed local Edge browser journeys, accessibility/responsive checks and the focused security/performance review on 18 October 2026. The repeatable [browser suite](../tests/browser/README.md) uses locked Playwright Core 1.61.1 and axe-core 4.14.0, an isolated database and synthetic users. These tools remain outside the application dependencies.

## Acceptance journeys

| Test Plan case | Executed evidence |
| --- | --- |
| E2E-001 | One newly registered UI account creates a project and task, changes status to Completed, reloads, verifies dashboard project/task/completed totals and signs out. The broader journey also exercises editing, assignment, deletion, progress and history. |
| E2E-002 | A different account receives generic unavailable-project views for known project/activity URLs. Its direct task PATCH returns 404; the owner's task DTO and activity count remain unchanged. |
| E2E-003 | Blank forms focus the invalid field and associate an error; corrected forms save. A single intercepted read failure shows a safe error and recovers through explicit retry. No failed write is automatically replayed. |
| E2E-004 | Owner adds an existing member; member can read/create/edit/update status but cannot perform owner-only operations. Removal clears assignments and revokes next-request access. Stale assignment drafts cannot restore removed membership. |

Registration/login/return URL/reload/logout, secure HttpOnly cookies, cross-tab account switching, large decimal-string IDs, filtering/sorting/paging/history, activity bounds and retained deleted-task history, and profile persistence also pass. The test runner respects real authentication Retry-After limits. Browser-only fault injection affects one read request and does not alter the deployed server.

## Accessibility and responsive review

Thirty axe scans cover home, login/registration and errors, dashboard, project list/create/edit/detail/delete confirmation, task list/create/edit/detail, activity, profile and safe API-error recovery. Widths include 320, 390, 768 and 1440 CSS pixels. No violations or horizontal overflow were detected in the final scans. Local desktop/mobile screenshots were inspected, including narrow login and task views.

Fixes from this review:

- Darkened muted/footer/help text to meet text contrast checks; strengthened input/select/textarea and outlined-button borders. Measured form boundaries exceed 3:1 against both their interior and surrounding backgrounds (minimum 3.24:1 in the full journey run).
- Removed the brand link's conflicting explicit accessible name; its visible name now supplies the accessible name.
- Made the sign-in/registration form title the primary h1 so it remains available when the decorative story is hidden on mobile.
- Increased hint spacing below form inputs.

Keyboard checks cover the visible skip link reaching main, route focus, validation focus/error associations, visible input focus, safe initial delete-confirmation focus, Escape cancellation/trigger restoration, and explicit error recovery. Confirmations are inline sections rather than modal dialogs. Axe marks the decorative, aria-hidden home-link arrow for manual contrast review at two widths because it contains only non-text characters. It conveys no information beyond the visible link text; both instances were reviewed and retained. These focused checks supplement automated tests; they are not a formal accessibility certification.

## Security review

The API continues to derive account/project access from validated sessions and current database membership, apply active-parent/task scopes, allowlist request properties and sorts, parameterize EF queries/parent locks, protect unsafe requests with antiforgery validation, and keep passwords in Identity. Session tickets remain Secure/HttpOnly/SameSite=Lax with fixed expiry, no sliding renewal and per-request security-stamp validation. Business/profile responses are no-store. Existing tests cover denied mutations, transactional rollback, logout replay, lockout, expiry and concurrent membership/assignment changes.

One client issue was fixed: a draft waiting for CSRF acquisition could be dispatched after a detected session/account change. The client now checks the identity generation immediately before transport dispatch. A new test proves that the stale draft sends zero writes. This protects the dispatch boundary after the client detects a change; already-sent requests still use normal server authentication and authorization.

The browser verifies stored HTML remains text, missing-CSRF rejection, real cross-origin response access denial, CSP script/frame restrictions, nosniff and unavailable Production OpenAPI. Routine request logs use route templates, status and duration, excluding IDs/query strings/headers/bodies. A new backend test also rejects sensitive exception/inner-exception messages in logs, while retaining exception type and trace. Body/query/header canaries were absent from the published host's logs. JSON console settings do not enable HTTP payload or EF sensitive-data logging.

Frontend production dependency audit and backend vulnerable-package checks (including transitives) reported no vulnerabilities against their configured registries on this review date. This is a focused source/configuration and behavior review, not a specialist penetration test. Localhost is excluded by framework HSTS defaults; actual-host HSTS/TLS/proxy behavior remains a release verification gate.

## Performance

SRS NFR-001 targets common API operations within two seconds under normal demonstration conditions, excluding network delay and cold starts. The full journey dataset exposes 25 projects and 122 active tasks to the owner, including a dedicated 100-task fixture. Each of 11 operations has three warmups and 20 measured requests, sequentially at concurrency one. Timings include browser evaluation, local HTTPS round trip, response consumption and JSON parsing, making them conservative relative to server execution alone. No other benchmark or load capacity is claimed.

| Operation | p50 ms | p95 ms | Maximum ms |
| --- | ---: | ---: | ---: |
| Current profile | 10.35 | 13.23 | 14.07 |
| Project list | 18.06 | 19.35 | 19.50 |
| Project detail | 12.21 | 15.28 | 15.45 |
| Members | 14.29 | 16.50 | 16.66 |
| Task filter/sort/page | 20.33 | 23.88 | 24.37 |
| Task detail | 12.39 | 13.16 | 14.83 |
| Dashboard | 14.42 | 16.24 | 16.38 |
| Activity | 16.05 | 18.51 | 19.17 |
| Task status update | 21.98 | 23.93 | 25.77 |
| Project edit | 18.53 | 22.27 | 22.47 |
| Profile edit | 16.74 | 18.92 | 20.35 |

All 220 warm samples in this full-suite run are below 2,000 ms (maximum 25.77 ms). An additional focused run verifies the full same-account new-user workflow and isolated outsider setup on the final build; its 30 scans and all timing samples also pass. No cache or query-index change was justified. Re-run in the selected hosting environment; local measurements do not establish production capacity. The sanitized [evidence snapshot](evidence/step21.json) records browser, fixture and timing details; ignored TestResults/browser holds raw reports/screenshots.

## Verification and remaining delivery

All 109 backend tests passed with real MySQL and zero skips (35 non-database and 74 live), and all 92 frontend tests passed. Release/production publishing, type checking and ESLint pass; EF reports no pending model change. The published HTTPS host also passed clean migration/idempotence, readiness, cookie/CSRF, persisted-key restart and logout-replay checks. Disposable verification databases were removed and services stopped.

Hosted CI/container execution and selected-host checks remain unexecuted. The committed browser suite is runnable against a prepared disposable host; this step does not claim a hosted browser CI run or publication. Step 22 [release/recovery preparation](release-runbook.md) is complete locally; next is step 23 documentation/demo handoff. External release gates remain pending.
