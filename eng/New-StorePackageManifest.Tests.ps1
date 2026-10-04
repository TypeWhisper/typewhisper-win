Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$checks = 0
foreach ($product in @('stable', 'beta')) {
    foreach ($rid in @('win-x64', 'win-arm64')) {
        $manifest = & (Join-Path $PSScriptRoot 'New-StorePackageManifest.ps1') -Version '1.1.1.0' -StoreProduct $product -RuntimeIdentifier $rid
        [xml]$xml = $manifest
        $name = if ($product -eq 'beta') { 'TypeWhisper.TypeWhisperBeta' } else { 'TypeWhisper.TypeWhisper' }
        $displayName = if ($product -eq 'beta') { 'TypeWhisper Beta' } else { 'TypeWhisper' }
        $architecture = if ($rid -eq 'win-arm64') { 'arm64' } else { 'x64' }
        if ($xml.Package.Identity.Name -cne $name -or $xml.Package.Identity.Version -cne '1.1.1.0' -or
            $xml.Package.Identity.ProcessorArchitecture -cne $architecture -or
            $xml.Package.Properties.DisplayName -cne $displayName -or
            $xml.Package.Applications.Application.VisualElements.DisplayName -cne $displayName -or
            $xml.Package.Applications.Application.Executable -cne 'TypeWhisper.exe' -or
            $xml.Package.Dependencies.TargetDeviceFamily.MinVersion -cne '10.0.26100.0' -or
            $manifest -match '__[A-Z_]+__') {
            throw "Incorrect manifest for $product / $rid."
        }
        $checks++
    }
}
Write-Host "$checks Store product/architecture manifest checks passed."
