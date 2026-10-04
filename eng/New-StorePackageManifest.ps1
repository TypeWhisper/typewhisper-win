param(
    [string]$Version,
    [ValidateSet('stable', 'beta')]
    [string]$StoreProduct = 'stable',
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$versionNumber = & (Join-Path $PSScriptRoot 'Get-StorePackageVersion.ps1') -Version $Version
$identity = & (Join-Path $PSScriptRoot 'Get-StorePackageIdentity.ps1') -StoreProduct $StoreProduct
$architecture = if ($RuntimeIdentifier -eq 'win-arm64') { 'arm64' } else { 'x64' }
$template = Join-Path $PSScriptRoot '../src/TypeWhisper.Windows.StorePackage/Package.appxmanifest.template'
$manifest = Get-Content -LiteralPath $template -Raw
$manifest.Replace('__VERSION__', $versionNumber).Replace('__ARCHITECTURE__', $architecture).
    Replace('__IDENTITY_NAME__', $identity.packageIdentityName).
    Replace('__PUBLISHER__', $identity.packagePublisher).
    Replace('__PUBLISHER_DISPLAY_NAME__', $identity.publisherDisplayName).
    Replace('__DISPLAY_NAME__', $identity.displayName)
