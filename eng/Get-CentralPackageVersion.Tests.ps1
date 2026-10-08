Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$reader = Join-Path $PSScriptRoot 'Get-CentralPackageVersion.ps1'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('package-versions-' + [guid]::NewGuid().ToString('N') + '.props')
try {
    foreach ($version in @('0.0.1298', '1.2.3-preview.4')) {
        Set-Content -LiteralPath $fixture -Value "<Project><PropertyGroup><TrayVersion>$version</TrayVersion></PropertyGroup><ItemGroup><PackageVersion Include='Velopack' Version='$version'/><PackageVersion Include='H.NotifyIcon' Version='`$(TrayVersion)'/></ItemGroup></Project>"
        foreach ($id in @('Velopack', 'H.NotifyIcon')) {
            if ((& $reader -PackageId $id -ManifestPath $fixture) -cne $version) { throw "Version update was not propagated for $id." }
        }
    }
    foreach ($invalid in @('1.*', '[1.2.3,2.0.0)', '$(Missing)', '')) {
        Set-Content -LiteralPath $fixture -Value "<Project><ItemGroup><PackageVersion Include='Velopack' Version='$invalid'/></ItemGroup></Project>"
        $rejected = $false
        try { & $reader -PackageId Velopack -ManifestPath $fixture | Out-Null } catch { $rejected = $true }
        if (-not $rejected) { throw "Accepted invalid version '$invalid'." }
    }
} finally { if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture } }

$repository = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repository 'src/TypeWhisper.WinUI/TypeWhisper.WinUI.csproj'
# Evaluation only: no restore, download, build or app launch. Simulate a future tray update.
$evaluated = & dotnet msbuild $project -nologo -getItem:PackageVersion,PackageDownload,Reference '-p:HNotifyIconVersion=9.8.7'
if ($LASTEXITCODE -ne 0) { throw 'Could not evaluate tray package bindings.' }
$items = ($evaluated -join "`n" | ConvertFrom-Json).Items
if (($items.PackageVersion | Where-Object Identity -eq 'H.NotifyIcon').Version -cne '9.8.7') { throw 'The tray package bypasses its central version property.' }
if (($items.PackageDownload | Where-Object Identity -eq 'H.NotifyIcon.WinUI').Version -cne '[9.8.7]') { throw 'The tray bridge download is not coupled to the tray package.' }
if (($items.Reference | Where-Object Identity -eq 'H.NotifyIcon.WinUI').HintPath -notmatch 'h.notifyicon.winui[/\\]9\.8\.7[/\\]') { throw 'The tray bridge assembly path is not coupled to the tray package.' }

$velopack = & $reader -PackageId Velopack
foreach ($name in @('package-dry-run.yml', 'winui-daily-candidate.yml')) {
    $workflow = Get-Content -Raw -LiteralPath (Join-Path $repository ".github/workflows/$name")
    if ($workflow -notmatch 'Get-CentralPackageVersion.ps1 -PackageId Velopack' -or $workflow -match 'vpk --version [0-9]') {
        throw "$name does not install vpk from the central package version."
    }
}
Write-Host "Central package version checks passed (Velopack $velopack; simulated tray update 9.8.7)."
