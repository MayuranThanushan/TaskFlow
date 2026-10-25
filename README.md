# TaskFlow

Private projects, membership, tasks, dashboard progress, activity history and profiles. Follow this checklist in order to run the application manually on Windows. Commands use PowerShell and the existing D:\TaskFlow workspace.

## 1. Stop the old backend before building

- [ ] In the terminal running the API, press **Ctrl+C**. Also stop Vite with Ctrl+C if you want to restart the frontend. Keep MySQL running.
- [ ] If the backend was started in the background, stop only the API from this checkout with the commands below. Windows locks its loaded DLLs while it is running. Rebuilding first causes MSB3026/MSB3027/MSB3021.

```powershell
Set-Location D:\TaskFlow
$taskflowRoot = (Get-Location).Path
$taskflowApiDll = Join-Path $taskflowRoot 'TaskFlow-BE\TaskFlow.Api\bin\Release\net10.0\TaskFlow.Api.dll'
$taskflowApiExe = Join-Path $taskflowRoot 'TaskFlow-BE\TaskFlow.Api\bin\Release\net10.0\TaskFlow.Api.exe'
Get-CimInstance Win32_Process | Where-Object {
    ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -match ('(?i)(?:^|[\s"])' + [regex]::Escape($taskflowApiDll) + '(?=[\s"]|$)')) -or
    ($_.Name -eq 'TaskFlow.Api.exe' -and $_.ExecutablePath -eq $taskflowApiExe)
} | ForEach-Object {
    Stop-Process -Id $_.ProcessId
    Wait-Process -Id $_.ProcessId -Timeout 15 -ErrorAction SilentlyContinue
}
```

- [ ] Check ports. MySQL uses 13306, API 5140, frontend 5173. If 5140 is still occupied, stop that API in its original terminal before proceeding. Do not terminate every dotnet/node process.

```powershell
Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
    Where-Object { $_.LocalPort -in 13306, 5140, 5173 } |
    Select-Object LocalAddress, LocalPort, OwningProcess
```

## 2. Check the required tools

- [ ] Open **Terminal A** in D:\TaskFlow. Check installed tools: .NET SDK 10.0.400 or its allowed compatible patch, Node 22.15.0 and npm 11.3.0. Full version details: [toolchain](docs/toolchain.md).

```powershell
Set-Location D:\TaskFlow
dotnet --version
node --version
npm --version
```

- [ ] Confirm this machine's portable MySQL and private settings exist. All three commands should return True. If they do not, provision a database using [database setup](docs/database-setup.md) before continuing. These ignored files are not included in a fresh checkout.

```powershell
Test-Path .local/mysql/mysql-8.4.11-winx64/bin/mysqld.exe
Test-Path .local/mysql/data
Test-Path .local/mysql/connections.json
```

## 3. Start and verify the database

- [ ] In **Terminal A**, start portable MySQL only if port 13306 is not already listening. This does not initialize, erase or recreate database files.

```powershell
$taskflowMysqlBase = Join-Path (Get-Location) '.local/mysql/mysql-8.4.11-winx64'
$taskflowMysqlData = Join-Path (Get-Location) '.local/mysql/data'
New-Item -ItemType Directory -Force .local/runtime | Out-Null
if (!(Get-NetTCPConnection -State Listen -LocalPort 13306 -ErrorAction SilentlyContinue)) {
    Start-Process -FilePath "$taskflowMysqlBase/bin/mysqld.exe" -ArgumentList @(
        '--no-defaults', "--basedir=$taskflowMysqlBase", "--datadir=$taskflowMysqlData",
        '--port=13306', '--bind-address=127.0.0.1', '--mysqlx=0',
        '--default-time-zone=+00:00', '--character-set-server=utf8mb4',
        '--collation-server=utf8mb4_0900_ai_ci'
    ) -WindowStyle Hidden -RedirectStandardOutput .local/runtime/mysql.stdout.log -RedirectStandardError .local/runtime/mysql.stderr.log
}
```

