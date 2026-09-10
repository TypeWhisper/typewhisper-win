[CmdletBinding()]
param([Parameter(Mandatory)][string]$PublishDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$appRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path
$cliRoot = Join-Path $appRoot 'Cli'
if (-not (Test-Path -LiteralPath (Join-Path $cliRoot 'typewhisper.exe') -PathType Leaf)) { throw 'CLI publish is missing.' }
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
$manifest = Join-Path $cliRoot '.typewhisper-shared-runtime.json'
$shared | ConvertTo-Json | Set-Content -LiteralPath $manifest -Encoding utf8
foreach ($file in $duplicates) { Remove-Item -LiteralPath $file }
Write-Host "Shared $($shared.Count) identical CLI runtime files with the app."
