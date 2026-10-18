# Testing and continuous integration

Delivery step 06 is implemented. After step 07, the suite contains 30 backend tests (15 requiring MySQL) and two frontend component checks. Local Release verification passed all 30 backend tests with no skips, plus frontend type-check, lint, tests and production build. GitHub execution has not been performed: no remote repository is configured.

## Shared MySQL fixture

PersistenceTests belongs to the MySQL integration collection. Its IAsyncLifetime fixture validates the connection before access, applies migrations once per collection, verifies the MySQL 8.4 server line, and creates a fresh DbContext for each test. Collection execution is serialized. Each persistence mutation test owns a transaction and rolls it back. Authentication HTTP tests span request scopes and instead remove only the synthetic accounts created by their own host. The fixture does not delete databases or wipe shared tables. Database/container startup and shutdown belong to the runner.

Only `taskflow_test` or a name beginning `taskflow_test_` is accepted. A non-root test account is required. Safety tests exercise rejection of development/production database names and privileged/missing accounts before a connection is attempted. These guards reduce accidental misuse; always supply an isolated test database and restricted credentials.

Local mode (no TASKFLOW_TEST_CONNECTION): 15 checks run and 15 live tests are explicitly skipped. Mandatory mode (`TASKFLOW_REQUIRE_MYSQL=true`): a missing connection fails the fixture instead of skipping. An unreachable/invalid database fails setup in either mode when a connection was supplied. The missing-connection mandatory failure path has been verified locally.

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

Use `npm run test:watch` interactively. The CI command generates test-results/junit.xml and fails when no tests are discovered. Current shell tests cover visible availability information/landmarks and keyboard access to the home link. Account forms, loading/error interactions and business workflows will gain tests when those features exist. Browser end-to-end tests remain later work; jsdom is not a real-browser substitute.

## GitHub Actions

`.github/workflows/ci.yml` runs for pushes to main, pull requests and manual dispatch. It has read-only repository permissions, immutable official Action commit pins, disabled persisted checkout credentials, concurrency cancellation and job timeouts.

- **Backend checks:** Ubuntu runner, pinned SDK via global.json, cached locked NuGet restore, local EF tool restore, Release build, migration snapshot check, mandatory live MySQL tests with a fresh 8.4.11 service, TRX no-skip gate, and artifact upload for results/Cobertura even on failures.
- **Frontend checks:** pinned Node/npm, npm ci, type-check, lint, Vitest/JUnit and production build, with JUnit artifact upload even on failures.

The disposable MySQL service uses hardcoded CI-only credentials and a dynamically mapped port; no production secrets are required or used. Results are retained for seven days. The workflow deploys nothing. Python 3 is supplied by the Ubuntu hosted runner for the TRX check.

The workflow passed actionlint v1.7.12 validation locally. All invoked application check commands were exercised locally; the hosted Ubuntu/container path still requires its first GitHub run. When a remote is connected, configure **Backend checks** and **Frontend checks** as required branch checks through repository settings if desired; no remote/branch settings were changed here.

References: [xUnit shared context](https://xunit.net/docs/shared-context), [Testing Library setup](https://testing-library.com/docs/react-testing-library/setup/), and [GitHub .NET build/test guidance](https://docs.github.com/en/actions/tutorials/build-and-test-code/net).

Step 07 authentication coverage and real HTTPS verification are detailed in [authentication.md](authentication.md). No browser account form tests exist before step 08.
