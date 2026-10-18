$ErrorActionPreference = 'Stop'
$taskflowWorkspace = Split-Path -Parent $PSScriptRoot
$taskflowCertDirectory = Join-Path $taskflowWorkspace '.local/dev-certs'
New-Item -ItemType Directory -Path $taskflowCertDirectory -Force | Out-Null
$taskflowPfx = Join-Path $taskflowCertDirectory 'localhost.pfx'
$taskflowPem = Join-Path $taskflowCertDirectory 'localhost.pem'
$taskflowSettings = Join-Path $taskflowCertDirectory 'settings.json'
if (Test-Path -LiteralPath $taskflowSettings) {
    Write-Output 'Local development certificate settings already exist; reusing them.'
    exit 0
}
$taskflowRandom = New-Object byte[] 32
$taskflowGenerator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try { $taskflowGenerator.GetBytes($taskflowRandom) } finally { $taskflowGenerator.Dispose() }
$taskflowPassword = [Convert]::ToBase64String($taskflowRandom)
& dotnet dev-certs https --export-path $taskflowPfx --password $taskflowPassword
if ($LASTEXITCODE -ne 0) { throw 'Development certificate export failed.' }
$taskflowCertificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($taskflowPfx, $taskflowPassword)
try {
    $taskflowPublic = [Convert]::ToBase64String($taskflowCertificate.RawData, [Base64FormattingOptions]::InsertLineBreaks)
    [IO.File]::WriteAllText($taskflowPem, "-----BEGIN CERTIFICATE-----`n$taskflowPublic`n-----END CERTIFICATE-----`n")
} finally { $taskflowCertificate.Dispose() }
$taskflowJson = @{ pfxPath = $taskflowPfx; certificatePath = $taskflowPem; password = $taskflowPassword } | ConvertTo-Json
[IO.File]::WriteAllText($taskflowSettings, $taskflowJson, (New-Object Text.UTF8Encoding($false)))
Write-Output 'Exported localhost certificate and generated export password under ignored .local/dev-certs. No machine trust settings changed.'