- [ ] Wait a few seconds, then check MySQL. Expected: mysqld is alive. If it is still starting, repeat the ping. If startup fails, inspect .local/runtime/mysql.stderr.log.

```powershell
$taskflowMysqlAdmin = (Resolve-Path .local/mysql/admin.cnf).Path
& "$taskflowMysqlBase/bin/mysqladmin.exe" "--defaults-extra-file=$taskflowMysqlAdmin" ping
```

## 4. Configure the backend connection

- [ ] In the **same Terminal A**, load the development connection privately. Do not print or commit the connection string. A different terminal does not inherit this setting.

```powershell
$taskflowConnections = Get-Content .local/mysql/connections.json -Raw | ConvertFrom-Json
$env:ConnectionStrings__TaskFlow = $taskflowConnections.development
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Transport__RequireHttps = 'false'
```

For your own MySQL or Docker database, configure ConnectionStrings__TaskFlow for that development database instead. The Compose development port is 3306, while this portable instance uses 13306. Do not target UAT/Production with development migration commands.

## 5. Restore, build and migrate

- [ ] Keep the API stopped. Run each command in **Terminal A** and proceed only after it succeeds.

```powershell
dotnet restore TaskFlow.slnx --locked-mode
dotnet tool restore
dotnet build TaskFlow.slnx --configuration Release --no-restore
dotnet ef database update --project TaskFlow-BE/TaskFlow.Infrastructure --startup-project TaskFlow-BE/TaskFlow.Api --configuration Release --no-build
```

Expected: build succeeds; migration reports applied changes or database already up to date. A missing connection setting or stopped MySQL must be corrected before starting the API.

## 6. Start the backend

- [ ] In **Terminal A**, run the API in the foreground. Leave this terminal open. It uses the build from step 5 and does not rebuild locked files.

```powershell
dotnet run --project TaskFlow-BE/TaskFlow.Api --configuration Release --no-build --launch-profile http
```

Expected: listening on http://localhost:5140. The standalone development API root can return 404; open the frontend URL in step 8.

## 7. Install and start the frontend

- [ ] Open **Terminal B**. Run npm ci on first setup or after package-lock.json changes; stop any existing Vite process before reinstalling dependencies.

```powershell
Set-Location D:\TaskFlow
npm --prefix TaskFlow-FE ci
```

- [ ] Configure the HTTP API proxy and start Vite. Leave Terminal B open.

```powershell
$env:TASKFLOW_API_TARGET = 'http://localhost:5140'
Remove-Item Env:TASKFLOW_HTTPS_PFX -ErrorAction SilentlyContinue
npm --prefix TaskFlow-FE run dev
```

Expected: http://localhost:5173. If port 5173 is occupied, stop the earlier Vite process in its terminal; do not start another copy on a different port accidentally.

## 8. Verify and open the application

- [ ] Open **Terminal C** and check process health, database readiness and the frontend proxy. Every command should print 200.

```powershell
(Invoke-WebRequest http://localhost:5140/api/v1/health/live -UseBasicParsing).StatusCode
(Invoke-WebRequest http://localhost:5140/api/v1/health/ready -UseBasicParsing).StatusCode
(Invoke-WebRequest http://localhost:5173/api/v1/health/ready -UseBasicParsing).StatusCode
(Invoke-WebRequest http://localhost:5173/api/v1/auth/csrf -UseBasicParsing).StatusCode
```

- [ ] Open **http://localhost:5173**. Register an account if needed, then sign in. Registration does not automatically sign in. There are no default passwords or automatically seeded accounts.

## 9. Stop or restart cleanly

- [ ] Stop backend and frontend with **Ctrl+C** in Terminals A and B. Before any new Release build, stop the Release backend first, then repeat steps 4-8. If the backend was launched in the background, use step 1.
- [ ] To stop portable MySQL, run this from the root after stopping the API:

```powershell
$taskflowMysqlBase = Join-Path (Get-Location) '.local/mysql/mysql-8.4.11-winx64'
$taskflowMysqlAdmin = (Resolve-Path .local/mysql/admin.cnf).Path
& "$taskflowMysqlBase/bin/mysqladmin.exe" "--defaults-extra-file=$taskflowMysqlAdmin" shutdown
```

