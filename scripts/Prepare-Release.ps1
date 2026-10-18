param(
    [Parameter(Mandatory=$true)][string] $OutputDirectory,
    [ValidateSet('win-x64','linux-x64','linux-arm64')][string] $Runtime = 'linux-x64'
)
$ErrorActionPreference = 'Stop'
$taskflowWorkspace = Split-Path -Parent $PSScriptRoot
$taskflowPython = if (Get-Command python -ErrorAction SilentlyContinue) { 'python' } else { 'python3' }
$taskflowUntrackedBackend = @(& git -C $taskflowWorkspace ls-files --others --exclude-standard -- TaskFlow-BE)
if ($LASTEXITCODE -ne 0 -or $taskflowUntrackedBackend.Count -ne 0) { throw 'Stage new backend inputs before preparing matching application and migration artifacts.' }
$taskflowPackage = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $taskflowPackage) { throw 'Choose a new release-package directory.' }
New-Item -ItemType Directory -Path $taskflowPackage | Out-Null
$taskflowApp = Join-Path $taskflowPackage 'app'
$taskflowOperations = Join-Path $taskflowPackage 'operations'
New-Item -ItemType Directory -Path $taskflowOperations | Out-Null
Push-Location $taskflowWorkspace
$taskflowPreviousConnection = $env:ConnectionStrings__TaskFlow
$taskflowBuildBase = [IO.Path]::GetFullPath((Join-Path $taskflowWorkspace '.local/build'))
$taskflowBuild = Join-Path $taskflowBuildBase ('release-' + [Guid]::NewGuid().ToString('N'))
try {
    & ./scripts/Publish-App.ps1 -OutputDirectory $taskflowApp
    # EF bundle publishes can regenerate RID-specific locks; isolate them from the checkout.
    New-Item -ItemType Directory -Path $taskflowBuild | Out-Null
    $taskflowInputs = @('global.json','Directory.Build.props','.config/dotnet-tools.json') + @(& git ls-files -- TaskFlow-BE)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate tracked backend release inputs.' }
    foreach ($taskflowRelative in $taskflowInputs) {
        $taskflowDestination = Join-Path $taskflowBuild $taskflowRelative
        New-Item -ItemType Directory -Path (Split-Path -Parent $taskflowDestination) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $taskflowWorkspace $taskflowRelative) -Destination $taskflowDestination
    }
    $env:ConnectionStrings__TaskFlow = 'Server=127.0.0.1;Database=taskflow_design;User ID=unused;Password=unused'
    Push-Location $taskflowBuild
    try {
        & dotnet tool restore
        if ($LASTEXITCODE -ne 0) { throw 'EF tooling restore failed.' }
        & dotnet restore TaskFlow-BE/TaskFlow.Api --locked-mode
        if ($LASTEXITCODE -ne 0) { throw 'Isolated locked restore failed.' }
        & dotnet build TaskFlow-BE/TaskFlow.Api --configuration Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Isolated migration build failed.' }
        $taskflowEf = @('--project','TaskFlow-BE/TaskFlow.Infrastructure','--startup-project','TaskFlow-BE/TaskFlow.Api','--configuration','Release','--no-build')
        & dotnet ef migrations has-pending-model-changes @taskflowEf
        if ($LASTEXITCODE -ne 0) { throw 'Migration snapshot is incomplete.' }
        & dotnet ef migrations script 0 @taskflowEf --output (Join-Path $taskflowOperations 'migration-from-empty.sql')
        if ($LASTEXITCODE -ne 0) { throw 'Migration review SQL generation failed.' }
        $taskflowBundle = if ($Runtime.StartsWith('win-')) { 'efbundle.exe' } else { 'efbundle' }
        & dotnet ef migrations bundle @taskflowEf --target-runtime $Runtime --output (Join-Path $taskflowOperations $taskflowBundle)
        if ($LASTEXITCODE -ne 0) { throw 'Migration bundle generation failed.' }
    } finally { Pop-Location }
    & $taskflowPython scripts/release_manifest.py create $taskflowPackage
    if ($LASTEXITCODE -ne 0) { throw 'Release package receipt generation failed.' }
    Write-Output "Release package prepared at $taskflowPackage. Review SQL, back up data and verify the receipt before rollout."
} finally {
    $env:ConnectionStrings__TaskFlow = $taskflowPreviousConnection
    Pop-Location
    if (Test-Path -LiteralPath $taskflowBuild) {
        $taskflowResolvedBuild = (Resolve-Path -LiteralPath $taskflowBuild).ProviderPath
        $taskflowBuildPrefix = $taskflowBuildBase + [IO.Path]::DirectorySeparatorChar
        if (!$taskflowResolvedBuild.StartsWith($taskflowBuildPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing cleanup outside isolated build directory.' }
        try { Remove-Item -LiteralPath $taskflowResolvedBuild -Recurse -Force }
        catch { Write-Output 'An ignored isolated build directory remains for later cleanup.' }
    }
}
