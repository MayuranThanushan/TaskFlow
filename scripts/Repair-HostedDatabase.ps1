[CmdletBinding()]
param([switch] $Apply, [switch] $UseExistingConnection)
$ErrorActionPreference = 'Stop'
$taskflowRoot = Split-Path -Parent $PSScriptRoot
$taskflowOriginalConnection = [Environment]::GetEnvironmentVariable('ConnectionStrings__TaskFlow', 'Process')
$taskflowOriginalSecretsDirectory = [Environment]::GetEnvironmentVariable('TASKFLOW_SECRETS_DIRECTORY', 'Process')
$taskflowPrivate = $null
$taskflowPointer = [IntPtr]::Zero
Push-Location $taskflowRoot
try {
    Write-Output "Repository: $taskflowRoot"
    Write-Output 'Building isolated database maintenance tool (no web server or hosting keys required).'
    # Do not pass a database secret to MSBuild or print it in command arguments.
    [Environment]::SetEnvironmentVariable('ConnectionStrings__TaskFlow', $null, 'Process')
    [Environment]::SetEnvironmentVariable('TASKFLOW_SECRETS_DIRECTORY', $null, 'Process')
    & dotnet publish TaskFlow-BE/TaskFlow.Api --configuration Release --artifacts-path .local/build/hosted-database --output .local/releases/database-maintenance /p:RestoreLockedMode=true /p:UseAppHost=false --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Database maintenance tool build failed.' }
    if (-not $UseExistingConnection) {
        $taskflowPrivate = Read-Host 'Paste the TiDB migration connection string or mysql:// URL (hidden)' -AsSecureString
        $taskflowPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($taskflowPrivate)
        $taskflowConnection = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($taskflowPointer)
    } else {
        if ([string]::IsNullOrWhiteSpace($taskflowOriginalConnection)) { throw 'Set ConnectionStrings__TaskFlow privately before using -UseExistingConnection.' }
        $taskflowConnection = $taskflowOriginalConnection
    }
    [Environment]::SetEnvironmentVariable('ConnectionStrings__TaskFlow', $taskflowConnection, 'Process')
    if ($Apply) {
        Write-Output 'Applying only pending forward migrations. Use a migration account and retain a verified backup first.'
        $taskflowCommand = '--database-update'
    } else {
        Write-Output 'Read-only schema check. No database changes will be made.'
        $taskflowCommand = '--database-status'
    }
    & dotnet "$taskflowRoot/.local/releases/database-maintenance/TaskFlow.Api.dll" $taskflowCommand
    $taskflowResult = $LASTEXITCODE
    if ($taskflowResult -eq 2) { Write-Output 'Pending migrations found. After backing up, run: .\scripts\Repair-HostedDatabase.ps1 -Apply' }
    if ($taskflowResult -eq 0) { Write-Output 'Schema matches this checkout. Confirm runtime grants, then deploy the same revision in Render.' }
    if ($taskflowResult -eq 3) { Write-Output 'Unknown migration history found. Verify the database/revision; do not delete or fabricate migration history.' }
} finally {
    [Environment]::SetEnvironmentVariable('ConnectionStrings__TaskFlow', $taskflowOriginalConnection, 'Process')
    [Environment]::SetEnvironmentVariable('TASKFLOW_SECRETS_DIRECTORY', $taskflowOriginalSecretsDirectory, 'Process')
    if ($taskflowPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($taskflowPointer) }
    if ($null -ne $taskflowPrivate) { $taskflowPrivate.Dispose() }
    $taskflowConnection = $null
    Pop-Location
}
exit $taskflowResult