## Troubleshooting checklist

- [ ] **MSB3026/MSB3027/MSB3021 / DLL locked:** stop the API first (step 1), then rebuild. Deleting bin/obj while the API is running does not resolve the underlying lock.
- [ ] **Readiness 503:** confirm MySQL is running, set the connection in the API terminal, apply migrations, then restart the API.
- [ ] **database.configuration_missing:** repeat step 4 in the terminal that actually starts the backend.
- [ ] **401 on auth/me before login:** expected; sign in through the frontend.
- [ ] **404 on API root / missing wwwroot:** development uses Vite at port 5173. A combined release is produced separately.
- [ ] **HTTPS required / certificate problems:** follow [HTTP/HTTPS environment configuration](docs/transport.md). This checklist uses local HTTP; public deployments should use HTTPS.
- [ ] **Need diagnostic files:** application JSON events are under .local/logs/taskflow-*.ndjson. See [logging and troubleshooting](docs/logging-and-startup.md).

## Optional verification checklist

- [ ] Stop the API before building. Start MySQL and set a separate non-root test connection, then run backend tests. This workspace already has a dedicated test connection; never use the development database for tests.

```powershell
$taskflowConnections = Get-Content .local/mysql/connections.json -Raw | ConvertFrom-Json
$env:TASKFLOW_TEST_CONNECTION = $taskflowConnections.test
$env:TASKFLOW_REQUIRE_MYSQL = 'true'
dotnet build TaskFlow.slnx --configuration Release --no-restore
dotnet test TaskFlow.slnx --configuration Release --no-build --no-restore --logger 'trx;LogFileName=backend.trx' --results-directory TestResults/backend
python scripts/verify_test_results.py TestResults/backend/backend.trx
```

- [ ] Run frontend and operations checks from the root:

```powershell
npm --prefix TaskFlow-FE run typecheck
npm --prefix TaskFlow-FE run lint
npm --prefix TaskFlow-FE run test:ci
npm --prefix TaskFlow-FE run build
python -m unittest discover -s tests/operations -v
```

Recorded local results: 122 backend passes with live MySQL and zero skips, 95 frontend passes and 16 operations passes. Without a test connection, 76 live backend tests skip in optional mode; mandatory mode fails. See [testing/CI](docs/testing.md) and the isolated [browser suite](tests/browser/README.md).

## Project and release references

[Architecture/ERD](docs/architecture.md), [decisions](docs/decisions/README.md), [API contracts/OpenAPI](docs/contracts/README.md), [demo/acceptance handoff](docs/handoff.md), [delivery backlog](Documents/Delivery-Backlog.md), [deployment](docs/deployment.md), [release runbook](docs/release-runbook.md), [backup/recovery](docs/backup-recovery.md).

The implemented MVP is locally verified. Container execution, hosted CI and selected-host verification remain release gates; nothing has been publicly deployed. Keep private settings, credentials, keys, databases and generated output outside Git.

## Next product releases

The target for future releases is multi-company SaaS. The [gap assessment](Documents/Product-Gap-Assessment.md) documents 25 findings; the [feature roadmap](Documents/Feature-Release-Roadmap.md) sequences 51 tasks for workspaces, audit, invitations, notifications, Kanban and later commercial/enterprise capabilities. Read the [architecture proposal](docs/saas-architecture.md) before implementation. These are planned changes, not shipped features. SaaS-001 is complete as a design task: [company boundaries and permissions](docs/decisions/0005-commercial-saas-boundaries.md), [migration contract](docs/saas/migration-cutover.md) and [security acceptance cases](docs/saas/tenant-threat-model.md). SaaS-002 adds [delivery/security checks and release gates](docs/ci-release-gates.md); hosted execution and repository protection still need verification. SaaS-003 adds [typed API contracts and drift verification](docs/contracts/README.md). Next implementation task: SaaS-004 service metrics and trace export.
