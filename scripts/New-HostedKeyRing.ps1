[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string] $OutputDirectory,
    [string] $PreviousKeyRing
)
$ErrorActionPreference = 'Stop'
$taskflowRoot = Split-Path -Parent $PSScriptRoot
$taskflowOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $taskflowOutput) { throw 'Choose a new private output directory. Existing hosting keys are never overwritten.' }
$taskflowArguments = @("--hosting-keys=$taskflowOutput")
if ($PreviousKeyRing) { $taskflowArguments += "--previous-keyring=$([IO.Path]::GetFullPath($PreviousKeyRing))" }
Push-Location $taskflowRoot
try {
    Write-Output "Building isolated hosting-key tool under $taskflowRoot/.local/build/hosting-keys"
    & dotnet build TaskFlow-BE/TaskFlow.Api -c Release --artifacts-path .local/build/hosting-keys /p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw 'Hosting-key tool build failed.' }
    & dotnet "$taskflowRoot/.local/build/hosting-keys/bin/TaskFlow.Api/release/TaskFlow.Api.dll" @taskflowArguments
    if ($LASTEXITCODE -ne 0) { throw 'Hosting-key generation failed. Private key contents were not printed.' }
    Write-Output "Private upload file: $taskflowOutput/key-ring.base64"
    Write-Output 'Use the hosting secret editor; do not print this file in shared logs or paste it into chat.'
} finally { Pop-Location }
