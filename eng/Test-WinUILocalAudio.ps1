<#
.SYNOPSIS
Prepare or run a physical speaker-to-microphone dictation smoke test against a running WinUI dev host.
.DESCRIPTION
See docs/WINUI-LOCAL-AUDIO-TEST.md. Run sends microphone audio to the selected provider
and may incur API charges. Focus a disposable text document before recording starts.
This script verifies transcription; paste and screenshots require the documented UI check.
#>
[CmdletBinding()]
param(
    [ValidateSet('Devices', 'Prepare', 'Run')][string]$Mode = 'Devices',
    [string]$Engine,
    [string]$Model,
    [string]$OutputDeviceName,
    [string]$ProfilePath = (Join-Path $env:LOCALAPPDATA 'TypeWhisper-WinUI-DevUserData'),
    [string]$OutputDirectory,
    [ValidateLength(1, 200)][string]$Text = 'The yellow bicycle is parked beside the garden. This is a short transcription test.',
    [ValidateRange(0, 30)][int]$FocusDelaySeconds = 5,
    [ValidateRange(10, 180)][int]$TimeoutSeconds = 90
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'tools/TypeWhisper.AudioSmoke/TypeWhisper.AudioSmoke.csproj'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot ('artifacts/local-audio/' + [guid]::NewGuid().ToString('N')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new output directory to preserve previous evidence.' }
& dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Audio helper build failed.' }
function Invoke-AudioHelper([string[]]$Arguments) {
    $result = & dotnet run --no-build --project $project -c Release -- @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Audio helper failed.' }
    return $result
}
$devices = @(Invoke-AudioHelper @('devices') | ConvertFrom-Json)
if ($Mode -eq 'Devices') { $devices | Format-Table flow, name, isDefault, id; return }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$wave = Join-Path $OutputDirectory 'speech-en.wav'
Invoke-AudioHelper @('synthesize', $wave, $Text)
if ($Mode -eq 'Prepare') { Write-Host "Prepared $wave"; return }
if (-not $Engine -or -not $Model -or -not $OutputDeviceName) { throw 'Run requires Engine, Model and the exact OutputDeviceName.' }
$output = @($devices | Where-Object { $_.flow -eq 'Render' -and $_.name -eq $OutputDeviceName })
if ($output.Count -ne 1) { throw 'The output must match exactly one active render endpoint. Use -Mode Devices.' }
if ($output[0].name -eq 'Remote Audio' -or @($devices | Where-Object { $_.flow -eq 'Capture' -and $_.name -ne 'Remote Audio' }).Count -eq 0) {
    throw 'Physical audio endpoints are required. Transfer the session to the local console first.'
}
$audio = Get-Content -LiteralPath (Join-Path $ProfilePath 'audio.json') -Raw | ConvertFrom-Json
if ($audio.AudioDuckingEnabled -or $audio.PauseMediaDuringRecording) { throw 'Temporarily disable Lower audio while recording and Pause media during recording in the dev UI; restore them after the test.' }
$discovery = Get-Content -LiteralPath (Join-Path $ProfilePath 'api-discovery.json') -Raw | ConvertFrom-Json
$port = [int]$discovery.port
if ($port -lt 1 -or $port -gt 65535) { throw 'Invalid local API port.' }
$base = 'http://127.0.0.1:' + $port
$headers = @{}
if ($discovery.requires_authentication) { $headers.Authorization = 'Bearer ' + $discovery.token }
function Invoke-LocalApi([string]$Method, [string]$Path, $Body = $null) {
    $arguments = @{ Method = $Method; Uri = $base + $Path; Headers = $headers; TimeoutSec = $TimeoutSeconds }
    if ($null -ne $Body) { $arguments.ContentType = 'application/json'; $arguments.Body = $Body | ConvertTo-Json -Compress }
    Invoke-RestMethod @arguments
}
$state = Invoke-LocalApi GET '/v1/dictation/status'
if ($state.state -ne 'idle' -or $state.is_recording) { throw 'Finish the current dictation before running this test.' }
$original = Invoke-LocalApi GET '/v1/status'
if (-not $original.engine -or -not $original.model) { throw 'Select a ready original model so the script can restore it.' }
$started = $null
$startAttempted = $false
$stopped = $false
try {
    Invoke-LocalApi POST '/v1/models/load' @{ engine = $Engine; model = $Model } | Out-Null
    Write-Host "Focus a blank Notepad document. Recording starts in $FocusDelaySeconds seconds."
    Start-Sleep -Seconds $FocusDelaySeconds
    $startAttempted = $true
    $started = Invoke-LocalApi POST '/v1/dictation/start' @{}
    try {
        Start-Sleep -Milliseconds 700
        Invoke-AudioHelper @('play', $output[0].id, $wave)
        Start-Sleep -Milliseconds 900
    } finally {
        Invoke-LocalApi POST '/v1/dictation/stop' | Out-Null
        $stopped = $true
    }
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $result = Invoke-LocalApi GET ('/v1/dictation/transcription?id=' + [uri]::EscapeDataString($started.id))
        if ($result.status -in @('completed', 'failed')) { break }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'result.json') -Encoding utf8
    if ($result.status -ne 'completed') { throw 'Dictation failed or timed out; inspect result.json.' }
    if ($result.transcription.engine -ne $Engine -or $result.transcription.model -ne $Model) { throw 'A workflow overrode the requested provider/model.' }
    if ($result.transcription.app_name -ne 'Notepad') { throw 'The captured target was not Notepad; inspect the target before continuing.' }
    if ($result.transcription.raw_text.Trim() -cne $Text.Trim()) { throw 'Raw transcription differs from the synthesized sentence; inspect result.json.' }
    Write-Host "Raw transcription matched. Verify the pasted text in Notepad and capture a screenshot. Evidence: $OutputDirectory"
} finally {
    if ($startAttempted -and -not $stopped) {
        try { Invoke-LocalApi POST '/v1/dictation/stop' | Out-Null } catch { Write-Warning 'Stop failed; check the recording status in the dev app.' }
    }
    # The server rejects a selection change while processing; retry for a bounded interval.
    $restoreDeadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            Invoke-LocalApi POST '/v1/models/load' @{ engine = $original.engine; model = $original.model } | Out-Null
            $restored = $true
            break
        } catch { $restored = $false; Start-Sleep -Seconds 1 }
    } while ([DateTime]::UtcNow -lt $restoreDeadline)
    if (-not $restored) { throw "Restore failed. Select $($original.engine) / $($original.model) manually in the dev app." }
}
