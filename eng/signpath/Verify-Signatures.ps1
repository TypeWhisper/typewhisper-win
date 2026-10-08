[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$FilesJson,
    [Parameter(Mandatory)][ValidateSet('test-signing', 'release-signing')][string]$Policy,
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string]$Thumbprint
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$files = @(Get-Content -LiteralPath $FilesJson -Raw | ConvertFrom-Json)
if ($files.Count -eq 0) { throw 'No signed files were returned.' }
foreach ($file in $files) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Signed file is missing: $file" }
    $signature = Get-AuthenticodeSignature -LiteralPath $file
    if ($null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $Thumbprint) {
        throw "Unexpected signing certificate: $file"
    }
    # PowerShell maps CERT_E_UNTRUSTEDROOT (0x800B0109) to UnknownError and
    # formats its message through Win32Exception. Compare that exact localized
    # message; NotTrusted instead means explicit distrust and must be rejected.
    $expectedTestRoot = $Policy -eq 'test-signing' -and
        [string]$signature.Status -eq 'UnknownError' -and
        $signature.StatusMessage -eq [ComponentModel.Win32Exception]::new(-2146762487).Message
    if ([string]$signature.Status -ne 'Valid' -and -not $expectedTestRoot) {
        throw "Invalid signature for ${file}: $($signature.Status) - $($signature.StatusMessage)"
    }
    if ($Policy -eq 'release-signing' -and $null -eq $signature.TimeStamperCertificate) {
        throw "Production signature has no timestamp: $file"
    }
    Write-Host "Verified $(Split-Path $file -Leaf): $Policy ($($signature.Status))."
}
