# Pure renderer: does not read secrets, write files or start services.
function ConvertTo-TaskFlowPsLiteral([string] $Value) {
    return "'" + $Value.Replace("'", "''") + "'"
}
function Get-TaskFlowStartupCommands([string] $Root, [switch] $SkipDatabase, [switch] $EnableSaasPreview) {
    $database = if ($SkipDatabase) {
        '# Database startup skipped. Configure $env:ConnectionStrings__TaskFlow privately in Terminal A; do not print it.'
    } else {
@'
$taskflowMysqlBase = Join-Path $taskflowRoot '.local/mysql/mysql-8.4.11-winx64'
$taskflowMysqlData = Join-Path $taskflowRoot '.local/mysql/data'
New-Item -ItemType Directory -Force (Join-Path $taskflowRoot '.local/runtime') | Out-Null
if (!(Test-Path "$taskflowMysqlBase/bin/mysqld.exe") -or !(Test-Path $taskflowMysqlData)) { throw 'Follow docs/database-setup.md first.' }
if (!(Get-NetTCPConnection -State Listen -LocalPort 13306 -ErrorAction SilentlyContinue)) {
    Start-Process -FilePath "$taskflowMysqlBase/bin/mysqld.exe" -ArgumentList @(
        '--no-defaults', ('"--basedir=' + $taskflowMysqlBase + '"'), ('"--datadir=' + $taskflowMysqlData + '"'),
        '--port=13306', '--bind-address=127.0.0.1', '--mysqlx=0', '--default-time-zone=+00:00',
        '--character-set-server=utf8mb4', '--collation-server=utf8mb4_0900_ai_ci'
    ) -WindowStyle Hidden -RedirectStandardOutput "$taskflowRoot/.local/runtime/mysql.stdout.log" -RedirectStandardError "$taskflowRoot/.local/runtime/mysql.stderr.log"
}
# Wait for "mysqld is alive"; repeat ping if MySQL is still starting.
$taskflowAdminFile = Join-Path $taskflowRoot '.local/mysql/admin.cnf'
& "$taskflowMysqlBase/bin/mysqladmin.exe" "--defaults-extra-file=$taskflowAdminFile" ping
if ($LASTEXITCODE -ne 0) { throw 'Database not ready. Read .local/runtime/mysql.stderr.log.' }
if ([string]::IsNullOrWhiteSpace($env:ConnectionStrings__TaskFlow)) {
    $env:ConnectionStrings__TaskFlow = (Get-Content -LiteralPath (Join-Path $taskflowRoot '.local/mysql/connections.json') -Raw | ConvertFrom-Json).development
}
'@
    }
    $apiFlags = if ($EnableSaasPreview) {
@'
$env:WorkspaceReads__Enabled = 'true'
$env:WorkspaceWrites__Enabled = 'true'
$env:WorkspaceLifecycle__Enabled = 'true'
$env:Invitations__Enabled = 'true'
'@
    } else { '# Existing backend preview flags are unchanged.' }
    $frontendFlags = if ($EnableSaasPreview) {
@'
$env:VITE_WORKSPACES_ENABLED = 'true'
$env:VITE_WORKSPACE_LIFECYCLE_ENABLED = 'true'
$env:VITE_INVITATIONS_ENABLED = 'true'
'@
    } else { '# Existing frontend preview flags are unchanged.' }
    $plan = @'
# TaskFlow manual startup checklist - copy each terminal section separately.
# Prerequisites: docs/toolchain.md and docs/database-setup.md.
# Private credentials are referenced, never printed. No default account is seeded.
# TERMINAL A - repository root. Stop this checkout's API before building.
Set-Location __ROOT__
$taskflowRoot = (Get-Location).Path
dotnet --version
node --version
npm.cmd --version
# Stop foreground API with Ctrl+C. For a background Release API, select ONLY this checkout:
$taskflowDll = Join-Path $taskflowRoot 'TaskFlow-BE/TaskFlow.Api/bin/Release/net10.0/TaskFlow.Api.dll'
$taskflowExe = Join-Path $taskflowRoot 'TaskFlow-BE/TaskFlow.Api/bin/Release/net10.0/TaskFlow.Api.exe'
Get-CimInstance Win32_Process | Where-Object {
    ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -and $_.CommandLine -match ('(?i)(?:^|[\s"])' + [regex]::Escape($taskflowDll) + '(?=[\s"]|$)')) -or
    ($_.Name -eq 'TaskFlow.Api.exe' -and $_.ExecutablePath -eq $taskflowExe)
} | ForEach-Object { Stop-Process -Id $_.ProcessId; Wait-Process -Id $_.ProcessId -Timeout 15 -ErrorAction SilentlyContinue }
Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -in 13306,5140,5173 } | Select-Object LocalAddress,LocalPort,OwningProcess
# Stop any other 5140/5173 listener in its original terminal. Do not kill all dotnet/node processes.
__DATABASE__
# Only migrate development databases using this checklist.
if ([string]::IsNullOrWhiteSpace($env:ConnectionStrings__TaskFlow)) { throw 'Configure the private development connection in Terminal A.' }
$taskflowConnectionCheck = New-Object System.Data.Common.DbConnectionStringBuilder
$taskflowConnectionCheck.set_ConnectionString($env:ConnectionStrings__TaskFlow)
$taskflowDbName = [string] $taskflowConnectionCheck.get_Item('Database')
if ($taskflowDbName -ne 'taskflow_dev' -and !$taskflowDbName.StartsWith('taskflow_dev_')) { throw 'Use the release runbook for shared/UAT/production databases.' }
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Transport__RequireHttps = 'false'
__API_FLAGS__
# Optional MFA enrollment preview (off by default): $env:Mfa__Enabled = 'true'
# Administrator enforcement is separate; read docs/saas/mfa.md before enabling it.
dotnet restore TaskFlow.slnx --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw 'Tool restore failed.' }
dotnet build TaskFlow.slnx -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Build failed. Stop the Release API before rebuilding locked DLLs.' }
dotnet ef database update --project TaskFlow-BE/TaskFlow.Infrastructure --startup-project TaskFlow-BE/TaskFlow.Api --configuration Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Migration failed.' }
# API runs in foreground. Leave Terminal A open; Ctrl+C stops it.
Set-Location (Join-Path $taskflowRoot 'TaskFlow-BE/TaskFlow.Api')
$env:ASPNETCORE_URLS = 'http://localhost:5140'
& dotnet (Join-Path $taskflowRoot 'TaskFlow-BE/TaskFlow.Api/bin/Release/net10.0/TaskFlow.Api.dll')

