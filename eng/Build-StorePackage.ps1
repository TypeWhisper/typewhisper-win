param(
    [string]$Version = $env:VERSION,
    [ValidateSet('stable', 'beta')]
    [string]$StoreProduct = 'stable',
    [ValidateSet("win-x64", "win-arm64")]
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Configuration = "Release",
    [string]$OutputRoot = "artifacts/store"
)

$ErrorActionPreference = "Stop"

function Get-MakeAppxPath {
    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (-not (Test-Path $kitsRoot)) {
        throw "Windows SDK not found. Install the Windows 10/11 SDK with MakeAppx.exe."
    }

    $candidate = Get-ChildItem $kitsRoot -Recurse -Filter makeappx.exe |
        Where-Object { $_.FullName -match "\\x64\\makeappx\.exe$" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1

    if ($null -eq $candidate) {
        throw "MakeAppx.exe was not found under '$kitsRoot'."
    }

    return $candidate.FullName
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$msixVersion = & (Join-Path $PSScriptRoot 'Get-StorePackageVersion.ps1') -Version $Version
$manifest = & (Join-Path $PSScriptRoot 'New-StorePackageManifest.ps1') -Version $msixVersion -StoreProduct $StoreProduct -RuntimeIdentifier $RuntimeIdentifier
$platform = if ($RuntimeIdentifier -eq 'win-arm64') { 'ARM64' } else { 'x64' }

$outputRootPath = [IO.Path]::GetFullPath((Join-Path $repoRoot "$OutputRoot/$StoreProduct"))
$repoPrefix = [IO.Path]::GetFullPath($repoRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $outputRootPath.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Store output must be inside the checkout.'
}
$publishDir = Join-Path $outputRootPath "publish/$RuntimeIdentifier"
$layoutDir = Join-Path $outputRootPath "layout/$RuntimeIdentifier"
$packageDir = Join-Path $outputRootPath "packages"
$packagePath = Join-Path $packageDir "TypeWhisper-$StoreProduct-$RuntimeIdentifier-$msixVersion.msix"
$assetsPath = Join-Path $repoRoot "src/TypeWhisper.Windows.StorePackage/Assets"

Remove-Item -LiteralPath $publishDir, $layoutDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $publishDir, $layoutDir, $packageDir | Out-Null
Get-ChildItem -Path $packageDir -Filter "TypeWhisper-$StoreProduct-$RuntimeIdentifier-*.msix" -File |
    Remove-Item -Force

dotnet publish (Join-Path $repoRoot "src/TypeWhisper.WinUI/TypeWhisper.WinUI.csproj") `
    -c $Configuration `
    -r $RuntimeIdentifier `
    -p:Platform=$platform `
    --self-contained true `
    -p:Version=$msixVersion `
    -p:TypeWhisperStoreBuild=true `
    -p:TypeWhisperStoreProduct=$StoreProduct `
    -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed."
}

Copy-Item -Path (Join-Path $publishDir "*") -Destination $layoutDir -Recurse -Force

$layoutPluginsPath = Join-Path $layoutDir "Plugins"
if (Test-Path $layoutPluginsPath) {
    Remove-Item -LiteralPath $layoutPluginsPath -Recurse -Force
}

$layoutAssetsPath = Join-Path $layoutDir "Assets"
New-Item -ItemType Directory -Force -Path $layoutAssetsPath | Out-Null
Copy-Item -Path (Join-Path $assetsPath "*") -Destination $layoutAssetsPath -Recurse -Force

Set-Content -Path (Join-Path $layoutDir "AppxManifest.xml") -Value $manifest -Encoding UTF8

$makeAppx = Get-MakeAppxPath
& $makeAppx pack /d $layoutDir /p $packagePath /o
if ($LASTEXITCODE -ne 0) {
    throw "MakeAppx failed."
}

Write-Host "Created $packagePath"
