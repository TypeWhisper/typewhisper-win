[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    # Signing changes the shared files after publish. This rewrites the recorded hashes from the signed
    # application files without sharing anything new.
    [switch]$RefreshSharedHashes
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$appRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path
$cliRoot = Join-Path $appRoot 'Cli'
if (-not (Test-Path -LiteralPath (Join-Path $cliRoot 'typewhisper.exe') -PathType Leaf)) { throw 'CLI publish is missing.' }
$manifest = Join-Path $cliRoot '.typewhisper-shared-runtime.json'
if ($RefreshSharedHashes) {
    $recorded = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json -AsHashtable
    if ($null -eq $recorded) { throw 'Missing shared CLI runtime metadata.' }
    $shared = [ordered]@{}
    foreach ($name in @($recorded.Keys | Sort-Object)) {
        if ([string]::IsNullOrWhiteSpace($name) -or $name -match '[/\\:]' -or $name -in '.', '..') { throw 'Invalid shared CLI runtime entry.' }
        $appFile = Join-Path $appRoot $name
        if (-not (Test-Path -LiteralPath $appFile -PathType Leaf)) { throw "Shared CLI runtime is missing: $name" }
        if (Test-Path -LiteralPath (Join-Path $cliRoot $name)) { throw "CLI runtime is duplicated: $name" }
        $shared[$name] = (Get-FileHash -LiteralPath $appFile -Algorithm SHA256).Hash
    }
    $shared | ConvertTo-Json | Set-Content -LiteralPath $manifest -Encoding utf8
    Write-Host "Refreshed the hashes of $($shared.Count) shared CLI runtime files."
    return
}
$shared = [ordered]@{}
$duplicates = @()
foreach ($file in Get-ChildItem -LiteralPath $cliRoot -File) {
    if ($file.Name -eq '.typewhisper-shared-runtime.json') { continue }
    $appFile = Join-Path $appRoot $file.Name
    if (-not (Test-Path -LiteralPath $appFile -PathType Leaf)) { continue }
    if ((Get-Item -LiteralPath $appFile).Length -ne $file.Length) { continue }
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    if ($hash -ne (Get-FileHash -LiteralPath $appFile -Algorithm SHA256).Hash) { continue }
    $shared[$file.Name] = $hash
    $duplicates += $file.FullName
}
# Publish generates a complete CLI first. Its installer reconstructs the full
# independent runtime from these hash-checked shared files before adding PATH.
$shared | ConvertTo-Json | Set-Content -LiteralPath $manifest -Encoding utf8
foreach ($file in $duplicates) { Remove-Item -LiteralPath $file }
Write-Host "Shared $($shared.Count) identical CLI runtime files with the app."
