param([string] $OutputDirectory)
$ErrorActionPreference = 'Stop'
$taskflowWorkspace = Split-Path -Parent $PSScriptRoot
$taskflowPython = if (Get-Command python -ErrorAction SilentlyContinue) { 'python' } else { 'python3' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $taskflowWorkspace ('.local/releases/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')) }
$taskflowOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $taskflowOutput) { throw 'Choose a new output directory to avoid mixing releases.' }
$taskflowNpm = if ($env:OS -eq 'Windows_NT') { 'npm.cmd' } else { 'npm' }
$taskflowBuildBase = [IO.Path]::GetFullPath((Join-Path $taskflowWorkspace '.local/build'))
$taskflowFrontendBuild = Join-Path $taskflowBuildBase ('frontend-' + [Guid]::NewGuid().ToString('N'))
Push-Location $taskflowWorkspace
try {
    & dotnet restore TaskFlow-BE/TaskFlow.Api --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked backend restore failed.' }
    & dotnet publish TaskFlow-BE/TaskFlow.Api -c Release --no-restore -o $taskflowOutput /p:UseAppHost=false
    if ($LASTEXITCODE -ne 0) { throw 'Backend publish failed.' }
    # Isolate the build from native modules held open by a local dev/test process. Never copy local env files.
    New-Item -ItemType Directory -Path $taskflowFrontendBuild | Out-Null
    $taskflowFrontendSource = Join-Path $taskflowWorkspace 'TaskFlow-FE'
    foreach ($taskflowName in @('src', 'public', 'package.json', 'package-lock.json', 'index.html', 'tsconfig.json', 'tsconfig.app.json', 'tsconfig.node.json', 'vite.config.ts', 'vitest.config.ts', 'eslint.config.js')) {
        Copy-Item -LiteralPath (Join-Path $taskflowFrontendSource $taskflowName) -Destination $taskflowFrontendBuild -Recurse
    }
    Push-Location $taskflowFrontendBuild
    try {
        & $taskflowNpm ci
        if ($LASTEXITCODE -ne 0) { throw 'Locked frontend install failed.' }
        & $taskflowNpm run build
        if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed.' }
    } finally { Pop-Location }
    Copy-Item -LiteralPath (Join-Path $taskflowFrontendBuild 'dist') -Destination (Join-Path $taskflowOutput 'wwwroot') -Recurse
    & $taskflowPython scripts/release_manifest.py create $taskflowOutput
    if ($LASTEXITCODE -ne 0) { throw 'Release receipt generation failed.' }
    Write-Output "Published app and frontend to $taskflowOutput. Configure Production, HTTPS, database, AllowedHosts and persistent keys before running."
} finally {
    Pop-Location
    if (Test-Path -LiteralPath $taskflowFrontendBuild) {
        $taskflowResolvedBuild = (Resolve-Path -LiteralPath $taskflowFrontendBuild).ProviderPath
        $taskflowBuildPrefix = $taskflowBuildBase + [IO.Path]::DirectorySeparatorChar
        if (!$taskflowResolvedBuild.StartsWith($taskflowBuildPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing cleanup outside the isolated build directory.' }
        try { Remove-Item -LiteralPath $taskflowResolvedBuild -Recurse -Force }
        catch { Write-Output "Temporary build files remain at $taskflowResolvedBuild; the workspace dependencies were not modified." }
    }
}
