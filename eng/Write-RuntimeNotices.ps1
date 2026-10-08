param(
    [Parameter(Mandatory)][string]$AssetsPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$AdditionalPackages = ''
)
$ErrorActionPreference = 'Stop'
$assets = Get-Content -LiteralPath $AssetsPath -Raw | ConvertFrom-Json -AsHashtable
$output = [IO.Path]::GetFullPath($OutputDirectory)
$licenses = Join-Path $output 'Licenses'
New-Item -ItemType Directory -Force -Path $licenses | Out-Null
$fallbacks = @{
    'H.GeneratedIcons.System.Drawing' = 'H.NotifyIcon.txt'; 'H.NotifyIcon' = 'H.NotifyIcon.txt'; 'H.NotifyIcon.WinUI' = 'H.NotifyIcon.txt'
    'NAudio.Core' = 'NAudio.txt'; 'NAudio.Wasapi' = 'NAudio.txt'; 'NAudio.WinMM' = 'NAudio.txt'
    'NuGet.Versioning' = 'NuGet.txt'; 'Velopack' = 'Velopack.txt'; 'WinUIEx' = 'WinUIEx.txt'
    'Microsoft.Graphics.Win2D' = 'Win2D.txt'; 'Microsoft.Win32.SystemEvents' = 'DotNet.txt'
    'System.Security.Cryptography.ProtectedData' = 'DotNet.txt'; 'System.Speech' = 'DotNet.txt'
}
$packages = @{}
foreach ($key in $assets.libraries.Keys) {
    if ($assets.libraries[$key].type -eq 'package') { $packages[$key] = $assets.libraries[$key].path }
}
foreach ($package in ($AdditionalPackages -split ';' | Where-Object { $_ })) {
    if ($package -notmatch '^([A-Za-z0-9_.-]+)@\[([A-Za-z0-9_.+-]+)\]$') { throw "Expected a pinned PackageDownload: $package" }
    $packages[$Matches[1] + '/' + $Matches[2]] = ($Matches[1] + '/' + $Matches[2]).ToLowerInvariant()
}
$index = [Collections.Generic.List[string]]::new()
$index.Add('TypeWhisper dependency notices')
$index.Add('')
$index.Add('This inventory includes restored application dependencies and their build tools. Plugins carry separate notices. License files and attribution are copied from the restored packages or the upstream sources recorded in eng/licenses/sources.json. Package license URLs are retained, including additional binary distribution terms.')
$index.Add('')
foreach ($key in ($packages.Keys | Sort-Object)) {
    $directory = $null
    foreach ($root in $assets.packageFolders.Keys) {
        $candidate = Join-Path $root $packages[$key]
        if (Test-Path -LiteralPath $candidate -PathType Container) { $directory = [IO.Path]::GetFullPath($candidate); break }
    }
    if (!$directory) { throw "Restored package not found: $key" }
    $specification = @(Get-ChildItem -LiteralPath $directory -Filter '*.nuspec' -File)
    if ($specification.Count -ne 1) { throw "Expected one NuGet specification for $key" }
    [xml]$spec = Get-Content -LiteralPath $specification[0].FullName -Raw
    $metadata = $spec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
    $id = $metadata.SelectSingleNode('*[local-name()="id"]').InnerText
    $index.Add($key)
    foreach ($field in @('authors', 'copyright', 'license', 'licenseUrl', 'projectUrl')) {
        $node = $metadata.SelectSingleNode('*[local-name()="' + $field + '"]')
        if ($node -and $node.InnerText) { $index.Add('  ' + $field + ': ' + $node.InnerText) }
    }
    $destination = Join-Path $licenses ($key -replace '/', '-')
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    # Preserve NuGet's attribution and repository revision even when a package supplies only a license expression.
    Copy-Item -LiteralPath $specification[0].FullName -Destination (Join-Path $destination 'package.nuspec')
    foreach ($file in (Get-ChildItem -LiteralPath $directory -File -Recurse | Where-Object { $_.Name -match '(?i)^(license|notice|third.party.notice)' })) {
        if ($file.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw "Linked notice file: $($file.FullName)" }
        $relative = [IO.Path]::GetRelativePath($directory, $file.FullName)
        $target = Join-Path $destination $relative
        New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($target)) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
    if ($fallbacks.ContainsKey($id)) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('licenses/' + $fallbacks[$id])) -Destination (Join-Path $destination 'UPSTREAM-LICENSE.txt')
    }
    $index.Add('  Included files: Licenses/' + ($key -replace '/', '-'))
    $index.Add('')
}
[IO.File]::WriteAllLines((Join-Path $output 'THIRD-PARTY-NOTICES.txt'), $index, [Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses/sources.json') -Destination (Join-Path $licenses 'sources.json')
Write-Host "Dependency notices: $($packages.Count) packages in $output"
