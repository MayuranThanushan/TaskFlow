# Testing and continuous integration

The suite now contains 110 backend tests (75 requiring MySQL) and 92 frontend API/component checks. Local Release verification passed all backend tests with no skips, plus frontend type-check, lint, tests and production build. Deployment checks and the separate published-app smoke rehearsal are described in [deployment.md](deployment.md). GitHub execution has not been performed: no remote repository is configured.

## Shared MySQL fixture

PersistenceTests belongs to the MySQL integration collection. Its IAsyncLifetime fixture validates the connection before access, applies migrations once per collection, verifies the MySQL 8.4 server line, and creates a fresh DbContext for each test. Collection execution is serialized. Each persistence mutation test owns a transaction and rolls it back. Authentication HTTP tests span request scopes and instead remove only the synthetic accounts created by their own host. Project-foundation transaction tests commit across independent contexts, then remove only their own synthetic projects, memberships, tasks, activity and accounts in FK order. The fixture does not delete databases or wipe shared tables. Database/container startup and shutdown belong to the runner.

Only `taskflow_test` or a name beginning `taskflow_test_` is accepted. A non-root test account is required. Safety tests exercise rejection of development/production database names and privileged/missing accounts before a connection is attempted. These guards reduce accidental misuse; always supply an isolated test database and restricted credentials.

Local mode (no TASKFLOW_TEST_CONNECTION): 35 checks run and 75 live tests are explicitly skipped. Mandatory mode (`TASKFLOW_REQUIRE_MYSQL=true`): a missing connection fails the fixture instead of skipping. An unreachable/invalid database fails setup in either mode when a connection was supplied. The missing-connection mandatory failure path has been verified locally.

With a dedicated connection already configured:

```powershell
$env:TASKFLOW_REQUIRE_MYSQL = 'true'
dotnet build TaskFlow.slnx --configuration Release --no-restore
dotnet test TaskFlow.slnx --configuration Release --no-build --no-restore --logger 'trx;LogFileName=backend.trx' --results-directory TestResults/backend --collect:'XPlat Code Coverage'
python scripts/verify_test_results.py TestResults/backend/backend.trx
```

The last command additionally rejects empty, failed or skipped reports. CI uses python3; this Windows workspace uses python. Coverage is backend Cobertura output from the existing Coverlet collector; no arbitrary coverage threshold is imposed. Reports are ignored by Git.

The existing portable MySQL setup and credentials are documented in [database-setup.md](database-setup.md). Its process is stopped after verification. Do not print the generated local connections file.

## Disposable Docker alternative

`compose.test.yaml` is separate from the development database. With Docker available, copy `.env.test.example` to `.env.test`, set two different test-only passwords, then run:

```powershell
docker compose --env-file .env.test -f compose.test.yaml up -d --wait
```

This creates taskflow_test and a restricted taskflow_test account on 127.0.0.1:3307. Set TASKFLOW_TEST_CONNECTION using that account/password, never root; the connection syntax is `Server=127.0.0.1;Port=3307;Database=taskflow_test;User ID=taskflow_test;Password=<local test password>`. The tests migrate it automatically. Stop the container when finished:

```powershell
docker compose --env-file .env.test -f compose.test.yaml down
```

Its database lives in tmpfs, so stopping the container discards its synthetic data. It does not mount the development volume. This Docker recipe was inspected but not run here; local database verification used portable MySQL 8.4.11.

## Frontend

Vitest uses jsdom, React Testing Library, user-event and jest-dom matchers, with explicit cleanup after each test and cleared/restored mocks. Config is separate from Vite's development-server proxy config; both are type-checked. Add feature tests beside components as `*.test.tsx` and helpers under src/test.

From TaskFlow-FE:

```powershell
npm ci
npm run typecheck
npm run lint
npm test
npm run test:ci
npm run build
```

Use `npm run test:watch` interactively. The CI command generates test-results/junit.xml and fails when no tests are discovered. The 92 frontend tests cover authentication, navigation, session lifecycle, errors, CSRF/API behavior and the implemented project/task/dashboard/activity/profile features. See [frontend authentication](frontend-authentication.md) for coverage and the separate real-Edge HTTPS verification. The separate [published-app browser suite](../tests/browser/README.md) covers real-browser journeys; it has been run locally and is not part of the hosted workflow.

## GitHub Actions

`.github/workflows/ci.yml` runs for pushes to main, pull requests and manual dispatch. It has read-only repository permissions, immutable official Action commit pins, disabled persisted checkout credentials, concurrency cancellation and job timeouts.

- **Backend checks:** Ubuntu runner, pinned SDK via global.json, cached locked NuGet restore, local EF tool restore, Release build, migration snapshot check, mandatory live MySQL tests with a fresh 8.4.11 service, TRX no-skip gate, and artifact upload for results/Cobertura even on failures.
- **Deployment smoke:** waits for backend/frontend checks, builds both Docker targets, starts private MySQL, applies migrations explicitly, verifies production HTTPS routing/authentication, recreates the app with persisted keys and checks session recovery/logout revocation. Disposable CI volumes are removed afterward. This job has not run on a Docker host yet.
- **Frontend checks:** pinned Node/npm, npm ci, type-check, lint, Vitest/JUnit and production build, with JUnit artifact upload even on failures.

