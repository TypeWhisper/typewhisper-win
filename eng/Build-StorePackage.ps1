param(
    [string]$Version = $env:VERSION,
    [ValidateSet("win-x64", "win-arm64")]
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Configuration = "Release",
    [string]$OutputRoot = "artifacts/store"
)

$ErrorActionPreference = "Stop"

function Convert-ToMsixVersion([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) {
        return "0.0.1.0"
    }

    $main = ($value -split "[-+]")[0]
    $parts = @($main -split "\.")
    while ($parts.Count -lt 4) {
        $parts += "0"
    }

    $numeric = $parts[0..3] | ForEach-Object {
        if ($_ -notmatch '^\d+$') {
            throw "MSIX version '$value' must start with numeric version segments."
        }

        $segment = [int]$_
        if ($segment -lt 0 -or $segment -gt 65535) {
            throw "MSIX version segment '$_' must be in the range 0-65535."
        }

        $segment
    }

    return ($numeric -join ".")
}

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
$msixVersion = Convert-ToMsixVersion $Version
$architecture = switch ($RuntimeIdentifier) {
    "win-arm64" { "arm64" }
    default { "x64" }
}

$outputRootPath = Join-Path $repoRoot $OutputRoot
$publishDir = Join-Path $outputRootPath "publish/$RuntimeIdentifier"
$layoutDir = Join-Path $outputRootPath "layout/$RuntimeIdentifier"
$packageDir = Join-Path $outputRootPath "packages"
$packagePath = Join-Path $packageDir "TypeWhisper-$RuntimeIdentifier-$msixVersion.msix"
$templatePath = Join-Path $repoRoot "src/TypeWhisper.Windows.StorePackage/Package.appxmanifest.template"
$assetsPath = Join-Path $repoRoot "src/TypeWhisper.Windows.StorePackage/Assets"

Remove-Item -LiteralPath $publishDir, $layoutDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $publishDir, $layoutDir, $packageDir | Out-Null
Get-ChildItem -Path $packageDir -Filter "TypeWhisper-$RuntimeIdentifier-*.msix" -File |
    Remove-Item -Force

dotnet publish (Join-Path $repoRoot "src/TypeWhisper.Windows/TypeWhisper.Windows.csproj") `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --self-contained true `
    -p:Version=$Version `
    -p:TypeWhisperStoreBuild=true `
    -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed."
}

Copy-Item -Path (Join-Path $publishDir "*") -Destination $layoutDir -Recurse -Force

$layoutPluginsPath = Join-Path $layoutDir "Plugins"
if (Test-Path $layoutPluginsPath) {
    Remove-Item -LiteralPath $layoutPluginsPath -Recurse -Force
}

Copy-Item -Path $assetsPath -Destination (Join-Path $layoutDir "Assets") -Recurse -Force

$manifest = Get-Content -Raw $templatePath
$manifest = $manifest.Replace("__VERSION__", $msixVersion).Replace("__ARCHITECTURE__", $architecture)
Set-Content -Path (Join-Path $layoutDir "AppxManifest.xml") -Value $manifest -Encoding UTF8

$makeAppx = Get-MakeAppxPath
& $makeAppx pack /d $layoutDir /p $packagePath /o
if ($LASTEXITCODE -ne 0) {
    throw "MakeAppx failed."
}

Write-Host "Created $packagePath"
