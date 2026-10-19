[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][ValidateSet('daily', 'rc', 'stable')][string]$Channel
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Windows Installer compares only Major.Minor.Build (255.255.65535), so every new package of one upgrade code needs a
# larger Build or a later build replaces nothing. Daily packages use the run number. RC and Stable share the original
# installation's upgrade code: Build = Patch * 1000 + RC number, and 999 for the Stable release, so an RC never
# outranks the Stable release of its version.
switch ($Channel) {
    'daily' {
        if ($Version -cnotmatch '\A([0-9]+)\.([0-9]+)\.[0-9]+-daily\.[0-9]{8}\.([0-9]+)\z') { throw "Daily version '$Version' is not Major.Minor.Patch-daily.yyyyMMdd.Run." }
        $major = [int]$Matches[1]; $minor = [int]$Matches[2]; $build = [long]$Matches[3]
    }
    'rc' {
        if ($Version -cnotmatch '\A([0-9]+)\.([0-9]+)\.([0-9]+)-rc\.([1-9][0-9]*)\z') { throw "RC version '$Version' is not Major.Minor.Patch-rc.N." }
        $major = [int]$Matches[1]; $minor = [int]$Matches[2]
        $rc = [long]$Matches[4]
        if ($rc -gt 998) { throw "RC number $rc does not fit the MSI product version." }
        $build = [long]$Matches[3] * 1000 + $rc
    }
    'stable' {
        if ($Version -cnotmatch '\A([0-9]+)\.([0-9]+)\.([0-9]+)\z') { throw "Stable version '$Version' is not Major.Minor.Patch." }
        $major = [int]$Matches[1]; $minor = [int]$Matches[2]; $build = [long]$Matches[3] * 1000 + 999
    }
}
if ($major -gt 255 -or $minor -gt 255 -or $build -gt 65535) { throw "Version '$Version' does not fit the MSI product version 255.255.65535." }
"$major.$minor.$build"
