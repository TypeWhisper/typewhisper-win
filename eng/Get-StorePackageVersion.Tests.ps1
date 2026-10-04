# Validate Store version boundaries without building or launching the app.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$validator = Join-Path $PSScriptRoot 'Get-StorePackageVersion.ps1'
$checks = 0

foreach ($case in @(
    @{ Input = '1.0.0.0'; Expected = '1.0.0.0' },
    @{ Input = '1.1.1.0'; Expected = '1.1.1.0' },
    @{ Input = '1.1.2.0'; Expected = '1.1.2.0' },
    @{ Input = '01.001.00002.0'; Expected = '1.1.2.0' },
    @{ Input = '65535.65535.65535.0'; Expected = '65535.65535.65535.0' }
)) {
    $actual = & $validator -Version $case.Input
    if ($actual -cne $case.Expected) {
        throw "Expected '$($case.Expected)' for '$($case.Input)', got '$actual'."
    }
    $checks++
}

foreach ($invalid in @(
    $null, '', ' ', '1', '1.1', '1.1.0', '1.1.0.0.1',
    '1.1.0-beta.1', '1.1.0-beta.2', '1.1.0.0-rc.1', '1.1.0.0+build.1',
    '0.1.0.0', '65536.1.0.0', '1.65536.0.0', '1.1.65536.0',
    '1.1.1.1', '1.1.1.65535', '1.1.-1.0', '1.1.x.0',
    '1.1.99999999999999999999.0', ' 1.1.1.0', "1.1.1.0`n"
)) {
    $failure = $null
    try { & $validator -Version $invalid | Out-Null }
    catch { $failure = $_.Exception.Message }
    if ($failure -notlike 'Store package version*') {
        throw "Expected a Store version validation error for '$invalid', got '$failure'."
    }
    $checks++
}

Write-Host "$checks Store package version checks passed."
