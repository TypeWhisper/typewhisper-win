Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('winui-package-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$checks = 0
function Expect-Rejection([string]$Version, [string]$Message) {
    $failure = $null
    try { & "$PSScriptRoot/Test-WinUIDailyCandidate.ps1" -PublishDirectory $fixture -ExpectedVersion $Version -RuntimeIdentifier win-x64 }
    catch { $failure = $_.Exception.Message }
    if ($null -eq $failure -or $failure -notlike "*$Message*") { throw "Expected '$Message'; received '$failure'" }
}
try {
    Expect-Rejection '1.1.0' 'explicit 1.1.0-daily version'; $checks++
    Expect-Rejection '1.1.0-daily.20260910.1' 'missing TypeWhisper.WinUI.exe'; $checks++
    $required = @('TypeWhisper.WinUI.exe', 'TypeWhisper.WinUI.dll', 'TypeWhisper.WinUI.runtimeconfig.json',
        'TypeWhisper.WinUI.pri', 'App.xbf', 'Microsoft.UI.Xaml.dll', 'coreclr.dll', 'Cli/typewhisper.exe',
        'Cli/TypeWhisper.Cli.dll')
    foreach ($name in $required) {
        $file = Join-Path $fixture $name
        New-Item -ItemType Directory -Path (Split-Path -Parent $file) -Force | Out-Null
        [IO.File]::WriteAllText($file, 'synthetic package fixture')
    }
    foreach ($name in @('typewhisper-dev-publication.json', 'Cli/cli-profile.json', 'api-discovery.json')) {
        $file = Join-Path $fixture $name
        [IO.File]::WriteAllText($file, '{}')
        Expect-Rejection '1.1.0-daily.20260910.1' 'development/user state'; $checks++
        Remove-Item -LiteralPath $file
    }
    Expect-Rejection '1.1.0-daily.20260910.1' 'unexpected version'; $checks++
    New-Item -ItemType Directory -Path (Join-Path $fixture 'Plugins') | Out-Null
    Expect-Rejection '1.1.0-daily.20260910.1' 'development/user state: Plugins'; $checks++
    Write-Host "$checks candidate rejection checks passed. Version and architecture acceptance runs on the published CI candidate."
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolved) -notlike 'winui-package-test-*') { throw 'Unexpected fixture path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
