[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string] $FromEmail,
    [Parameter(Mandatory=$true)][string] $AllowedRecipient,
    [Parameter(Mandatory=$true)][string] $PublicOrigin,
    [string] $OutputDirectory = '.local/sendgrid-secrets'
)
$ErrorActionPreference = 'Stop'
function Test-PrivateMailAddress([string] $Value) {
    try {
        $taskflowAddress = [Net.Mail.MailAddress]::new($Value)
        return $taskflowAddress.Address -ceq $Value -and !$taskflowAddress.DisplayName -and $taskflowAddress.Host.Contains('.') -and $Value -notmatch '[\r\n]'
    } catch { return $false }
}
if (!(Test-PrivateMailAddress $FromEmail) -or !(Test-PrivateMailAddress $AllowedRecipient)) { throw 'Use one plain verified sender and one plain operator-owned recipient address.' }
$taskflowOrigin = $null
if (![Uri]::TryCreate($PublicOrigin, [UriKind]::Absolute, [ref]$taskflowOrigin) -or $taskflowOrigin.Scheme -ne 'https' -or $taskflowOrigin.UserInfo -or $taskflowOrigin.AbsolutePath -ne '/' -or $taskflowOrigin.Query -or $taskflowOrigin.Fragment) { throw 'Use an HTTPS origin without credentials, path, query or fragment.' }
$taskflowOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $taskflowOutput) { throw 'Choose a new private output directory. Existing settings are never overwritten.' }
if ($taskflowOutput -split '[\\/]' | Where-Object { $_ -in @('wwwroot', 'public') }) { throw 'Secrets cannot be stored under public assets.' }
$taskflowParent = [IO.DirectoryInfo]::new([IO.Path]::GetDirectoryName($taskflowOutput))
while ($taskflowParent) {
    if ($taskflowParent.Exists -and ($taskflowParent.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Use a private path without links or reparse points.' }
    $taskflowParent = $taskflowParent.Parent
}
$taskflowSecureKey = Read-Host 'Paste the SendGrid Mail Send API key (hidden; not saved in shell history)' -AsSecureString
$taskflowPointer = [IntPtr]::Zero
try {
    $taskflowPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($taskflowSecureKey)
    $taskflowKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($taskflowPointer)
    if ($taskflowKey.Length -lt 20 -or $taskflowKey.Length -gt 512 -or $taskflowKey -match '[^\x21-\x7E]') { throw 'The private API key has an invalid format.' }
    New-Item -ItemType Directory -Path $taskflowOutput | Out-Null
    # Lock down the new directory before creating plaintext secret files.
    if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
        $taskflowUser = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $taskflowAcl = [Security.AccessControl.DirectorySecurity]::new()
        $taskflowAcl.SetOwner($taskflowUser)
        $taskflowAcl.SetAccessRuleProtection($true, $false)
        $taskflowAcl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($taskflowUser, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
        Set-Acl -LiteralPath $taskflowOutput -AclObject $taskflowAcl
    } else { & chmod 700 -- $taskflowOutput; if ($LASTEXITCODE -ne 0) { throw 'Private directory permissions failed.' } }
    $taskflowSettings = @{
        Email__Mode = 'SendGrid'; Email__PublicOrigin = $taskflowOrigin.GetLeftPart([UriPartial]::Authority)
        Email__AllowedRecipients__0 = $AllowedRecipient
        Email__SendGrid__FromEmail = $FromEmail; Email__SendGrid__FromName = 'TaskFlow'; Email__SendGrid__ApiKey = $taskflowKey
    }
    $taskflowUtf8 = [Text.UTF8Encoding]::new($false)
    foreach ($taskflowName in $taskflowSettings.Keys) {
        [IO.File]::WriteAllText((Join-Path $taskflowOutput $taskflowName), $taskflowSettings[$taskflowName], $taskflowUtf8)
    }
    $taskflowRequest = @{ workspaceId = 1; messageId = [Guid]::NewGuid().ToString('N'); recipient = $AllowedRecipient; template = 'WorkspaceWelcome'; displayName = 'TaskFlow operator'; companyName = 'email setup check (no company access granted)' } | ConvertTo-Json
    [IO.File]::WriteAllText((Join-Path $taskflowOutput '.test-message.json'), $taskflowRequest, $taskflowUtf8)
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        Get-ChildItem -LiteralPath $taskflowOutput -File -Force | ForEach-Object { & chmod 600 -- $_.FullName; if ($LASTEXITCODE -ne 0) { throw 'Private file permissions failed.' } }
    }
    Write-Output "Private staging settings created in $taskflowOutput. No email was sent."
    Write-Output 'Keep this directory and its test request out of Git, chat and build artifacts. Use docs/saas/onboarding-acceptance.md for the explicit test command.'
} finally {
    if ($taskflowPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($taskflowPointer) }
    $taskflowKey = $null; $taskflowSettings = $null
    if ($taskflowSecureKey) { $taskflowSecureKey.Dispose() }
}
