# TaskFlow

TaskFlow is a private project and task workspace: secure accounts, owner-managed membership, task assignment/status/priority/deadlines, searchable lists, dashboard progress, activity history and editable display names.

The MVP and local verification are complete. Portable release and recovery tooling are ready. Docker execution, hosted CI and verification on a selected hosting target remain release gates; nothing has been published. Start with the [demo and developer handoff](docs/handoff.md).

For this initialized Windows workspace, run `./scripts/Start-Local.ps1` to start DB, BE and FE in order. See [startup troubleshooting and SIEM logging](docs/logging-and-startup.md) for the manual steps, log files and diagnostic codes.

## Clean checkout setup

Prerequisites: .NET SDK from global.json (10.0.400 with compatible patches), Node 22.15.0/npm 11.3.0, MySQL 8.4.11 and Python 3.12+ for operations checks. See [pinned toolchain](docs/toolchain.md). Commands below use PowerShell from the repository root.

```powershell
dotnet restore TaskFlow.slnx --locked-mode
dotnet tool restore
npm --prefix TaskFlow-FE ci
```

Provision an empty development database and a separate non-root integration database using [database setup](docs/database-setup.md) and [testing](docs/testing.md). The clean-checkout Compose option is `Copy-Item .env.example .env`, set two distinct private passwords, then `docker compose up -d --wait mysql`. It creates taskflow_dev on loopback port 3306 with user taskflow. Set ConnectionStrings__TaskFlow privately to that database's connection string; Compose does not export it into your terminal. The optional portable MySQL files under .local are machine-specific and are not in a checkout.

```powershell
# ConnectionStrings__TaskFlow must already contain your development connection.
dotnet build TaskFlow.slnx --no-restore
dotnet ef database update --project TaskFlow-BE/TaskFlow.Infrastructure --startup-project TaskFlow-BE/TaskFlow.Api --no-build
./scripts/Export-DevCertificate.ps1
```

For a fresh machine, explicitly trust the localhost certificate with `dotnet dev-certs https --trust`. Export creates ignored private settings without changing machine trust. Start the API in this terminal:

```powershell
$taskflowCert = Get-Content .local/dev-certs/settings.json -Raw | ConvertFrom-Json
$env:ASPNETCORE_Kestrel__Certificates__Default__Path = $taskflowCert.pfxPath
$env:ASPNETCORE_Kestrel__Certificates__Default__Password = $taskflowCert.password
dotnet run --project TaskFlow-BE/TaskFlow.Api --launch-profile https
```

Start the frontend in another terminal from the root:

```powershell
$taskflowCert = Get-Content .local/dev-certs/settings.json -Raw | ConvertFrom-Json
$env:TASKFLOW_API_TARGET = 'https://localhost:7258'
$env:TASKFLOW_HTTPS_PFX = $taskflowCert.pfxPath
$env:TASKFLOW_HTTPS_PASSWORD = $taskflowCert.password
$env:TASKFLOW_API_CA = $taskflowCert.certificatePath
npm --prefix TaskFlow-FE run dev
```

Open https://localhost:5173, register, then sign in. Registration does not sign in automatically. Local/Development also supports HTTP using the http launch profile and Vite default proxy. See [environment transport settings](docs/transport.md) for HTTP/HTTPS in Local, UAT and Production. Check `/api/v1/health/live` for process health and `/api/v1/health/ready` for database/schema readiness. No automatic migrations or seeded demo accounts run at startup. See [authentication](docs/authentication.md) for session and CSRF details.

## Verification

Set TASKFLOW_TEST_CONNECTION privately to a separate taskflow_test or taskflow_test_* database using a non-root account before backend acceptance tests:

```powershell
$env:TASKFLOW_REQUIRE_MYSQL = 'true'
dotnet build TaskFlow.slnx --configuration Release --no-restore
dotnet test TaskFlow.slnx --configuration Release --no-build --no-restore --logger 'trx;LogFileName=backend.trx' --results-directory TestResults/backend
python scripts/verify_test_results.py TestResults/backend/backend.trx
npm --prefix TaskFlow-FE run typecheck
npm --prefix TaskFlow-FE run lint
npm --prefix TaskFlow-FE run test:ci
npm --prefix TaskFlow-FE run build
python -m unittest discover -s tests/operations -v
```

Latest local results: 122 backend tests with zero skips, 92 frontend tests and 16 operations checks pass. Without the integration connection, 76 backend tests skip in optional mode; mandatory mode fails. See [testing and CI](docs/testing.md) and the isolated [real-browser suite](tests/browser/README.md). Browser tests require a disposable seeded environment and must not target a shared database.

## Architecture and contracts

- [Accepted architecture and ERD](docs/architecture.md), [decisions](docs/decisions/README.md) and [original architecture plan](Documents/Architecture-Plan.md).
- [OpenAPI snapshot and regeneration](docs/contracts/README.md), [API amendments](docs/contracts/api-baseline.md), [database amendments](docs/contracts/database-baseline.md).
- [Acceptance matrix, demo walkthrough, evidence and limitations](docs/handoff.md).
- [Ordered delivery backlog](Documents/Delivery-Backlog.md) and original Word specifications under Documents.

Backend: Domain entities, Application contracts/policies, Infrastructure persistence/services, API transport/composition, Tests. Frontend: React/TypeScript under TaskFlow-FE/src, with feature folders, a shared API client and protected routes. Production serves the built frontend and API from one HTTPS origin.

## Release and operations

[Deployment](docs/deployment.md), [release runbook](docs/release-runbook.md) and [backup/recovery](docs/backup-recovery.md) cover private MySQL, mounted secrets, separate migration/runtime accounts, persisted protection keys, explicit migrations and rollback. `scripts/Prepare-Release.ps1` produces app, migration SQL/bundle and checksum receipts. Production does not expose OpenAPI. Keep credentials, database files, keys, backups, dependencies and generated output outside Git; never put secrets in Vite client variables.
