[CmdletBinding()]
param([string]$Path = (Join-Path $PSScriptRoot '../.github/premium-win.ref'))
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# The pin file holds exactly one full commit SHA of the private modules repository; official builds check out that commit.
if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "The modules pin $Path is missing." }
$ref = [string](Get-Content -LiteralPath $Path -Raw) -replace '\r?\n\z', ''
if ($ref -cnotmatch '\A[0-9a-f]{40}\z') { throw "The modules pin $Path must contain one full lowercase commit SHA and nothing else." }
if ($ref -eq ('0' * 40)) { throw "The modules pin $Path still holds the placeholder SHA; set it to a commit of the private modules repository." }
return $ref
