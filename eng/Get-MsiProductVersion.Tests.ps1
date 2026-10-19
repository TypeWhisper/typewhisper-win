# Validate the MSI product version mapping without building a package.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$mapper = Join-Path $PSScriptRoot 'Get-MsiProductVersion.ps1'
$checks = 0

foreach ($case in @(
    @{ Version = '1.2.0-daily.20261019.910'; Channel = 'daily'; Expected = '1.2.910' },
    @{ Version = '1.3.0-daily.20270101.65535'; Channel = 'daily'; Expected = '1.3.65535' },
    @{ Version = '1.2.0-rc.1'; Channel = 'rc'; Expected = '1.2.1' },
    @{ Version = '1.2.1-rc.12'; Channel = 'rc'; Expected = '1.2.1012' },
    @{ Version = '1.2.0'; Channel = 'stable'; Expected = '1.2.999' },
    @{ Version = '1.2.3'; Channel = 'stable'; Expected = '1.2.3999' }
)) {
    $actual = & $mapper -Version $case.Version -Channel $case.Channel
    if ($actual -cne $case.Expected) { throw "Expected '$($case.Expected)' for '$($case.Version)', got '$actual'." }
    $checks++
}

# An RC sorts below the Stable release of its version and above the previous Stable release.
$order = @(
    (& $mapper -Version '1.2.0' -Channel stable),
    (& $mapper -Version '1.2.1-rc.1' -Channel rc),
    (& $mapper -Version '1.2.1-rc.2' -Channel rc),
    (& $mapper -Version '1.2.1' -Channel stable)
) | ForEach-Object { [version]$_ }
for ($i = 1; $i -lt $order.Count; $i++) {
    if ($order[$i] -le $order[$i - 1]) { throw "MSI versions do not increase: $($order -join ', ')." }
    $checks++
}

foreach ($invalid in @(
    @{ Version = '1.2.0'; Channel = 'daily' },
    @{ Version = '1.2.0-daily.20261019.65536'; Channel = 'daily' },
    @{ Version = '1.2.0-rc.0'; Channel = 'rc' },
    @{ Version = '1.2.0-rc.999'; Channel = 'rc' },
    @{ Version = '1.2.66'; Channel = 'stable' },
    @{ Version = '256.0.0'; Channel = 'stable' },
    @{ Version = '1.2.0-rc.1'; Channel = 'stable' }
)) {
    $failed = $false
    try { & $mapper -Version $invalid.Version -Channel $invalid.Channel | Out-Null } catch { $failed = $true }
    if (-not $failed) { throw "Expected '$($invalid.Version)' on $($invalid.Channel) to be rejected." }
    $checks++
}
Write-Host "MSI product version checks passed ($checks)."
