[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ReleaseDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$resolvedReleaseDirectory = Resolve-Path -LiteralPath $ReleaseDirectory
$portableArchives = @(
    Get-ChildItem -LiteralPath $resolvedReleaseDirectory.Path -File -Filter "*-Portable.zip"
)

if ($portableArchives.Count -ne 1) {
    $archiveNames = @($portableArchives | Select-Object -ExpandProperty Name) -join ", "
    throw "Expected exactly one portable ZIP in '$($resolvedReleaseDirectory.Path)', found $($portableArchives.Count): $archiveNames"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

$requiredEntries = @(
    ".portable"
    "TypeWhisper.exe"
    "Update.exe"
    "current/TypeWhisper.exe"
)
$portableArchive = $portableArchives[0]
$archive = [System.IO.Compression.ZipFile]::OpenRead($portableArchive.FullName)

try {
    $entryNames = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)

    foreach ($entry in $archive.Entries) {
        $normalizedName = $entry.FullName.Replace('\', '/').TrimStart([char]'/')
        $null = $entryNames.Add($normalizedName)
    }

    $missingEntries = @($requiredEntries | Where-Object { -not $entryNames.Contains($_) })
    if ($missingEntries.Count -gt 0) {
        throw "Portable ZIP '$($portableArchive.Name)' is missing required entries: $($missingEntries -join ', ')"
    }
}
finally {
    $archive.Dispose()
}

Write-Host "Validated portable package '$($portableArchive.Name)'."
