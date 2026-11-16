Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('winui-package-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$checks = 0
function Expect-Rejection([string]$Version, [string]$Message, [string]$Channel = 'daily', [string]$Modules = 'premium') {
    $failure = $null
    try { & "$PSScriptRoot/Test-WinUIDailyCandidate.ps1" -PublishDirectory $fixture -ExpectedVersion $Version -RuntimeIdentifier win-x64 -Channel $Channel -ExpectedModules $Modules }
    catch { $failure = $_.Exception.Message }
    if ($null -eq $failure -or $failure -notlike "*$Message*") { throw "Expected '$Message'; received '$failure'" }
}
# Compiles a stand-in TypeWhisper.Modules.dll carrying the given assembly metadata.
function Set-ModulesAssembly([string]$Key, [string]$Value) {
    $path = Join-Path $fixture 'TypeWhisper.Modules.dll'
    Remove-Item -LiteralPath $path -ErrorAction SilentlyContinue
    $attribute = if ($Key) { "[assembly: System.Reflection.AssemblyMetadata(""$Key"", ""$Value"")]" } else { '' }
    Add-Type -TypeDefinition "$attribute`nnamespace TypeWhisper.Modules { public static class Fixture { } }" `
        -OutputAssembly $path -OutputType Library
}
try {
    Expect-Rejection '1.1.0' 'such as 1.2.0-daily.20261013.1'; $checks++
    Expect-Rejection '1.2.1-daily.20261013.1' 'such as 1.2.0-daily.20261013.1'; $checks++
    Expect-Rejection '1.0.0-daily.20261013.1' '1.1.0 or later'; $checks++
    Expect-Rejection '1.1.0' 'such as 1.1.0-rc.1' 'rc'; $checks++
    Expect-Rejection '1.1.0-rc.0' 'such as 1.1.0-rc.1' 'rc'; $checks++
    Expect-Rejection '1.1.0-rc.1' 'such as 1.1.0.' 'stable'; $checks++
    Expect-Rejection '1.1.0-daily.20260910.1' 'such as 1.1.0.' 'stable'; $checks++
    Expect-Rejection '1.0.10' '1.1.0 or later' 'stable'; $checks++
    Expect-Rejection '1.0.0-rc.3' '1.1.0 or later' 'rc'; $checks++
    Expect-Rejection '1.1.0-daily.20260910.1' 'missing TypeWhisper.exe'; $checks++
    Expect-Rejection '1.2.0-daily.20261014.1' 'missing TypeWhisper.exe'; $checks++
    Expect-Rejection '1.1.0-rc.1' 'missing TypeWhisper.exe' 'rc'; $checks++
    Expect-Rejection '1.1.0' 'missing TypeWhisper.exe' 'stable'; $checks++
    $required = @('TypeWhisper.exe', 'TypeWhisper.dll', 'TypeWhisper.runtimeconfig.json',
        'TypeWhisper.pri', 'App.xbf', 'Microsoft.UI.Xaml.dll', 'Cli/typewhisper.exe',
        'Cli/TypeWhisper.Cli.dll', 'Cli/TypeWhisper.Cli.runtimeconfig.json', 'Cli/.typewhisper-shared-runtime.json',
        'TypeWhisper.Modules.dll')
    foreach ($name in $required) {
        $file = Join-Path $fixture $name
        New-Item -ItemType Directory -Path (Split-Path -Parent $file) -Force | Out-Null
        [IO.File]::WriteAllText($file, 'synthetic package fixture')
    }
    Set-Content -LiteralPath (Join-Path $fixture 'Cli/.typewhisper-shared-runtime.json') -Value '{}'
    foreach ($name in @('PresentationFramework.dll', 'DirectML.dll', 'onnxruntime.dll', 'coreclr.dll', 'Cli/coreclr.dll')) {
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
    foreach ($name in @('TypeWhisper.runtimeconfig.json', 'Cli/TypeWhisper.Cli.runtimeconfig.json')) {
        Set-Content -LiteralPath (Join-Path $fixture $name) -Value '{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"11.0.0"}}}'
    }
    Set-Content -LiteralPath (Join-Path $fixture 'Cli/TypeWhisper.Cli.runtimeconfig.json') -Value '{"runtimeOptions":{"includedFrameworks":[]}}'
    Expect-Rejection '1.1.0-daily.20260910.1' 'Invalid shared .NET 11'; $checks++
    Set-Content -LiteralPath (Join-Path $fixture 'Cli/TypeWhisper.Cli.runtimeconfig.json') -Value '{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"11.0.0"}}}'
    Expect-Rejection '1.1.0-daily.20260910.1' 'TypeWhisper.Modules.dll is not a .NET assembly'; $checks++
    # Official builds must ship the optional modules, and every other build the stub.
    Set-ModulesAssembly 'TypeWhisperModules' 'stub'
    Expect-Rejection '1.1.0-daily.20260910.1' "links modules 'stub'; expected 'premium'"; $checks++
    Set-ModulesAssembly 'TypeWhisperModules' 'premium'
    Expect-Rejection '1.1.0-daily.20260910.1' "links modules 'premium'; expected 'stub'" -Modules stub; $checks++
    Expect-Rejection '1.1.0-rc.1' "links modules 'premium'; expected 'stub'" 'rc' 'stub'; $checks++
    Set-ModulesAssembly 'TypeWhisperModules' 'Premium'
    Expect-Rejection '1.1.0-daily.20260910.1' "links modules 'Premium'; expected 'premium'"; $checks++
    Set-ModulesAssembly 'OtherModules' 'premium'
    Expect-Rejection '1.1.0-daily.20260910.1' "links modules ''; expected 'premium'"; $checks++
    Set-ModulesAssembly
    Expect-Rejection '1.1.0-daily.20260910.1' "links modules ''; expected 'stub'" -Modules stub; $checks++
    # Accepted modules lead on to the version check.
    Set-ModulesAssembly 'TypeWhisperModules' 'stub'
    Expect-Rejection '1.1.0-daily.20260910.1' 'unexpected version' -Modules stub; $checks++
    Set-ModulesAssembly 'TypeWhisperModules' 'premium'
    Expect-Rejection '1.1.0-daily.20260910.1' 'unexpected version'; $checks++
    foreach ($name in @('.typewhisper-cli.json', '.typewhisper-shared-runtime.json', 'cli-profile.json', 'CLI-PROFILE.JSON', '', ' ')) {
        $manifest = @{ }
        $manifest[$name] = 'A' * 64
        $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixture 'Cli/.typewhisper-shared-runtime.json')
        Expect-Rejection '1.1.0-daily.20260910.1' 'Invalid shared CLI runtime entry'; $checks++
    }
    Set-Content -LiteralPath (Join-Path $fixture 'Cli/.typewhisper-shared-runtime.json') -Value '{}'
    New-Item -ItemType Directory -Path (Join-Path $fixture 'Plugins') | Out-Null
    Expect-Rejection '1.1.0-daily.20260910.1' 'development/user state: Plugins'; $checks++
    Set-Content -LiteralPath (Join-Path $fixture 'coreclr.dll') -Value 'synthetic duplicate'
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
