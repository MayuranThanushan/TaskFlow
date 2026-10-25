# Logging and local startup troubleshooting

## What the supplied log means

The API successfully started: the log says Application started and lists https://localhost:7258 and http://localhost:5140. The problem was a later POST /api/v1/auth/login returning 500, not host startup failure.

- Missing wwwroot is a harmless development warning when the backend is run separately. Open the React application at http://localhost:5173, not the API root. The warning is now avoided when no published assets exist. Production artifacts include wwwroot through the publish script.
- GET / on the standalone development API returns 404 because it has no frontend build. That is expected.
- GET /api/v1/auth/me returning 401 before successful login is expected session discovery.
- InvalidOperationException on login was previously too vague to establish the cause from that log alone. The persistence factory throws when ConnectionStrings__TaskFlow is missing. New diagnostics identify that condition as database.configuration_missing. Database reachability and migration mismatch have separate codes.
- On inspection, only the frontend port was listening; the database and backend were stopped. A previous background startup does not survive every restart/reboot. Starting the API from a new terminal also does not inherit a connection string set in an older terminal.

## Start this initialized Windows workspace

From PowerShell in the repository root:

```powershell
./scripts/Start-Local.ps1
```

The script performs these steps in order:

1. Start the initialized portable MySQL instance on 127.0.0.1:13306 if needed. Load its private development connection from ignored .local/mysql/connections.json unless the terminal already has one.
2. Restore locked .NET dependencies and EF tools; build Release; install frontend dependencies if missing.
3. Apply reviewed existing migrations to the development database. The helper only accepts taskflow_dev or taskflow_dev_* names and does not recreate/drop databases.
4. Start the API on http://localhost:5140 with the development connection and HTTP-compatible authentication. Wait for /api/v1/health/ready to return 200.
5. Start Vite on http://localhost:5173. Check the page, proxied database readiness and CSRF bootstrap. Open this frontend URL, register if needed, then sign in.

Existing listeners are reused only after health checks; the script does not kill an unrelated process. A frontend already running with a different proxy/HTTPS configuration must be stopped in its terminal and restarted with the intended settings. Newly launched process IDs and stdout/stderr are saved under .local/runtime. On failure, services already started remain available for diagnosis. They are also left running after successful startup. This helper is a local convenience, not a Windows service or production supervisor.

For a fresh checkout, first install [the toolchain](toolchain.md), provision MySQL using [database setup](database-setup.md), and follow the [README](../README.md). Portable binaries/data/credentials are machine-local, not part of Git. If you run your own development database, set ConnectionStrings__TaskFlow privately and use ./scripts/Start-Local.ps1 -SkipDatabase. Do not pass credentials as command-line arguments. UAT/Production uses the [release runbook](release-runbook.md), not this development migration helper.

## Manual startup, one step at a time

1. Start MySQL using the portable command in [database setup](database-setup.md), or start the development Compose service. Confirm the expected port is listening.
2. In the API PowerShell terminal, set the development connection without displaying it. For this initialized workspace:

```powershell
$taskflowConnection = Get-Content .local/mysql/connections.json -Raw | ConvertFrom-Json
$env:ConnectionStrings__TaskFlow = $taskflowConnection.development
dotnet restore TaskFlow.slnx --locked-mode
dotnet tool restore
dotnet build TaskFlow.slnx -c Release --no-restore
dotnet ef database update --project TaskFlow-BE/TaskFlow.Infrastructure --startup-project TaskFlow-BE/TaskFlow.Api --configuration Release --no-build
dotnet run --project TaskFlow-BE/TaskFlow.Api --launch-profile http
```

3. In another terminal from the root:

```powershell
$env:TASKFLOW_API_TARGET = 'http://localhost:5140'
Remove-Item Env:TASKFLOW_HTTPS_PFX -ErrorAction SilentlyContinue
npm --prefix TaskFlow-FE ci
npm --prefix TaskFlow-FE run dev
```

4. Check http://localhost:5140/api/v1/health/live (API process), then /api/v1/health/ready (database/schema), then http://localhost:5173/api/v1/health/ready (frontend proxy). All should return 200. Open http://localhost:5173/login. For HTTPS or other environments see [transport](transport.md).

## Log files and SIEM ingestion

Local/Development prints single-line UTC console messages without repeated framework scopes. UAT/Production keeps JSON stdout for host collectors, also without redundant scopes. Application file logging is enabled by default in Local/Development and disabled elsewhere so read-only containers continue working.

