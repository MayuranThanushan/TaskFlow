<#
.SYNOPSIS
Starts the initialized local development stack and displays a manual command checklist.
.DESCRIPTION
PrintCommandsOnly renders the checklist without changing files, environment or processes.
Normal startup saves a credential-free checklist under .local/runtime and shows commands
and working directories as it runs them. The saved checklist has separate terminal sections.
.PARAMETER SkipDatabase
Use an already-running development database and a privately configured connection.
.PARAMETER PrintCommandsOnly
Print commands and paths; do not start services, restore, build, migrate or create files.
.PARAMETER EnableSaasPreview
Enable company, team and invitation preview flags for newly launched backend/frontend processes.
Does not enable email capture or start workers. Restart existing Vite to inherit changed flags.
.EXAMPLE
./scripts/Start-Local.ps1 -PrintCommandsOnly -EnableSaasPreview
.EXAMPLE
./scripts/Start-Local.ps1 -EnableSaasPreview
#>
[CmdletBinding()]
param(
    [switch] $SkipDatabase,
    [switch] $PrintCommandsOnly,
    [switch] $EnableSaasPreview
)
$ErrorActionPreference = 'Stop'
$taskflowRoot = Split-Path -Parent $PSScriptRoot
$taskflowRuntime = Join-Path $taskflowRoot '.local/runtime'
. (Join-Path $PSScriptRoot 'Local-StartupCommands.ps1')
$taskflowPlan = Get-TaskFlowStartupCommands -Root $taskflowRoot -SkipDatabase:$SkipDatabase -EnableSaasPreview:$EnableSaasPreview
Write-Output $taskflowPlan
if ($PrintCommandsOnly) { return }
New-Item -ItemType Directory -Force -Path $taskflowRuntime | Out-Null
$taskflowPlan | Set-Content -LiteralPath (Join-Path $taskflowRuntime 'startup-commands.ps1') -Encoding UTF8
function Write-TaskFlowCommand([string] $Command, [string] $Directory = $taskflowRoot) {
    Write-Output "`n[RUN] Directory: $Directory"
    Write-Output "PS> $Command"
}
Write-Output "`nCopyable manual checklist: $taskflowRuntime/startup-commands.ps1"
if ($EnableSaasPreview) {
    $env:WorkspaceReads__Enabled = 'true'
    $env:WorkspaceWrites__Enabled = 'true'
    $env:WorkspaceLifecycle__Enabled = 'true'
    $env:Invitations__Enabled = 'true'
    $env:VITE_WORKSPACES_ENABLED = 'true'
    $env:VITE_WORKSPACE_LIFECYCLE_ENABLED = 'true'
    $env:VITE_INVITATIONS_ENABLED = 'true'
    Write-Output 'Company/invitation preview enabled for new processes. Email capture is configured separately.'
}
function Wait-TaskFlowUrl([string] $Url) {
    Write-TaskFlowCommand ("Invoke-WebRequest -Uri '" + $Url + "' -UseBasicParsing -TimeoutSec 3")
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 3
            if ($response.StatusCode -eq 200) { return }
        } catch { }
        Start-Sleep -Milliseconds 500
    }
    throw "Not ready: $Url. Inspect $taskflowRuntime and $taskflowRoot/.local/logs; check database settings and ports."
}
function Test-TaskFlowPort([int] $Port) {
    $socket = New-Object System.Net.Sockets.TcpClient
    try { $socket.Connect('localhost', $Port); return $true } catch { return $false }
    finally { $socket.Dispose() }
}
Push-Location $taskflowRoot
try {
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:Transport__RequireHttps = 'false'
    foreach ($taskflowTool in @('dotnet', 'node', 'npm.cmd')) {
        if (!(Get-Command $taskflowTool -ErrorAction SilentlyContinue)) {
            throw "Missing tool: $taskflowTool. Follow docs/toolchain.md and open a terminal with that tool on PATH."
        }
        Write-TaskFlowCommand "$taskflowTool --version"
        & $taskflowTool --version
        if ($LASTEXITCODE -ne 0) { throw "Tool check failed: $taskflowTool. Follow docs/toolchain.md." }
    }
    Write-Output '1/5 Database'
    if (!$SkipDatabase -and !(Test-TaskFlowPort 13306)) {
        $taskflowMysql = Join-Path $taskflowRoot '.local/mysql/mysql-8.4.11-winx64'
        $taskflowData = Join-Path $taskflowRoot '.local/mysql/data'
        if (!(Test-Path "$taskflowMysql/bin/mysqld.exe") -or !(Test-Path $taskflowData)) {
            throw 'Portable MySQL is not initialized. Follow docs/database-setup.md, or start your own MySQL and use -SkipDatabase with ConnectionStrings__TaskFlow configured.'
        }
        Write-TaskFlowCommand ('Start-Process -FilePath ' + (ConvertTo-TaskFlowPsLiteral "$taskflowMysql/bin/mysqld.exe") + ' -ArgumentList @(''--no-defaults'', ' + (ConvertTo-TaskFlowPsLiteral ('"--basedir=' + $taskflowMysql + '"')) + ', ' + (ConvertTo-TaskFlowPsLiteral ('"--datadir=' + $taskflowData + '"')) + ', ''--port=13306'', ''--bind-address=127.0.0.1'', ''--mysqlx=0'', ''--default-time-zone=+00:00'', ''--character-set-server=utf8mb4'', ''--collation-server=utf8mb4_0900_ai_ci'') -WindowStyle Hidden')
        $taskflowDbProcess = Start-Process -FilePath "$taskflowMysql/bin/mysqld.exe" -ArgumentList @(
            '--no-defaults', ('"--basedir=' + $taskflowMysql + '"'), ('"--datadir=' + $taskflowData + '"'), '--port=13306',
            '--bind-address=127.0.0.1', '--mysqlx=0', '--default-time-zone=+00:00',
            '--character-set-server=utf8mb4', '--collation-server=utf8mb4_0900_ai_ci'
        ) -WindowStyle Hidden -PassThru -RedirectStandardOutput "$taskflowRuntime/mysql.stdout.log" -RedirectStandardError "$taskflowRuntime/mysql.stderr.log"
        $taskflowDbProcess.Id | Set-Content "$taskflowRuntime/mysql.pid"
        for ($attempt = 0; $attempt -lt 60 -and !(Test-TaskFlowPort 13306); $attempt++) { Start-Sleep -Milliseconds 500 }
        if (!(Test-TaskFlowPort 13306)) { throw 'MySQL did not start. Inspect .local/runtime/mysql.stderr.log.' }
    } elseif (!$SkipDatabase) {
        Write-Output 'MySQL port 13306 is already listening; migrations and readiness will verify database access.'
    } else {
        Write-Output 'Database launch skipped. Using the private connection configured in this terminal.'
    }
    if ([string]::IsNullOrWhiteSpace($env:ConnectionStrings__TaskFlow)) {
        $taskflowConnectionFile = Join-Path $taskflowRoot '.local/mysql/connections.json'
        if ($SkipDatabase -or !(Test-Path $taskflowConnectionFile)) { throw 'Set ConnectionStrings__TaskFlow in this terminal before startup.' }
        Write-TaskFlowCommand ('$env:ConnectionStrings__TaskFlow = (Get-Content -LiteralPath ' + (ConvertTo-TaskFlowPsLiteral $taskflowConnectionFile) + ' -Raw | ConvertFrom-Json).development')
        $env:ConnectionStrings__TaskFlow = (Get-Content -LiteralPath $taskflowConnectionFile -Raw | ConvertFrom-Json).development
    }
    $taskflowConnectionCheck = New-Object System.Data.Common.DbConnectionStringBuilder
    $taskflowConnectionCheck.set_ConnectionString($env:ConnectionStrings__TaskFlow)
    $taskflowDatabaseName = [string] $taskflowConnectionCheck.get_Item('Database')
    if ($taskflowDatabaseName -ne 'taskflow_dev' -and !$taskflowDatabaseName.StartsWith('taskflow_dev_')) {
        throw 'Start-Local only migrates taskflow_dev or taskflow_dev_* databases. Use the release runbook for shared/UAT/Production databases.'
    }
    # Windows locks loaded DLLs. Stop only this checkout's API before rebuilding it.
    $taskflowApiDirectory = Join-Path $taskflowRoot 'TaskFlow-BE/TaskFlow.Api'
    $taskflowReleaseDll = Join-Path $taskflowApiDirectory 'bin/Release/net10.0/TaskFlow.Api.dll'
    $taskflowReleaseExe = Join-Path $taskflowApiDirectory 'bin/Release/net10.0/TaskFlow.Api.exe'
    $taskflowProcesses = @(Get-CimInstance Win32_Process | Where-Object {
        ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -and
            $_.CommandLine -match ('(?i)(?:^|[\s"])' + [regex]::Escape($taskflowReleaseDll) + '(?=[\s"]|$)')) -or
        ($_.Name -eq 'TaskFlow.Api.exe' -and $_.ExecutablePath -eq $taskflowReleaseExe)
    })
    foreach ($taskflowProcess in $taskflowProcesses) {
        Write-Output "Stopping this checkout's Release API process $($taskflowProcess.ProcessId) before rebuilding."
        Write-TaskFlowCommand "Stop-Process -Id $($taskflowProcess.ProcessId)"
        Stop-Process -Id $taskflowProcess.ProcessId -ErrorAction Stop
        Wait-Process -Id $taskflowProcess.ProcessId -Timeout 15 -ErrorAction SilentlyContinue
    }
    if (Test-TaskFlowPort 5140) {
        throw 'Port 5140 is occupied by another API/process. Stop it in its terminal before building; this helper does not stop unrelated processes.'
    }
    Write-Output '2/5 Restore and build' 
    Write-TaskFlowCommand 'dotnet restore TaskFlow.slnx --locked-mode'
    & dotnet restore TaskFlow.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Backend restore failed.' }
    Write-TaskFlowCommand 'dotnet tool restore'
    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'EF tool restore failed.' }
    Write-TaskFlowCommand 'dotnet build TaskFlow.slnx -c Release --no-restore'
    & dotnet build TaskFlow.slnx -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Backend build failed. If MSB3026/MSB3027 reports a lock, stop the API in its original terminal, then retry.' }
    if (!(Test-Path TaskFlow-FE/node_modules/.bin/vite.cmd)) {
        Write-TaskFlowCommand 'npm.cmd --prefix TaskFlow-FE ci'
        & npm.cmd --prefix TaskFlow-FE ci
        if ($LASTEXITCODE -ne 0) { throw 'Frontend dependency installation failed.' }
    } else { Write-Output 'Existing Vite dependencies found. After package-lock.json changes, stop Vite and run npm.cmd --prefix TaskFlow-FE ci.' }
    Write-Output '3/5 Apply development migrations'
    Write-TaskFlowCommand 'dotnet ef database update --project TaskFlow-BE/TaskFlow.Infrastructure --startup-project TaskFlow-BE/TaskFlow.Api --configuration Release --no-build'
    & dotnet ef database update --project TaskFlow-BE/TaskFlow.Infrastructure --startup-project TaskFlow-BE/TaskFlow.Api --configuration Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Migration failed. Check MySQL, credentials and the intended development database.' }
    Write-Output '4/5 Backend'
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS = 'http://localhost:5140'
    $env:Transport__RequireHttps = 'false'
    if (!(Test-TaskFlowPort 5140)) {
        $taskflowApiDll = Join-Path $taskflowRoot 'TaskFlow-BE/TaskFlow.Api/bin/Release/net10.0/TaskFlow.Api.dll'
        Write-TaskFlowCommand '$env:ASPNETCORE_ENVIRONMENT = ''Development''; $env:ASPNETCORE_URLS = ''http://localhost:5140''; $env:Transport__RequireHttps = ''false''' $taskflowApiDirectory
        Write-TaskFlowCommand ('& dotnet ' + (ConvertTo-TaskFlowPsLiteral $taskflowApiDll)) $taskflowApiDirectory
        $taskflowBeProcess = Start-Process -FilePath dotnet -ArgumentList @('"' + $taskflowApiDll + '"') -WorkingDirectory "$taskflowRoot/TaskFlow-BE/TaskFlow.Api" -WindowStyle Hidden -PassThru -RedirectStandardOutput "$taskflowRuntime/backend.stdout.log" -RedirectStandardError "$taskflowRuntime/backend.stderr.log"
        $taskflowBeProcess.Id | Set-Content "$taskflowRuntime/backend.pid"
    } else { Write-Output 'Backend port already occupied; checking its health rather than replacing it.' }
    Wait-TaskFlowUrl 'http://localhost:5140/api/v1/health/ready'
    Write-Output '5/5 Frontend'
    $env:TASKFLOW_API_TARGET = 'http://localhost:5140'
    Remove-Item Env:TASKFLOW_HTTPS_PFX -ErrorAction SilentlyContinue
    if (!(Test-TaskFlowPort 5173)) {
        Write-TaskFlowCommand '$env:TASKFLOW_API_TARGET = ''http://localhost:5140''; Remove-Item Env:TASKFLOW_HTTPS_PFX -ErrorAction SilentlyContinue' "$taskflowRoot/TaskFlow-FE"
        Write-TaskFlowCommand 'npm.cmd run dev -- --host localhost --port 5173 --strictPort' "$taskflowRoot/TaskFlow-FE"
        $taskflowFeProcess = Start-Process -FilePath cmd.exe -ArgumentList @('/d', '/c', 'npm run dev -- --host localhost --port 5173 --strictPort') -WorkingDirectory "$taskflowRoot/TaskFlow-FE" -WindowStyle Hidden -PassThru -RedirectStandardOutput "$taskflowRuntime/frontend.stdout.log" -RedirectStandardError "$taskflowRuntime/frontend.stderr.log"
        $taskflowFeProcess.Id | Set-Content "$taskflowRuntime/frontend.pid"
    } else { Write-Output 'Frontend port already occupied; checking its API proxy. Restart existing Vite in its terminal to inherit changed preview flags.' }
    Wait-TaskFlowUrl 'http://localhost:5173'
    Wait-TaskFlowUrl 'http://localhost:5173/api/v1/health/ready'
    Wait-TaskFlowUrl 'http://localhost:5173/api/v1/auth/csrf'
    Write-Output 'Ready: http://localhost:5173 (frontend), http://localhost:5140 (API). Services are left running.'
    Write-Output "Application events: $taskflowRoot/.local/logs/taskflow-*.ndjson"
    Write-Output "Process stdout/stderr and PID files: $taskflowRuntime"
    Write-Output "Inspect failures: Get-Content -LiteralPath '$taskflowRuntime/backend.stderr.log' -Tail 60"
    Write-Output "Follow output: Get-Content -LiteralPath '$taskflowRuntime/backend.stdout.log' -Tail 30 -Wait"
    Write-Output 'No default account is seeded. Open http://localhost:5173, register, then sign in.'

} finally { Pop-Location }
