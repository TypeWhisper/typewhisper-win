Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('modules-ref-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$pin = Join-Path $fixture 'premium-win.ref'
$sha = '0123456789abcdef0123456789abcdef01234567'
$checks = 0
function Expect-Rejection($Content, [string]$Message) {
    if ($null -ne $Content) { [IO.File]::WriteAllText($pin, $Content) } else { Remove-Item -LiteralPath $pin -ErrorAction SilentlyContinue }
    $failure = $null
    try { & "$PSScriptRoot/Get-ModulesRef.ps1" -Path $pin | Out-Null } catch { $failure = $_.Exception.Message }
    if ($null -eq $failure -or $failure -notlike "*$Message*") { throw "Expected '$Message'; received '$failure'" }
}
try {
    Expect-Rejection $null 'is missing'; $checks++
    foreach ($content in @('', "`n", $sha.Substring(0, 39), $sha.ToUpperInvariant(), "$sha`n$sha", "# pin`n$sha", " $sha", "$sha ", 'main')) {
        Expect-Rejection $content 'one full lowercase commit SHA'; $checks++
    }
    Expect-Rejection ('0' * 40) 'placeholder SHA'; $checks++
    Expect-Rejection (('0' * 40) + "`n") 'placeholder SHA'; $checks++
    foreach ($content in @($sha, "$sha`n", "$sha`r`n")) {
        [IO.File]::WriteAllText($pin, $content)
        $ref = & "$PSScriptRoot/Get-ModulesRef.ps1" -Path $pin
        if ($ref -cne $sha) { throw "Expected $sha; received '$ref'" }
        $checks++
    }
    Write-Host "$checks modules pin checks passed."
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolved) -notlike 'modules-ref-test-*') { throw 'Unexpected fixture path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