Application events are UTF-8 NDJSON: one complete JSON object per line, no multiline stack trace parsing required. Default files are .local/logs/taskflow-*.ndjson for the repository API. Each filename contains UTC date, process ID, instance ID and sequence. Files rotate at UTC day changes or approximately 10 MiB and retain the newest 14 files, including prior runs. One record can exceed the size boundary. Retention removes matching old taskflow-*.ndjson files in the dedicated directory; do not mix other applications or API instances in it. Use a separate directory per replica. Synchronous local writes suit this modest application; production throughput and collector delivery need host verification.

| Setting / environment variable | Default |
| --- | --- |
| FileLogging__Enabled | true in Local/Development, false otherwise |
| FileLogging__Directory | Content-root-relative ../../.local/logs; override explicitly for hosting |
| FileLogging__MaxBytes | 10485760, minimum 1024 |
| FileLogging__RetainedFiles | 14, accepted range 1-1000 |

To enable files on a hosted process, set FileLogging__Enabled=true and FileLogging__Directory to an absolute access-controlled writable directory on a persisted volume. The operator manages ACLs, collector access and off-host retention. File logging reports LOG_WRITE_FAILED to stderr on write/retention failure and continues serving requests. Invalid initial directory permissions fail startup explicitly. File output is an application event stream, not an exhaustive security audit trail or guaranteed-delivery queue; framework diagnostics remain on the console. Collect one authoritative stream to avoid duplicate application records.

Stable fields include @timestamp (UTC), schema.version, service.name, service.environment, process.pid, log.level, log.logger, event.code/action, trace.id/span.id, taskflow.trace_id, http.request.method, http.route (route template), http.response.status_code and taskflow.duration_ms. Diagnostic records add error.code/type. Durations are milliseconds; taskflow.trace_id matches the full API Problem Details/X-Trace-Id correlation value. trace.id is the W3C trace identifier when available.

The file sink only accepts TaskFlow categories and allowlisted structured fields. It excludes scopes, raw request paths/IDs/query strings, bodies, headers, emails, cookie/session/CSRF values, connection strings, exception messages and stack traces. Arbitrary log message text is replaced by the stable event action. This deliberately differs from a general-purpose provider: add reviewed fields explicitly when new diagnostics are needed. Existing application messages sent to console also avoid sensitive payloads; keep that rule when adding logs.

| Event / diagnostic | Meaning / action |
| --- | --- |
| 1000 http.request_completed | Request summary; 5xx Error, 4xx Warning, other Information; correlate by trace |
| 2000 api.failure / database.configuration_missing | Set ConnectionStrings__TaskFlow in the API's own process and restart |
| 2001 database.configuration_missing | Startup has no database connection setting; liveness can still work |
| 2002 database.not_ready / database.unreachable | Start MySQL and check configured host/port/credentials/network |
| 2002 database.not_ready / database.schema_mismatch | Apply the reviewed matching release migration; never auto-delete unknown migration history |
| 2002 database.not_ready / database.check_failed | Check restricted grants/configuration and migration health; raw database details remain private |
| 2000 api.failure / database.operation_failed | A database operation failed; use the trace plus readiness/controlled diagnostics |
| 2000 api.failure / api.unhandled | Unexpected application failure, identified by exception type and trace |

Inspect recent local events without showing credentials:

```powershell
Get-ChildItem .local/logs/taskflow-*.ndjson | Sort-Object LastWriteTime -Descending | Select-Object -First 1 | Get-Content -Tail 10 | ForEach-Object { $_ | ConvertFrom-Json } | Select-Object '@timestamp','log.level','event.action','error.code','http.route','http.response.status_code','taskflow.trace_id'
```

A [Filebeat filestream example](../deploy/filebeat.example.yml) decodes dotted JSON keys for SIEM collection. Supply collector output/TLS credentials through your deployment's secret mechanism; no remote logging destination is configured or contacted here. Fields use ECS-style names but this is not a claim of complete ECS or vendor certification. See [Microsoft logging providers](https://learn.microsoft.com/dotnet/core/extensions/logging-providers) and [Elastic filestream NDJSON parser](https://www.elastic.co/docs/reference/beats/filebeat/filebeat-input-filestream).

## Verification on 25 October 2026

Release build: zero warnings/errors. Full backend suite: 122 passes, live MySQL, zero skips. The focused logging/deployment suite was rerun after the deleted-file recovery guard: eight passes. Operations suite: 16 passes. Start-Local completed all five stages, applied no new migrations, and verified frontend/backend readiness and CSRF. Edge rendered sign-in without script errors. A nonexistent synthetic account returned the expected 401 through the frontend proxy, with a corresponding parseable NDJSON event. The development-database name guard rejected a production-like name before migration. Existing user accounts were not changed. Frontend application code/dependencies are unchanged. No SIEM server, container host or public deployment was contacted.
