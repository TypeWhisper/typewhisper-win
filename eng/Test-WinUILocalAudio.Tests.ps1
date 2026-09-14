# Runs the real orchestration script with fake audio/network boundaries; no audio is captured or uploaded.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('local-audio-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
function dotnet {
    $global:LASTEXITCODE = 0
    if ($args -contains 'devices') {
        '[{"flow":"Capture","name":"Fixture mic","id":"mic","isDefault":true},{"flow":"Render","name":"Fixture speaker","id":"speaker","isDefault":true}]'
    }
    if ($args -contains 'synthesize') { [IO.File]::WriteAllText($args[[array]::IndexOf($args, 'synthesize') + 1], 'fake WAV') }
}
function Start-Sleep { param($Seconds, $Milliseconds) }
function Invoke-RestMethod {
    param($Method, $Uri, $Headers, $TimeoutSec, $ContentType, $Body)
    $path = ([uri]$Uri).AbsolutePath
    if ($path -eq '/v1/dictation/status') { return @{state='idle';is_recording=$false} }
    if ($path -eq '/v1/status') { return @{engine='original';model='original-model'} }
    if ($path -eq '/v1/models/load') {
        $value = $Body | ConvertFrom-Json
        if ($value.engine -eq 'original') {
            $audioFixtureState.calls.Add('restore')
            if ($audioFixtureState.recording) { throw 'Cannot restore while recording.' }
        } else {
            $audioFixtureState.calls.Add('load')
            if ($audioFixtureState.scenario -eq 'model-failure') { throw 'fixture model failure' }
        }
        return @{}
    }
    if ($path -eq '/v1/dictation/start') {
        $audioFixtureState.calls.Add('start')
        $audioFixtureState.recording = $true
        if ($audioFixtureState.scenario -eq 'start-timeout') { throw 'fixture start timeout after acceptance' }
        $start = $Body | ConvertFrom-Json
        if ($audioFixtureState.scenario -in @('workflow', 'workflow-mismatch', 'output-mismatch')) {
            if ($start.workflow_id -ne 'c96ec09c-d990-4cb0-b88e-7b3d6708c5bf') { throw 'Workflow was not sent.' }
            return @{id='fixture-session';workflow_id= $(if ($audioFixtureState.scenario -eq 'workflow-mismatch') { 'wrong' } else { $start.workflow_id })}
        }
        return @{id='fixture-session'}
    }
    if ($path -eq '/v1/dictation/stop') {
        $audioFixtureState.calls.Add('stop')
        if ($audioFixtureState.scenario -eq 'stop-failure' -and -not $audioFixtureState.stopFailed) {
            $audioFixtureState.stopFailed = $true
            throw 'fixture stop failure'
        }
        $audioFixtureState.recording = $false
        return @{status='stopped'}
    }
    if ($path -eq '/v1/dictation/transcription') {
        return @{status='completed';transcription=@{engine='fixture';model='fixture-model';app_name='Notepad';raw_text='Fixture sentence.';text='FIXTURE SENTENCE.'}}
    }
    throw "Unexpected API call: $path"
}
try {
    '{"AudioDuckingEnabled":false,"PauseMediaDuringRecording":false}' | Set-Content (Join-Path $fixture 'audio.json')
    '{"port":12345,"requires_authentication":false}' | Set-Content (Join-Path $fixture 'api-discovery.json')
    foreach ($scenario in @('success', 'start-timeout', 'model-failure', 'stop-failure', 'workflow', 'workflow-mismatch', 'output-mismatch')) {
        $audioFixtureState = @{scenario=$scenario;calls=[Collections.Generic.List[string]]::new();recording=$false;stopFailed=$false}
        $failure = $null
        $workflowArguments = @{}
        if ($scenario -in @('workflow', 'workflow-mismatch', 'output-mismatch')) {
            $workflowArguments.WorkflowId = 'c96ec09c-d990-4cb0-b88e-7b3d6708c5bf'
            $workflowArguments.ExpectedText = if ($scenario -eq 'output-mismatch') { 'DIFFERENT.' } else { 'FIXTURE SENTENCE.' }
        }
        try {
            & "$PSScriptRoot/Test-WinUILocalAudio.ps1" -Mode Run -Engine fixture -Model fixture-model `
                -OutputDeviceName 'Fixture speaker' -ProfilePath $fixture -OutputDirectory (Join-Path $fixture $scenario) `
                -Text 'Fixture sentence.' -FocusDelaySeconds 0 -TimeoutSeconds 10 @workflowArguments
        } catch { $failure = $_.Exception.Message }
        $expected = switch ($scenario) {
            success { 'load,start,stop,restore' }
            start-timeout { 'load,start,stop,restore' }
            model-failure { 'load,restore' }
            stop-failure { 'load,start,stop,stop,restore' }
            default { 'load,start,stop,restore' }
        }
        if (($audioFixtureState.calls -join ',') -ne $expected -or $audioFixtureState.recording) { throw "Incorrect cleanup for $scenario`: $($audioFixtureState.calls -join ','); failure=$failure" }
        if ($scenario -in @('success', 'workflow') -and $null -ne $failure) { throw $failure }
        if ($scenario -in @('start-timeout', 'model-failure', 'stop-failure') -and $failure -notlike 'fixture *') { throw "Expected fixture failure for $scenario; got $failure" }
        if ($scenario -eq 'workflow-mismatch' -and $failure -ne 'The host did not select the requested workflow.') { throw "Expected workflow mismatch; got $failure" }
        if ($scenario -eq 'output-mismatch' -and $failure -notlike 'Final workflow text differs*') { throw "Expected output mismatch; got $failure" }
    }
    Write-Host 'Seven audio/workflow orchestration lifecycle checks passed.'
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing cleanup outside the temporary directory.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
