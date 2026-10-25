param([switch] $SkipDatabase)
$ErrorActionPreference = 'Stop'
$taskflowRoot = Split-Path -Parent $PSScriptRoot
$taskflowRuntime = Join-Path $taskflowRoot '.local/runtime'
New-Item -ItemType Directory -Force -Path $taskflowRuntime | Out-Null
function Wait-TaskFlowUrl([string] $Url) {
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 3
            if ($response.StatusCode -eq 200) { return }
        } catch { }
        Start-Sleep -Milliseconds 500
    }
    throw "Not ready: $Url. Inspect .local/runtime and .local/logs; check database settings and ports."
}
function Test-TaskFlowPort([int] $Port) {
    $socket = New-Object System.Net.Sockets.TcpClient
    try { $socket.Connect('localhost', $Port); return $true } catch { return $false }
    finally { $socket.Dispose() }
}
Push-Location $taskflowRoot
try {
    Write-Output '1/5 Database'
    if (!$SkipDatabase -and !(Test-TaskFlowPort 13306)) {
        $taskflowMysql = Join-Path $taskflowRoot '.local/mysql/mysql-8.4.11-winx64'
        $taskflowData = Join-Path $taskflowRoot '.local/mysql/data'
        if (!(Test-Path "$taskflowMysql/bin/mysqld.exe") -or !(Test-Path $taskflowData)) {
            throw 'Portable MySQL is not initialized. Follow docs/database-setup.md, or start your own MySQL and use -SkipDatabase with ConnectionStrings__TaskFlow configured.'
        }
        $taskflowDbProcess = Start-Process -FilePath "$taskflowMysql/bin/mysqld.exe" -ArgumentList @(
            '--no-defaults', "--basedir=$taskflowMysql", "--datadir=$taskflowData", '--port=13306',
            '--bind-address=127.0.0.1', '--mysqlx=0', '--default-time-zone=+00:00',
            '--character-set-server=utf8mb4', '--collation-server=utf8mb4_0900_ai_ci'
        ) -WindowStyle Hidden -PassThru -RedirectStandardOutput "$taskflowRuntime/mysql.stdout.log" -RedirectStandardError "$taskflowRuntime/mysql.stderr.log"
        $taskflowDbProcess.Id | Set-Content "$taskflowRuntime/mysql.pid"
        for ($attempt = 0; $attempt -lt 60 -and !(Test-TaskFlowPort 13306); $attempt++) { Start-Sleep -Milliseconds 500 }
        if (!(Test-TaskFlowPort 13306)) { throw 'MySQL did not start. Inspect .local/runtime/mysql.stderr.log.' }
    }
    if ([string]::IsNullOrWhiteSpace($env:ConnectionStrings__TaskFlow)) {
        $taskflowConnectionFile = Join-Path $taskflowRoot '.local/mysql/connections.json'
        if ($SkipDatabase -or !(Test-Path $taskflowConnectionFile)) { throw 'Set ConnectionStrings__TaskFlow in this terminal before startup.' }
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
        Stop-Process -Id $taskflowProcess.ProcessId -ErrorAction Stop
        Wait-Process -Id $taskflowProcess.ProcessId -Timeout 15 -ErrorAction SilentlyContinue
    }
    if (Test-TaskFlowPort 5140) {
        throw 'Port 5140 is occupied by another API/process. Stop it in its terminal before building; this helper does not stop unrelated processes.'
    }
    Write-Output '2/5 Restore and build' 
    & dotnet restore TaskFlow.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Backend restore failed.' }
    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'EF tool restore failed.' }
    & dotnet build TaskFlow.slnx -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Backend build failed. If MSB3026/MSB3027 reports a lock, stop the API in its original terminal, then retry.' }
    if (!(Test-Path TaskFlow-FE/node_modules/.bin/vite.cmd)) {
        & npm.cmd --prefix TaskFlow-FE ci
        if ($LASTEXITCODE -ne 0) { throw 'Frontend dependency installation failed.' }
    }
    Write-Output '3/5 Apply development migrations'
    & dotnet ef database update --project TaskFlow-BE/TaskFlow.Infrastructure --startup-project TaskFlow-BE/TaskFlow.Api --configuration Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Migration failed. Check MySQL, credentials and the intended development database.' }
    Write-Output '4/5 Backend'
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS = 'http://localhost:5140'
    $env:Transport__RequireHttps = 'false'
    if (!(Test-TaskFlowPort 5140)) {
        $taskflowApiDll = Join-Path $taskflowRoot 'TaskFlow-BE/TaskFlow.Api/bin/Release/net10.0/TaskFlow.Api.dll'
        $taskflowBeProcess = Start-Process -FilePath dotnet -ArgumentList @($taskflowApiDll) -WorkingDirectory "$taskflowRoot/TaskFlow-BE/TaskFlow.Api" -WindowStyle Hidden -PassThru -RedirectStandardOutput "$taskflowRuntime/backend.stdout.log" -RedirectStandardError "$taskflowRuntime/backend.stderr.log"
        $taskflowBeProcess.Id | Set-Content "$taskflowRuntime/backend.pid"
    } else { Write-Output 'Backend port already occupied; checking its health rather than replacing it.' }
    Wait-TaskFlowUrl 'http://localhost:5140/api/v1/health/ready'
    Write-Output '5/5 Frontend'
    $env:TASKFLOW_API_TARGET = 'http://localhost:5140'
    Remove-Item Env:TASKFLOW_HTTPS_PFX -ErrorAction SilentlyContinue
    if (!(Test-TaskFlowPort 5173)) {
        $taskflowFeProcess = Start-Process -FilePath cmd.exe -ArgumentList @('/d', '/c', 'npm run dev') -WorkingDirectory "$taskflowRoot/TaskFlow-FE" -WindowStyle Hidden -PassThru -RedirectStandardOutput "$taskflowRuntime/frontend.stdout.log" -RedirectStandardError "$taskflowRuntime/frontend.stderr.log"
        $taskflowFeProcess.Id | Set-Content "$taskflowRuntime/frontend.pid"
    } else { Write-Output 'Frontend port already occupied; checking its API proxy.' }
    Wait-TaskFlowUrl 'http://localhost:5173'
    Wait-TaskFlowUrl 'http://localhost:5173/api/v1/health/ready'
    Wait-TaskFlowUrl 'http://localhost:5173/api/v1/auth/csrf'
    Write-Output 'Ready: http://localhost:5173 (frontend), http://localhost:5140 (API). Services are left running.'
    Write-Output 'Application events: .local/logs/taskflow-*.ndjson; process output: .local/runtime/*.log'
} finally { Pop-Location }
