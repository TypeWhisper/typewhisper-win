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
# Plugin tests are restored and built once through a generated solution before any of them runs.
$pluginBuild = @("restore $($pluginProjects -join ' ')", "build $($pluginProjects -join ' ')")
$checks = 0

function dotnet {
    $command = $args[0]
    $target = $args[1]
    if ($command -in @('restore', 'build')) {
        if ((Split-Path -Leaf $target) -ne 'PluginTests.slnx' -or -not (Test-Path -LiteralPath $target -PathType Leaf)) {
            throw "Unexpected dotnet invocation: $args"
        }
        if ($command -eq 'build' -and ($args -notcontains '--no-restore' -or $args -notcontains 'Release' -or
            $args -notcontains '-p:ShouldUnsetParentConfigurationAndPlatform=false')) {
            throw "The shared build must reuse the restore and keep the configuration for referenced plugin projects: $args"
        }
        $solutionDirectory = Split-Path -Parent $target
        $projects = @(([xml](Get-Content -LiteralPath $target -Raw)).Solution.Project | ForEach-Object {
            [IO.Path]::GetRelativePath($fixture, [IO.Path]::GetFullPath((Join-Path $solutionDirectory $_.Path))).Replace('\', '/')
        })
        $headlessFixtureState.calls.Add("$command $($projects -join ' ')")
        $global:LASTEXITCODE = if ($headlessFixtureState.buildFailure -eq $command) { 1 } else { 0 }
        return
    }
    if ($command -ne 'test' -or -not (Test-Path -LiteralPath $target -PathType Leaf)) {
        throw "Unexpected dotnet invocation: $args"
    }
    $project = [IO.Path]::GetRelativePath($fixture, $target).Replace('\', '/')
    if (($args -contains '--no-build') -ne $project.StartsWith('plugins/')) {
        throw "Plugin tests must reuse the shared build and app tests must build themselves: $args"
    }
    $expectedCoverage = $headlessFixtureState.coverage -and $project -in @($appProjects[0], $appProjects[3])
    if (($args -contains '--collect') -ne $expectedCoverage) { throw "Unexpected coverage instrumentation for $project" }
    $headlessFixtureState.calls.Add($project)
    $global:LASTEXITCODE = if ($project -in $headlessFixtureState.failures) { 1 } else { 0 }
}

function Expect-Run([string]$Suite, [string[]]$Expected, [string[]]$Failures = @(), [string]$BuildFailure = '', [bool]$Coverage = $false) {
    $headlessFixtureState = @{ calls = [Collections.Generic.List[string]]::new(); failures = $Failures; buildFailure = $BuildFailure; coverage = $Coverage }
    $resultsDirectory = Join-Path $fixture ('results-' + [guid]::NewGuid().ToString('N'))
    $arguments = @{ Configuration = 'Release'; ResultsDirectory = $resultsDirectory }
    if ($Suite) { $arguments.Suite = $Suite }
    if ($Coverage) { $arguments.CollectCoverage = $true }
    $failure = $null
    try { & (Join-Path $fixture 'eng/Test-WinUIHeadless.ps1') @arguments }
    catch { $failure = $_.Exception.Message }

    if (($headlessFixtureState.calls -join '|') -cne ($Expected -join '|')) {
        throw "Wrong dotnet calls for '$Suite': $($headlessFixtureState.calls -join ', ')"
    }
    $expectsFailure = $Failures.Count -gt 0 -or $BuildFailure -ne ''
    if (-not $expectsFailure -and $null -ne $failure) { throw $failure }
    if ($BuildFailure -and $failure -notlike 'Plugin test build failed*') {
        throw "The runner must report a failed plugin build without running plugin tests; got '$failure'."
    }
    if (-not $BuildFailure -and $Failures.Count -gt 0 -and $failure -notlike 'Headless checks failed:*') {
        throw "The runner must report failed suites after running every project; got '$failure'."
    }
    $summary = Get-Content -LiteralPath (Join-Path $resultsDirectory 'summary.json') -Raw | ConvertFrom-Json
    $expectedSuite = if ($Suite) { $Suite } else { 'All' }
    $expectedChecks = @($Expected | Where-Object { $_ -notmatch '^(restore|build) ' }).Count
    if ($summary.suite -ne $expectedSuite -or $summary.configuration -ne 'Release' -or
        $summary.passed -ne (-not $expectsFailure) -or $summary.checks.Count -ne $expectedChecks) {
        throw "Incorrect summary for '$Suite'."
    }
    $sharedBuild = @($Expected | Where-Object { $_ -like 'restore *' }).Count -gt 0
    if ($sharedBuild -ne ($null -ne $summary.pluginBuild)) { throw "The summary must record the plugin build only when plugins ran ('$Suite')." }
    if ($sharedBuild -and ($summary.pluginBuild.exitCode -ne [int][bool]$BuildFailure -or
        -not $summary.pluginBuild.solution.EndsWith('PluginTests.slnx'))) {
        throw "Incorrect plugin build summary for '$Suite'."
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
    Expect-Run '' ($expectedAppProjects + $pluginBuild + $pluginProjects)
    Expect-Run 'All' ($expectedAppProjects + $pluginBuild + $pluginProjects)
    Expect-Run 'App' $expectedAppProjects
    Expect-Run 'All' ($expectedAppProjects + $pluginBuild + $pluginProjects) -Coverage $true
    Expect-Run 'Plugins' ($pluginBuild + $pluginProjects)
    Expect-Run 'All' ($expectedAppProjects + $pluginBuild + $pluginProjects) @($appProjects[0], $pluginProjects[0])
    Expect-Run 'Plugins' ($pluginBuild + $pluginProjects) @($pluginProjects[0])
    Expect-Run 'Plugins' @($pluginBuild[0]) -BuildFailure 'restore'
    Expect-Run 'All' ($expectedAppProjects + $pluginBuild) @($appProjects[0]) -BuildFailure 'build'
    Write-Host "$checks headless suite selection and failure-reporting checks passed."
} finally {
    $global:LASTEXITCODE = 0
    $resolved = [IO.Path]::GetFullPath($fixture)
    $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolved) -notlike 'headless-suites-test-*') { throw 'Unexpected fixture path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
