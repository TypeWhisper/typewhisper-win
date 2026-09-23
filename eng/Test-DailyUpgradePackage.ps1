[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseDirectory,
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$RuntimeIdentifier,
    [Parameter(Mandatory)][string]$ExpectedVersion
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$feed = Get-Content -LiteralPath (Join-Path $root "releases.$RuntimeIdentifier-daily.json") -Raw | ConvertFrom-Json
$full = @($feed.Assets | Where-Object Type -eq 'Full')
if ($full.Count -ne 1 -or $full[0].PackageId -ne 'TypeWhisper' -or $full[0].Version -ne $ExpectedVersion) {
    throw 'The legacy Daily feed must contain exactly one matching TypeWhisper full package.'
}
$name = $full[0].FileName
if ($name -ne [IO.Path]::GetFileName($name) -or $name -match '[/\\:]') { throw 'Invalid upgrade package filename.' }
$package = Join-Path $root $name
if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne $full[0].SHA256) { throw 'Upgrade package hash mismatch.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($package)
try {
    $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    if (-not ($entries | Where-Object { $_ -match '^lib/[^/]+/TypeWhisper\.exe$' })) { throw 'The original entry point is missing.' }
    if ($entries | Where-Object { $_ -match '(^|/)(TypeWhisper\.WinUI\.exe|TypeWhisper\.Windows\.dll|PresentationFramework\.dll)$' }) { throw 'Unexpected old executable or WPF host.' }
    $manifest = @($archive.Entries | Where-Object { $_.FullName -like '*.nuspec' })
    if ($manifest.Count -ne 1) { throw 'Missing package manifest.' }
    $reader = [IO.StreamReader]::new($manifest[0].Open())
    try { [xml]$metadata = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($metadata.package.metadata.id -ne 'TypeWhisper' -or $metadata.package.metadata.mainExe -ne 'TypeWhisper.exe' -or
        $metadata.package.metadata.version -ne $ExpectedVersion -or $metadata.package.metadata.rid -ne $RuntimeIdentifier -or
        $metadata.package.metadata.channel -ne "$RuntimeIdentifier-daily") {
        throw 'Package identity or restart executable changed.'
    }
} finally { $archive.Dispose() }
Write-Host "Validated original-installation upgrade: $ExpectedVersion ($RuntimeIdentifier)."
