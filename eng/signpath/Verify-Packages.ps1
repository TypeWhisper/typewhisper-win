[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseDirectory,
    [Parameter(Mandatory)][ValidateSet('test-signing', 'release-signing')][string]$Policy,
    [Parameter(Mandatory)][string]$Thumbprint
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('signpath-verify-' + [Guid]::NewGuid())
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    $files = @(Get-ChildItem -LiteralPath $root -Filter '*Setup.exe' -File | Select-Object -ExpandProperty FullName)
    if ($files.Count -ne 1) { throw 'Expected exactly one setup executable.' }
    foreach ($pattern in @('*-Portable.zip', '*-full.nupkg')) {
        $archives = @(Get-ChildItem -LiteralPath $root -Filter $pattern -File)
        if ($archives.Count -ne 1) { throw "Expected exactly one $pattern archive." }
        $destination = Join-Path $temporary $archives[0].BaseName
        [IO.Compression.ZipFile]::ExtractToDirectory($archives[0].FullName, $destination)
        $executables = @(Get-ChildItem -LiteralPath $destination -Recurse -File |
            Where-Object { $_.Name -match '^(?:TypeWhisper.*|Squirrel|Update)\.exe$' })
        if ($executables.Count -lt 4) { throw "Missing application, CLI, launcher or updater in $($archives[0].Name)." }
        $files += $executables.FullName
    }
    $manifest = Join-Path $temporary 'files.json'
    ConvertTo-Json -InputObject @($files) | Set-Content -LiteralPath $manifest
    & "$PSScriptRoot/Verify-Signatures.ps1" -FilesJson $manifest -Policy $Policy -Thumbprint $Thumbprint
} finally {
    # This directory is created above with a fixed prefix and a fresh GUID.
    $resolvedTemporary = [IO.Path]::GetFullPath($temporary)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedTemporary.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid verification directory.' }
    Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
}
