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
        'Cli/TypeWhisper.Cli.dll', 'Cli/.typewhisper-shared-runtime.json')
    foreach ($name in $required) {
        $file = Join-Path $fixture $name
        New-Item -ItemType Directory -Path (Split-Path -Parent $file) -Force | Out-Null
        [IO.File]::WriteAllText($file, 'synthetic package fixture')
    }
    Set-Content -LiteralPath (Join-Path $fixture 'Cli/.typewhisper-shared-runtime.json') -Value '{}'
    foreach ($name in @('PresentationFramework.dll', 'DirectML.dll', 'onnxruntime.dll')) {
        $file = Join-Path $fixture $name
        [IO.File]::WriteAllText($file, 'unused dependency')
        Expect-Rejection '1.1.0-daily.20260910.1' 'unused host dependency'; $checks++
        Remove-Item -LiteralPath $file
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
    Copy-Item -LiteralPath (Join-Path $fixture 'coreclr.dll') -Destination (Join-Path $fixture 'Cli/coreclr.dll')
    [IO.File]::WriteAllText((Join-Path $fixture 'different.dll'), 'A')
    [IO.File]::WriteAllText((Join-Path $fixture 'Cli/different.dll'), 'B')
    & "$PSScriptRoot/Optimize-CliBundle.ps1" -PublishDirectory $fixture
    $shared = Get-Content -LiteralPath (Join-Path $fixture 'Cli/.typewhisper-shared-runtime.json') -Raw | ConvertFrom-Json -AsHashtable
    if (Test-Path -LiteralPath (Join-Path $fixture 'Cli/coreclr.dll')) { throw 'Identical CLI runtime was not shared.' }
    if ($shared['coreclr.dll'] -ne (Get-FileHash -LiteralPath (Join-Path $fixture 'coreclr.dll')).Hash) { throw 'Shared runtime hash is incorrect.' }
    if ((Get-Content -LiteralPath (Join-Path $fixture 'Cli/different.dll') -Raw) -ne 'B') { throw 'Distinct CLI dependency was modified.' }
    $checks++
    Write-Host "$checks candidate rejection checks passed. Version and architecture acceptance runs on the published CI candidate."
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolved) -notlike 'winui-package-test-*') { throw 'Unexpected fixture path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
