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
    'TypeWhisper.WinUI.pri', 'App.xbf', 'Microsoft.UI.Xaml.dll', 'coreclr.dll', 'Cli/typewhisper.exe',
    'Cli/TypeWhisper.Cli.dll', 'Plugins/com.typewhisper.sherpa-onnx/manifest.json')
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $relative) -PathType Leaf)) {
        throw "Candidate is missing $relative"
    }
}
foreach ($relative in @('typewhisper-dev-publication.json', 'cli-profile.json', 'Cli/cli-profile.json',
    'api-discovery.json', 'PluginData', 'PluginPackages', 'setup.json', 'workflows.json')) {
    if (Test-Path -LiteralPath (Join-Path $root $relative)) { throw "Candidate contains development/user state: $relative" }
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
