Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('cli-bundle-test-' + [guid]::NewGuid().ToString('N'))
$checks = 0
function Write-Fixture([string]$Relative, [string]$Text) {
    $file = Join-Path $fixture $Relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $file) -Force | Out-Null
    [IO.File]::WriteAllText($file, $Text)
}
function Read-Manifest { Get-Content -LiteralPath (Join-Path $fixture 'Cli/.typewhisper-shared-runtime.json') -Raw | ConvertFrom-Json -AsHashtable }
function Expect-Rejection([string]$Message) {
    $failure = $null
    try { & "$PSScriptRoot/Optimize-CliBundle.ps1" -PublishDirectory $fixture -RefreshSharedHashes | Out-Null }
    catch { $failure = $_.Exception.Message }
    if ($null -eq $failure -or $failure -notlike "*$Message*") { throw "Expected '$Message'; received '$failure'" }
}
try {
    Write-Fixture 'TypeWhisper.Core.dll' 'shared core'
    Write-Fixture 'Cli/TypeWhisper.Core.dll' 'shared core'
    Write-Fixture 'TypeWhisper.dll' 'application'
    Write-Fixture 'Cli/TypeWhisper.Cli.dll' 'cli'
    Write-Fixture 'Cli/typewhisper.exe' 'cli launcher'
    & "$PSScriptRoot/Optimize-CliBundle.ps1" -PublishDirectory $fixture | Out-Null
    $shared = Read-Manifest
    if (@($shared.Keys) -join ',' -ne 'TypeWhisper.Core.dll') { throw 'Only identical files are shared.' }
    if (Test-Path -LiteralPath (Join-Path $fixture 'Cli/TypeWhisper.Core.dll')) { throw 'A shared file stayed in the CLI folder.' }
    $checks++

    # Signing rewrites the shared application file; the refresh records the signed file's hash.
    Write-Fixture 'TypeWhisper.Core.dll' 'shared core with signature'
    & "$PSScriptRoot/Optimize-CliBundle.ps1" -PublishDirectory $fixture -RefreshSharedHashes | Out-Null
    $expected = (Get-FileHash -LiteralPath (Join-Path $fixture 'TypeWhisper.Core.dll') -Algorithm SHA256).Hash
    $shared = Read-Manifest
    if (@($shared.Keys).Count -ne 1 -or $shared['TypeWhisper.Core.dll'] -ne $expected) { throw 'The refreshed hash does not match the signed file.' }
    $checks++

    Write-Fixture 'Cli/TypeWhisper.Core.dll' 'duplicate'
    Expect-Rejection 'CLI runtime is duplicated'; $checks++
    Remove-Item -LiteralPath (Join-Path $fixture 'Cli/TypeWhisper.Core.dll')
    Remove-Item -LiteralPath (Join-Path $fixture 'TypeWhisper.Core.dll')
    Expect-Rejection 'Shared CLI runtime is missing'; $checks++
    @{ '../outside.dll' = 'A' * 64 } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture 'Cli/.typewhisper-shared-runtime.json')
    Expect-Rejection 'Invalid shared CLI runtime entry'; $checks++
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if ($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
Write-Host "Optimize-CliBundle checks passed: $checks"
