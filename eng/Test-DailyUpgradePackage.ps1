[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseDirectory,
    [Parameter(Mandatory)][ValidateSet('win-x64')][string]$RuntimeIdentifier,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    # The original installation keeps its 1.0 feed per channel: win-x64-daily, win-x64-rc and win-x64.
    [ValidateSet('daily', 'rc', 'stable')][string]$Channel = 'daily'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$velopackChannel = switch ($Channel) { 'daily' { "$RuntimeIdentifier-daily" } 'rc' { "$RuntimeIdentifier-rc" } 'stable' { $RuntimeIdentifier } }
$root = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$feed = Get-Content -LiteralPath (Join-Path $root "releases.$velopackChannel.json") -Raw | ConvertFrom-Json
$full = @($feed.Assets | Where-Object Type -eq 'Full')
if ($full.Count -ne 1 -or $full[0].PackageId -ne 'TypeWhisper' -or $full[0].Version -ne $ExpectedVersion) {
    throw "The $velopackChannel feed must contain exactly one matching TypeWhisper full package."
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
    # Dependency notices carry their own nuspec files under lib/app/licenses. Only
    # the archive-root manifest identifies the Velopack upgrade package.
    $manifest = @($archive.Entries | Where-Object { $_.FullName.Replace('\', '/') -match '^[^/]+\.nuspec$' })
    if ($manifest.Count -ne 1) { throw 'Expected exactly one root package manifest.' }
    # Velopack reads the first nuspec entry, including nested dependency notices.
    # Root identity alone is insufficient if a notice appears earlier in the ZIP.
    $firstManifest = $archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
    if ($firstManifest.FullName -cne $manifest[0].FullName) { throw 'The root package manifest must precede dependency manifests.' }
    $reader = [IO.StreamReader]::new($manifest[0].Open())
    try { [xml]$metadata = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($metadata.package.metadata.id -ne 'TypeWhisper' -or $metadata.package.metadata.mainExe -ne 'TypeWhisper.exe' -or
        $metadata.package.metadata.version -ne $ExpectedVersion -or $metadata.package.metadata.rid -ne $RuntimeIdentifier -or
        $metadata.package.metadata.channel -ne $velopackChannel) {
        throw 'Package identity or restart executable changed.'
    }
} finally { $archive.Dispose() }
Write-Host "Validated original-installation upgrade: $ExpectedVersion ($velopackChannel)."
