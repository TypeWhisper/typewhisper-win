param(
    [ValidateSet('stable', 'beta')]
    [string]$StoreProduct = 'stable'
)

$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot '../src/TypeWhisper.Windows.StorePackage/StoreProducts.json'
$products = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
$products.$StoreProduct
