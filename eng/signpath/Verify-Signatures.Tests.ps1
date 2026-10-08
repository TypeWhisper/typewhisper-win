Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or $env:GITHUB_ACTIONS -ne 'true') {
    throw 'Run these certificate fixtures on a disposable Windows GitHub Actions runner.'
}

$temporary = Join-Path ([IO.Path]::GetTempPath()) ('signpath-signature-test-' + [Guid]::NewGuid())
New-Item -ItemType Directory -Path $temporary | Out-Null
$certificate = $null
try {
    # Keep the fixture out of Root/TrustedPublisher; its untrusted chain is the
    # behavior under test. The private key is temporary and never exported.
    $certificate = New-SelfSignedCertificate -Type CodeSigningCert `
        -Subject 'CN=TypeWhisper signing verification test' `
        -CertStoreLocation 'Cert:\CurrentUser\My' -KeyExportPolicy NonExportable `
        -NotAfter (Get-Date).AddDays(1)
    $fixture = Join-Path $temporary 'fixture.ps1'
    Set-Content -LiteralPath $fixture -Value "Write-Output 'signed fixture'" -Encoding utf8BOM
    Set-AuthenticodeSignature -LiteralPath $fixture -Certificate $certificate | Out-Null
    $manifest = Join-Path $temporary 'files.json'
    ConvertTo-Json -InputObject @($fixture) | Set-Content -LiteralPath $manifest
    $verifier = Join-Path $PSScriptRoot 'Verify-Signatures.ps1'

    function Assert-Rejected([string]$Policy, [string]$Thumbprint, [string]$Reason) {
        $rejected = $false
        try { & $verifier -FilesJson $manifest -Policy $Policy -Thumbprint $Thumbprint }
        catch { $rejected = $true }
        if (-not $rejected) { throw "Signature verification accepted $Reason." }
        Write-Host "Correctly rejected $Reason."
    }

    if ((Get-AuthenticodeSignature -LiteralPath $fixture).Status -ne 'UnknownError') {
        throw 'Expected the fixture to exercise the untrusted-root path.'
    }
    & $verifier -FilesJson $manifest -Policy test-signing -Thumbprint $certificate.Thumbprint
    Assert-Rejected release-signing $certificate.Thumbprint 'a test certificate in production'
    Assert-Rejected test-signing ('0' * 40) 'an unexpected signing certificate'

    $changed = [IO.File]::ReadAllText($fixture).Replace('signed fixture', 'changed fixture')
    [IO.File]::WriteAllText($fixture, $changed, [Text.UTF8Encoding]::new($true))
    if ((Get-AuthenticodeSignature -LiteralPath $fixture).Status -ne 'HashMismatch') {
        throw 'Expected the modified fixture to have a cryptographic hash mismatch.'
    }
    Assert-Rejected test-signing $certificate.Thumbprint 'a modified signed file'
    Write-Host 'Authenticode verification fixtures passed.'
} finally {
    if ($null -ne $certificate) {
        Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($certificate.Thumbprint)" -DeleteKey -Force
    }
    $resolvedTemporary = [IO.Path]::GetFullPath($temporary)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedTemporary.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid fixture directory.' }
    Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
}