# TERMINAL B - a separate terminal; environment settings are not shared between terminals.
Set-Location __ROOT__
npm.cmd --prefix TaskFlow-FE ci
if ($LASTEXITCODE -ne 0) { throw 'Frontend install failed.' }
$env:TASKFLOW_API_TARGET = 'http://localhost:5140'
Remove-Item Env:TASKFLOW_HTTPS_PFX -ErrorAction SilentlyContinue
__FE_FLAGS__
Set-Location TaskFlow-FE
npm.cmd run dev -- --host localhost --port 5173 --strictPort
# Leave Terminal B open; Ctrl+C stops Vite. Restart Vite after changing VITE flags.

# TERMINAL C - each request should return 200 once the services are ready.
Set-Location __ROOT__
(Invoke-WebRequest 'http://localhost:5140/api/v1/health/live' -UseBasicParsing).StatusCode
(Invoke-WebRequest 'http://localhost:5140/api/v1/health/ready' -UseBasicParsing).StatusCode
(Invoke-WebRequest 'http://localhost:5173/api/v1/health/ready' -UseBasicParsing).StatusCode
(Invoke-WebRequest 'http://localhost:5173/api/v1/auth/csrf' -UseBasicParsing).StatusCode
Start-Process 'http://localhost:5173'
# Register, then sign in. Email capture workers are optional and NOT started here.
# Email configuration: docs/saas/invitation-ui.md and docs/saas/account-security.md.
# Application events: __LOGS__; background output/PIDs: __RUNTIME__.
Get-Content -LiteralPath (Join-Path (Get-Location) '.local/runtime/backend.stderr.log') -Tail 60 -ErrorAction SilentlyContinue
# Follow background output (Ctrl+C stops following):
# Get-Content -LiteralPath (Join-Path (Get-Location) '.local/runtime/backend.stdout.log') -Tail 30 -Wait
# Background output files exist only when the helper launched those services.
# Stop foreground API/Vite using Ctrl+C in A/B. Keep MySQL running for the next start.
# Optional MySQL shutdown AFTER stopping API, from repository root:
# & '.local/mysql/mysql-8.4.11-winx64/bin/mysqladmin.exe' "--defaults-extra-file=$((Get-Location).Path)/.local/mysql/admin.cnf" shutdown
'@
    return $plan.Replace('__ROOT__', (ConvertTo-TaskFlowPsLiteral $Root)).Replace('__DATABASE__', $database).Replace('__API_FLAGS__', $apiFlags).Replace('__FE_FLAGS__', $frontendFlags).Replace('__LOGS__', "$Root/.local/logs/taskflow-*.ndjson").Replace('__RUNTIME__', "$Root/.local/runtime")
}
