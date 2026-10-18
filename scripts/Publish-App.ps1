param([string] $OutputDirectory)
$ErrorActionPreference = 'Stop'
$taskflowWorkspace = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $taskflowWorkspace ('.local/releases/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')) }
$taskflowOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $taskflowOutput) { throw 'Choose a new output directory to avoid mixing releases.' }
$taskflowNpm = if ($env:OS -eq 'Windows_NT') { 'npm.cmd' } else { 'npm' }
Push-Location $taskflowWorkspace
try {
    & dotnet restore TaskFlow-BE/TaskFlow.Api --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked backend restore failed.' }
    & dotnet publish TaskFlow-BE/TaskFlow.Api -c Release --no-restore -o $taskflowOutput /p:UseAppHost=false
    if ($LASTEXITCODE -ne 0) { throw 'Backend publish failed.' }
    Push-Location (Join-Path $taskflowWorkspace 'TaskFlow-FE')
    try {
        & $taskflowNpm ci
        if ($LASTEXITCODE -ne 0) { throw 'Locked frontend install failed.' }
        & $taskflowNpm run build
        if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed.' }
    } finally { Pop-Location }
    Copy-Item -LiteralPath (Join-Path $taskflowWorkspace 'TaskFlow-FE/dist') -Destination (Join-Path $taskflowOutput 'wwwroot') -Recurse
    Write-Output "Published app and frontend to $taskflowOutput. Configure Production, HTTPS, database, AllowedHosts and persistent keys before running."
} finally { Pop-Location }
