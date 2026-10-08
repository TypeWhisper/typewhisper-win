# Exercise the real runner with fixture projects and a fake dotnet command.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('headless-suites-test-' + [guid]::NewGuid().ToString('N'))
$appProjects = @(
    'tests/TypeWhisper.Core.Tests/TypeWhisper.Core.Tests.csproj',
    'tests/TypeWhisper.PluginSDK.Portable.Tests/TypeWhisper.PluginSDK.Portable.Tests.csproj',
    'tests/TypeWhisper.Cli.Tests/TypeWhisper.Cli.Tests.csproj',
    'tests/TypeWhisper.Presentation.Tests/TypeWhisper.Presentation.Tests.csproj'
)
$windowsProjects = @(
    'tests/TypeWhisper.Platform.Tests/TypeWhisper.Platform.Tests.csproj',
    'tests/TypeWhisper.Dictation.AudioTests/TypeWhisper.Dictation.AudioTests.csproj'
)
$pluginProjects = @(
    'plugins/Fixture.Alpha/Tests/Fixture.Alpha.Tests.csproj',
    'plugins/Fixture.Beta/Tests/Fixture.Beta.Tests.csproj'
)
$expectedAppProjects = @($appProjects)
if ($IsWindows) { $expectedAppProjects += $windowsProjects }
$checks = 0

function dotnet {
    if ($args[0] -ne 'test' -or -not (Test-Path -LiteralPath $args[1] -PathType Leaf)) {
        throw "Unexpected dotnet invocation: $args"
    }
    $project = [IO.Path]::GetRelativePath($fixture, $args[1]).Replace('\', '/')
    $headlessFixtureState.calls.Add($project)
    $global:LASTEXITCODE = if ($project -in $headlessFixtureState.failures) { 1 } else { 0 }
}

function Expect-Run([string]$Suite, [string[]]$Expected, [string[]]$Failures = @()) {
    $headlessFixtureState = @{ calls = [Collections.Generic.List[string]]::new(); failures = $Failures }
    $resultsDirectory = Join-Path $fixture ('results-' + [guid]::NewGuid().ToString('N'))
    $arguments = @{ Configuration = 'Release'; ResultsDirectory = $resultsDirectory }
    if ($Suite) { $arguments.Suite = $Suite }
    $failure = $null
    try { & (Join-Path $fixture 'eng/Test-WinUIHeadless.ps1') @arguments }
    catch { $failure = $_.Exception.Message }

    if (($headlessFixtureState.calls -join '|') -cne ($Expected -join '|')) {
        throw "Wrong projects for '$Suite': $($headlessFixtureState.calls -join ', ')"
    }
    if ($Failures.Count -eq 0 -and $null -ne $failure) { throw $failure }
    if ($Failures.Count -gt 0 -and $failure -notlike 'Headless checks failed:*') {
        throw "The runner must report failed suites after running every project; got '$failure'."
    }
    $summary = Get-Content -LiteralPath (Join-Path $resultsDirectory 'summary.json') -Raw | ConvertFrom-Json
    $expectedSuite = if ($Suite) { $Suite } else { 'All' }
    if ($summary.suite -ne $expectedSuite -or $summary.configuration -ne 'Release' -or
        $summary.passed -ne ($Failures.Count -eq 0) -or $summary.checks.Count -ne $Expected.Count) {
        throw "Incorrect summary for '$Suite'."
    }
    $failedChecks = @($summary.checks | Where-Object exitCode -ne 0)
    if ($failedChecks.Count -ne $Failures.Count) { throw 'The summary lost a failed project.' }
    foreach ($failedCheck in $failedChecks) {
        if (-not $failure.Contains($failedCheck.name)) { throw "The error omitted $($failedCheck.name)." }
    }
    $script:checks++
}

try {
    New-Item -ItemType Directory -Path (Join-Path $fixture 'eng') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Test-WinUIHeadless.ps1') -Destination (Join-Path $fixture 'eng')
    foreach ($project in @($appProjects) + @($windowsProjects) + $pluginProjects + @('plugins/Fixture.Alpha/Fixture.Alpha.csproj')) {
        $path = Join-Path $fixture $project
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        Set-Content -LiteralPath $path -Value '<Project />'
    }
    Expect-Run '' ($expectedAppProjects + $pluginProjects)
    Expect-Run 'All' ($expectedAppProjects + $pluginProjects)
    Expect-Run 'App' $expectedAppProjects
    Expect-Run 'Plugins' $pluginProjects
    Expect-Run 'All' ($expectedAppProjects + $pluginProjects) @($appProjects[0], $pluginProjects[0])
    Expect-Run 'Plugins' $pluginProjects @($pluginProjects[0])
    Write-Host "$checks headless suite selection and failure-reporting checks passed."
} finally {
    $global:LASTEXITCODE = 0
    $resolved = [IO.Path]::GetFullPath($fixture)
    $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolved) -notlike 'headless-suites-test-*') { throw 'Unexpected fixture path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
