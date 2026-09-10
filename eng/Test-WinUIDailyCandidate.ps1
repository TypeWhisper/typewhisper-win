[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$RuntimeIdentifier
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($ExpectedVersion -notmatch '^1\.1\.0-daily\.[0-9]{8}\.[0-9]+$') {
    throw 'A candidate must have an explicit 1.1.0-daily version.'
}
$root = (Resolve-Path -LiteralPath $PublishDirectory).Path
$required = @('TypeWhisper.WinUI.exe', 'TypeWhisper.WinUI.dll', 'TypeWhisper.WinUI.runtimeconfig.json',
    'TypeWhisper.WinUI.pri', 'App.xbf', 'Microsoft.UI.Xaml.dll', 'Cli/typewhisper.exe',
    'Cli/TypeWhisper.Cli.dll', 'Cli/TypeWhisper.Cli.runtimeconfig.json', 'Cli/.typewhisper-shared-runtime.json')
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $relative) -PathType Leaf)) {
        throw "Candidate is missing $relative"
    }
}
foreach ($relative in @('Plugins', 'typewhisper-dev-publication.json', 'cli-profile.json', 'Cli/cli-profile.json',
    'api-discovery.json', 'PluginData', 'PluginPackages', 'setup.json', 'workflows.json')) {
    if (Test-Path -LiteralPath (Join-Path $root $relative)) { throw "Candidate contains development/user state: $relative" }
}
foreach ($relative in @('PresentationFramework.dll', 'PresentationCore.dll', 'System.Xaml.dll', 'DirectML.dll', 'onnxruntime.dll', 'coreclr.dll', 'System.Private.CoreLib.dll', 'Cli/coreclr.dll', 'Cli/System.Private.CoreLib.dll')) {
    if (Test-Path -LiteralPath (Join-Path $root $relative)) { throw "Candidate contains an unused host dependency: $relative" }
}
# Both executables must use the installed .NET 10 runtime, without a desktop dependency.
foreach ($relative in @('TypeWhisper.WinUI.runtimeconfig.json', 'Cli/TypeWhisper.Cli.runtimeconfig.json')) {
    $config = Get-Content -LiteralPath (Join-Path $root $relative) -Raw | ConvertFrom-Json -AsHashtable
    $options = $config['runtimeOptions']
    $framework = $options['framework']
    if ($null -eq $framework -or $framework['name'] -ne 'Microsoft.NETCore.App' -or
        $framework['version'] -ne '10.0.0' -or $options.ContainsKey('includedFrameworks') -or
        $options.ContainsKey('frameworks')) { throw "Invalid shared .NET 10 runtime configuration: $relative" }
}
$shared = Get-Content -LiteralPath (Join-Path $root 'Cli/.typewhisper-shared-runtime.json') -Raw | ConvertFrom-Json -AsHashtable
if ($null -eq $shared) { throw 'Missing shared CLI runtime metadata.' }
foreach ($name in $shared.Keys) {
    if ($name -match '[/\\:]' -or $name -in '.', '..' -or $shared[$name] -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Invalid shared CLI runtime entry.' }
    $source = Join-Path $root $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or
        (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $shared[$name]) { throw "Shared CLI runtime is missing or changed: $name" }
    if (Test-Path -LiteralPath (Join-Path (Join-Path $root 'Cli') $name)) { throw "CLI runtime is duplicated: $name" }
}
foreach ($relative in @('TypeWhisper.WinUI.dll', 'Cli/TypeWhisper.Cli.dll')) {
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $root $relative)).ProductVersion
    if (($version -split '\+')[0] -ne $ExpectedVersion) { throw "$relative has unexpected version $version" }
}
# Read the PE header without executing a cross-architecture candidate.
$stream = [IO.File]::OpenRead((Join-Path $root 'TypeWhisper.WinUI.exe'))
$reader = [IO.BinaryReader]::new($stream)
try {
    $stream.Position = 0x3c
    $peOffset = $reader.ReadInt32()
    $stream.Position = $peOffset
    if ($reader.ReadUInt32() -ne 0x4550) { throw 'Invalid executable PE header.' }
    $machine = $reader.ReadUInt16()
    $expected = if ($RuntimeIdentifier -eq 'win-arm64') { 0xaa64 } else { 0x8664 }
    if ($machine -ne $expected) { throw "Executable architecture does not match $RuntimeIdentifier" }
} finally { $reader.Dispose() }
Write-Host "Validated $ExpectedVersion ($RuntimeIdentifier) candidate contents. Native installation/startup acceptance is still required."
