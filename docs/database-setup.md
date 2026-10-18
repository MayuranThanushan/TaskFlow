# Database implementation and setup

Delivery step 05 is complete. The initial migration has been applied to clean MySQL 8.4.11 development and test databases. These are local verification databases, not production services.

## Implemented

- Domain entities: Project, ProjectMember, TaskItem and ActivityLog, plus validated status/priority/action types.
- ApplicationUser uses IdentityUser<long> and the existing AspNetUsers store, with display name and UTC audit timestamps. No duplicate Users table exists.
- TaskFlowDbContext maps signed BIGINT keys, unique normalized email, composite membership key, indexes, bounded text, explicit enum strings/check constraints and restrictive foreign keys, including Identity relationships.
- DateTime values are written as UTC DATETIME(6); non-UTC values are rejected. Reads restore DateTimeKind.Utc. Nullable task deadlines round-trip correctly.
- Saving changes normalizes bounded names/descriptions, validates enum values, stamps creation/update times using TimeProvider, rejects ordinary activity edits/deletes and prevents accidental hard deletion of projects/tasks.
- Global filters hide soft-deleted projects and their memberships/tasks/activity. Individually deleted task history remains visible within an active project. IgnoreQueryFilters is reserved for deliberate internal retention operations.
- Identity persistence is registered in dependency injection with the agreed password, uniqueness and lockout policy. Cookie authentication and account endpoints remain step 07 work.

Filters enforce active-record visibility, **not user authorization**. Owner/member eligibility, project locking, member-removal assignment cleanup and automatic activity creation are use-case work in later feature steps. Changing an entity through the DbContext alone does not yet establish those access/business rules. Do not add public generic CRUD controllers around this context.

## Version correction

The earlier baseline selected 8.4.12 from release notes. Its ZIP download returned 404; [Oracle's current Windows download page](https://dev.mysql.com/downloads/mysql/8.4.html) publishes 8.4.11. The baseline and Compose image now use 8.4.11. The downloaded ZIP's MD5 matched the value published by Oracle (2e833921898a9a030ea6bfe81bd811bc), and the executable reports 8.4.11. This is an integrity check against the official HTTPS source, not a claim of detached-signature verification.

## Portable instance on this workspace

MySQL binaries, data, logs, generated local credentials and the session verification helper are under ignored `.local/mysql` (the helper is `.local/verify_mysql.py`). No Windows service was installed. The server was bound to 127.0.0.1:13306 for verification and **stopped after the checks**. Development and test accounts have separate generated passwords and privileges limited to taskflow_dev and taskflow_test respectively. Do not print or commit the connections file.

To start the already initialized instance from the repository root:

```powershell
$taskflowMysqlBase = Join-Path (Get-Location) '.local/mysql/mysql-8.4.11-winx64'
$taskflowMysqlData = Join-Path (Get-Location) '.local/mysql/data'
$taskflowMysqlProcess = Start-Process -FilePath "$taskflowMysqlBase/bin/mysqld.exe" -ArgumentList @(
  '--no-defaults', "--basedir=$taskflowMysqlBase", "--datadir=$taskflowMysqlData",
  '--port=13306', '--bind-address=127.0.0.1', '--mysqlx=0', '--default-time-zone=+00:00',
  '--character-set-server=utf8mb4', '--collation-server=utf8mb4_0900_ai_ci'
) -WindowStyle Hidden -PassThru
$taskflowConnections = Get-Content .local/mysql/connections.json -Raw | ConvertFrom-Json
$env:ConnectionStrings__TaskFlow = $taskflowConnections.development
$env:TASKFLOW_TEST_CONNECTION = $taskflowConnections.test
```

The process needs a short startup interval before accepting connections. Credentials stay in process environment/local files; avoid echoing the variables. Shut down this portable server afterward:

```powershell
$taskflowMysqlAdmin = (Resolve-Path .local/mysql/admin.cnf).Path
& "$taskflowMysqlBase/bin/mysqladmin.exe" "--defaults-extra-file=$taskflowMysqlAdmin" shutdown
```

The portable ZIP is a machine-local convenience and not part of a clean checkout. For reproducible provisioning elsewhere, use the Compose recipe below or install the pinned native server from Oracle's official distribution. Do not run both recipes on the same data directory.

## Clean checkout / Compose alternative

Install/start Docker, copy .env.example to .env, and choose different local root/application passwords. Run `docker compose up -d mysql`. It initializes taskflow_dev on loopback port 3306; it does not automatically create the integration database. Use a separately provisioned taskflow_test database/account when running live tests. Do not reuse the application or root account for the integration fixture.

Supply `ConnectionStrings__TaskFlow` in the process environment using the intended database's local credentials. The design-time factory requires this variable explicitly; it does not fall back to a hardcoded user/password or read the Compose .env file automatically. There is no automatic startup migration or server seeding.

From the root, with a development connection configured:

```powershell
dotnet restore TaskFlow.slnx --locked-mode
dotnet tool restore
dotnet build TaskFlow.slnx --no-restore
dotnet ef database update --project TaskFlow-BE/TaskFlow.Infrastructure --startup-project TaskFlow-BE/TaskFlow.Api --no-build
dotnet ef migrations has-pending-model-changes --project TaskFlow-BE/TaskFlow.Infrastructure --startup-project TaskFlow-BE/TaskFlow.Api --no-build
```

Review migrations before targeting shared databases. Never commit connection strings. A missing connection fails when persistence/EF commands are used; the API's process-liveness endpoint can still run without MySQL configuration.

## Live verification

With TASKFLOW_TEST_CONNECTION set to a dedicated test database, run `dotnet test TaskFlow.slnx --no-restore`. The live fixture refuses databases other than taskflow_test or names starting taskflow_test_. It applies migrations without dropping the database, uses unique synthetic users, and rolls fixture changes back in transactions. Without that variable, seven live tests are explicitly skipped; the nine existing checks still run.

The completed live run passed **16 tests, zero skips**. It verified migration application/idempotency, schema constraints, normalized-email uniqueness, duplicate memberships, foreign keys, invalid enums, soft-delete filters/history retention, UTC/deadline round trips, rollback, append-only/history guards, and Identity password hashing/email lookup. The EF model has no changes pending against its migration.

Step 06 will add shared database fixture lifecycle, frontend test infrastructure and CI. Project authorization, assignment/removal concurrency and mutation-created activity will be tested with their actual use cases. Current persistence tests do not claim those future workflows are implemented.
