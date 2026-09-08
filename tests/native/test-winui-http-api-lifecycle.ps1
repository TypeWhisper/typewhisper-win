param([Parameter(Mandatory)][string]$SourcePath)
$ErrorActionPreference = 'Stop'
$profileRoot = Join-Path $env:TEMP 'TypeWhisper-WinUI-TestProfiles/http-api-20260908'
if (!(Test-Path (Join-Path $profileRoot 'PluginData'))) { throw 'Prepare the isolated local-model profile first (see HTTP API docs).' }
$settingsPath = Join-Path $profileRoot 'http-api.json'
$discoveryPath = Join-Path $profileRoot 'api-discovery.json'
$portPath = Join-Path $profileRoot 'api-port'
$helper = 'F:/typewhisper/typewhisper-dev-tools/build-typewhisper-windows-dev.ps1'
$evidence = [System.Collections.Generic.List[string]]::new()
function Launch-Test([string]$label) {
    Get-ChildItem Env:TYPEWHISPER_WINUI_* | Remove-Item
    $env:TYPEWHISPER_WINUI_TEST_PROFILE = 'http-api-20260908'
    & $helper --run --winui $SourcePath *> (Join-Path $SourcePath "artifacts/http-api-$label-build.log")
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $label" }
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    if ($label -eq 'enabled' -or $label -eq 'restarted') {
        while (!(Test-Path $discoveryPath) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200 }
        if (!(Test-Path $discoveryPath)) { throw "Discovery did not appear: $label" }
    } else {
        while (((Test-Path $discoveryPath) -or (Test-Path $portPath)) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200 }
        if ((Test-Path $discoveryPath) -or (Test-Path $portPath)) { throw "Stale discovery remains: $label" }
    }
}
try {
    [IO.File]::WriteAllText($settingsPath, '{"Enabled":true,"Port":18978}')
    Launch-Test 'enabled'
    $first = Get-Content $discoveryPath -Raw | ConvertFrom-Json
    if ($first.version -ne 1 -or $first.port -ne 18978 -or !$first.token) { throw 'Invalid discovery contract' }
    $acl = Get-Acl -LiteralPath $discoveryPath
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    if (!$acl.AreAccessRulesProtected -or $acl.Access.Count -ne 1 -or $acl.Access[0].IdentityReference.Value -ne $identity) { throw 'Discovery is not owner-only' }
    $evidence.Add('Mac discovery contract and owner-only ACL passed')
    [IO.File]::WriteAllText($settingsPath, '{"Enabled":false,"Port":18978}')
    Launch-Test 'disabled'
    $evidence.Add('Disable/startup removes both discovery files')
    [IO.File]::WriteAllText($settingsPath, 'malformed')
    [IO.File]::WriteAllText($discoveryPath, '{"version":1,"port":18978,"token":"synthetic stale discovery"}')
    [IO.File]::WriteAllText($portPath, '18978')
    Launch-Test 'corrupt'
    $evidence.Add('Corrupt settings remove stale discovery and fail closed')
    [IO.File]::WriteAllText($settingsPath, '{"Enabled":true,"Port":18978}')
    Launch-Test 'restarted'
    $second = Get-Content $discoveryPath -Raw | ConvertFrom-Json
    if ($second.token -ne $first.token) { throw 'Token did not survive restart' }
    $evidence.Add('DPAPI token survives restart')
    python (Join-Path $SourcePath 'tests/native/test_winui_http_api.py') --profile $profileRoot --audio (Join-Path $SourcePath 'artifacts/ui-fixtures/file-speech-en.wav')
    if ($LASTEXITCODE -ne 0) { throw 'Real transcription API checks failed' }
    $evidence.Add('Real local-engine HTTP acceptance passed')
    $evidence | ConvertTo-Json | Set-Content (Join-Path $SourcePath 'artifacts/http-api-lifecycle-evidence.json') -Encoding utf8
} finally {
    Get-ChildItem Env:TYPEWHISPER_WINUI_* | Remove-Item
    & $helper --run --winui $SourcePath *> (Join-Path $SourcePath 'artifacts/http-api-normal-build.log')
}
