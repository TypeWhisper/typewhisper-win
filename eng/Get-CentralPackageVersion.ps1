param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9_.-]+$')]
    [string]$PackageId,
    [string]$ManifestPath = (Join-Path $PSScriptRoot '../Directory.Packages.props')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[xml]$manifest = Get-Content -Raw -LiteralPath $ManifestPath
$package = $manifest.SelectSingleNode("/Project/ItemGroup/PackageVersion[@Include='$PackageId']")
if ($null -eq $package) { throw "No central version for $PackageId." }
$version = $package.GetAttribute('Version')
if ($version -match '^\$\(([A-Za-z0-9_]+)\)$') {
    $property = $manifest.SelectSingleNode("/Project/PropertyGroup/$($Matches[1])")
    if ($null -eq $property) { throw "Missing version property for $PackageId." }
    $version = $property.InnerText
}
if ($version -notmatch '^\d+\.\d+\.\d+(-[A-Za-z0-9.-]+)?$') {
    throw "Expected a pinned central version for $PackageId, got '$version'."
}
$version