The disposable MySQL service uses hardcoded CI-only credentials and a dynamically mapped port; no production secrets are required or used. Results are retained for seven days. The workflow publishes nothing; the deployment smoke job starts only temporary local services on its runner. Python 3 is supplied by the Ubuntu hosted runner for the TRX check.

The workflow passed actionlint v1.7.12 validation locally. Backend/frontend check commands were exercised locally; the new container build/recreation commands require their first Docker-host run. When a remote is connected, configure **Backend checks** and **Frontend checks** as required branch checks through repository settings if desired; no remote/branch settings were changed here.

References: [xUnit shared context](https://xunit.net/docs/shared-context), [Testing Library setup](https://testing-library.com/docs/react-testing-library/setup/), and [GitHub .NET build/test guidance](https://docs.github.com/en/actions/tutorials/build-and-test-code/net).

Step 07 authentication coverage and real HTTPS verification are detailed in [authentication.md](authentication.md). Step 08 frontend coverage and browser verification are detailed in [frontend-authentication.md](frontend-authentication.md).

Step 10 adds permission-matrix, safe denial mapping and live transaction/locking checks. See [project foundation](project-foundation.md) for real rollback, no-op, task-scope and waiting-writer verification. Backend Release tests passed all 59 cases without skips. Existing frontend files/dependencies are unchanged by step 10.

Step 11 adds six live project API tests and fourteen frontend project tests; all 65 backend and 38 frontend checks passed. The final published app also passed real Edge HTTPS project, privacy, reload, pagination and mobile checks. See [projects](projects.md). Project API HTTP tests clean only their own synthetic data in FK order.

Step 12 adds two live API/database tests for owner-only replacement, soft deletion, retained rows, no-op history and rollback, plus four frontend edit/confirmation/role/failure tests. All 67 backend and 42 frontend tests pass.

Step 13 adds three live membership HTTP/database tests and five frontend member-panel checks. All 70 backend and 47 frontend tests pass. See [project membership](project-membership.md) for role, concurrency, assignment cleanup and rollback coverage.

Step 14 adds six live task API tests and ten frontend task checks; all 76 backend and 57 frontend tests passed. Coverage includes activity rollback, assignee eligibility/removal races, UTC conversion, visibility, query ordering/paging, form errors and account-switch safety. See [tasks](tasks.md).

Step 15 adds six live task mutation API tests and seven frontend cases; all 82 backend and 64 frontend tests passed. See [task mutations](task-mutations.md) for omission/null semantics, no-op timestamps, retained deletion history, mutation rollback and edit/delete concurrency.

Step 16 adds eight live HTTP/database cases with bounded parent-lock gates, both removal/assignment orderings, removed-member writes, cleanup scope/readdition and failed-removal rollback with a queued assignment. Two frontend tests cover stale assignee drafts. All 90 backend and 66 frontend tests passed. See [assignment concurrency](assignment-concurrency.md).

Step 17 adds five live task query tests and six frontend cases. All 95 backend and 72 frontend checks pass. Explicit ordering/page expectations, combined filter scope, repeated/invalid queries, maximum/empty/far pages and a forced count/create overlap verify the database contract; frontend checks cover URL controls/history/recovery and superseded responses. See [task queries](task-queries.md).

Step 18 adds five live dashboard/progress tests and six frontend overview checks; all 100 backend and 78 frontend tests passed. The aggregate tests force concurrent creation and a clock advance to verify a consistent snapshot and captured UTC time. Scope, overdue equality/completion/deletion, progress rounding and mutation refresh are covered. See [dashboard](dashboard.md).

Step 19 adds five live activity reader tests and six frontend feed cases; all 105 backend and 84 frontend tests passed. Coverage includes visible event types, deleted-task/removed-actor history, immutable public routes, safe summary redaction, precise inclusive time bounds, ordering/paging and a forced concurrent mutation snapshot. See [activity](activity.md).

Step 20 adds three live profile API cases and seven frontend cases. All 108 backend and 91 frontend tests pass. Validation/mass-assignment/CSRF rejection, current-user persistence, no-op timestamps, unchanged credentials/session expiry, conflict persistence, form retry/expiry and delayed cross-account responses are covered. See [profile](profile.md).

Step 21 adds a logging-safety backend test, a client account-change/CSRF dispatch test, and the locked [real-browser review suite](../tests/browser/README.md). Current totals are 109 backend and 92 frontend tests. E2E-001 through E2E-004, 30 accessibility/responsive scans, keyboard/security checks and demo performance measurements pass locally. See [review evidence](verification-review.md). Hosted/container CI remains pending.

Step 22 adds an unknown-applied-migration readiness case (safe 503, liveness 200) and 16 separate standard-library operations tests: `python -m unittest discover -s tests/operations -v`. They cover backup checksums/layout, traversal, invalid/occupied targets, incompatible table-case mode, failed import/count validation, session-stamp rotation and release-payload drift. All 110 backend and 92 frontend tests pass, alongside those 16 operations checks. The operations suite is in backend CI. Real restricted-role recovery, key/data verification and compatible rollback passed locally; see [release evidence](release-runbook.md). Hosted/container verification remains pending.
