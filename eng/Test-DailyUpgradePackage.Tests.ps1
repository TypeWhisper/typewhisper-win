Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('daily-upgrade-package-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$version = '1.1.0-daily.20260923.1'
function Write-Fixture([string]$PackageId = 'TypeWhisper', [string]$Executable = 'TypeWhisper.exe',
    [string]$ManifestPath = 'TypeWhisper.nuspec', [switch]$DependencyNotices, [switch]$DuplicateManifest,
    [switch]$DependencyManifestFirst) {
    $package = Join-Path $fixture 'upgrade.nupkg'
    if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package }
    $archive = [IO.Compression.ZipFile]::Open($package, [IO.Compression.ZipArchiveMode]::Create)
    try {
        if ($DependencyManifestFirst) {
            $writer = [IO.StreamWriter]::new($archive.CreateEntry('lib/app/licenses/dependency/package.nuspec').Open())
            try { $writer.Write('<package><metadata><id>Dependency</id></metadata></package>') }
            finally { $writer.Dispose() }
        }
        $writer = [IO.StreamWriter]::new($archive.CreateEntry($ManifestPath).Open())
        try { $writer.Write("<package><metadata><id>$PackageId</id><version>$version</version><mainExe>$Executable</mainExe><rid>$fixtureRid</rid><channel>$fixtureRid-daily</channel></metadata></package>") }
        finally { $writer.Dispose() }
        if ($DependencyNotices) {
            foreach ($path in @('lib/app/licenses/NAudio/package.nuspec', 'lib\app\licenses\Velopack\package.nuspec')) {
                $writer = [IO.StreamWriter]::new($archive.CreateEntry($path).Open())
                try { $writer.Write('<package><metadata><id>Dependency</id></metadata></package>') }
                finally { $writer.Dispose() }
            }
        }
        if ($DuplicateManifest) { $archive.CreateEntry('Unexpected.nuspec') | Out-Null }
        $archive.CreateEntry("lib/app/$Executable") | Out-Null
    } finally { $archive.Dispose() }
    @{ Assets = @(@{ PackageId = $PackageId; Version = $version; Type = 'Full'; FileName = 'upgrade.nupkg'; SHA256 = (Get-FileHash -LiteralPath $package).Hash }) } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $fixture "releases.$fixtureRid-daily.json")
}
function Check-Fixture {
    & "$PSScriptRoot/Test-DailyUpgradePackage.ps1" -ReleaseDirectory $fixture -RuntimeIdentifier $fixtureRid -ExpectedVersion $version
}
function Expect-Rejection([string]$Message = '') {
    $rejected = $false
    try { Check-Fixture } catch {
        if ($Message -and $_.Exception.Message -ne $Message) { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'Invalid upgrade package was accepted.' }
}
try {
    foreach ($fixtureRid in @('win-x64', 'win-arm64')) {
        Write-Fixture; Check-Fixture
        Write-Fixture -DependencyNotices; Check-Fixture
        Write-Fixture -DependencyManifestFirst
        Expect-Rejection 'The root package manifest must precede dependency manifests.'
        Write-Fixture -PackageId 'TypeWhisperDaily'; Expect-Rejection
        Write-Fixture -Executable 'TypeWhisper.WinUI.exe'; Expect-Rejection
        Write-Fixture -ManifestPath 'lib/app/licenses/dependency/package.nuspec'; Expect-Rejection
        Write-Fixture -DuplicateManifest; Expect-Rejection
        Write-Fixture
        Add-Content -LiteralPath (Join-Path $fixture 'upgrade.nupkg') -Value 'tampered'
        Expect-Rejection
    }
    Write-Host '16 original Daily package checks passed.'
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolved) -notlike 'daily-upgrade-package-*') { throw 'Unexpected fixture path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
