Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('daily-upgrade-package-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$version = '1.1.0-daily.20260923.1'
function Write-Fixture([string]$PackageId = 'TypeWhisper', [string]$Executable = 'TypeWhisper.exe') {
    $package = Join-Path $fixture 'upgrade.nupkg'
    if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package }
    $archive = [IO.Compression.ZipFile]::Open($package, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $writer = [IO.StreamWriter]::new($archive.CreateEntry('TypeWhisper.nuspec').Open())
        try { $writer.Write("<package><metadata><id>$PackageId</id><version>$version</version><mainExe>$Executable</mainExe><rid>win-x64</rid><channel>win-x64-daily</channel></metadata></package>") }
        finally { $writer.Dispose() }
        $archive.CreateEntry("lib/app/$Executable") | Out-Null
    } finally { $archive.Dispose() }
    @{ Assets = @(@{ PackageId = $PackageId; Version = $version; Type = 'Full'; FileName = 'upgrade.nupkg'; SHA256 = (Get-FileHash -LiteralPath $package).Hash }) } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $fixture 'releases.win-x64-daily.json')
}
function Check-Fixture {
    & "$PSScriptRoot/Test-DailyUpgradePackage.ps1" -ReleaseDirectory $fixture -RuntimeIdentifier win-x64 -ExpectedVersion $version
}
function Expect-Rejection {
    $rejected = $false
    try { Check-Fixture } catch { $rejected = $true }
    if (-not $rejected) { throw 'Invalid upgrade package was accepted.' }
}
try {
    Write-Fixture; Check-Fixture
    Write-Fixture -PackageId 'TypeWhisperDaily'; Expect-Rejection
    Write-Fixture -Executable 'TypeWhisper.WinUI.exe'; Expect-Rejection
    Write-Fixture
    Add-Content -LiteralPath (Join-Path $fixture 'upgrade.nupkg') -Value 'tampered'
    Expect-Rejection
    Write-Host '4 original Daily package checks passed.'
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolved) -notlike 'daily-upgrade-package-*') { throw 'Unexpected fixture path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
