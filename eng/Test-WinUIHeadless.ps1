param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$ResultsDirectory,
    [ValidateSet('All', 'App', 'Plugins')]
    [string]$Suite = 'All'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repository = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $ResultsDirectory = Join-Path $repository 'artifacts/test-results/winui-headless'
}
$ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null
$appChecks = @()
if ($Suite -in @('All', 'App')) {
    $appChecks += @(
        @{ Name = 'Core'; Project = 'tests/TypeWhisper.Core.Tests/TypeWhisper.Core.Tests.csproj' },
        @{ Name = 'PluginHost'; Project = 'tests/TypeWhisper.PluginSDK.Portable.Tests/TypeWhisper.PluginSDK.Portable.Tests.csproj' },
        @{ Name = 'CLI'; Project = 'tests/TypeWhisper.Cli.Tests/TypeWhisper.Cli.Tests.csproj' },
        @{ Name = 'Presentation'; Project = 'tests/TypeWhisper.Presentation.Tests/TypeWhisper.Presentation.Tests.csproj' }
    )
    if ($IsWindows) {
        $appChecks += @{ Name = 'Platform'; Project = 'tests/TypeWhisper.Platform.Tests/TypeWhisper.Platform.Tests.csproj' }
    }
}
$pluginChecks = @()
if ($Suite -in @('All', 'Plugins')) {
    # Each portable plugin owns its tests; discovery needs no host-side provider list.
    $pluginChecks += @(Get-ChildItem -Path (Join-Path $repository 'plugins/*/Tests/*.csproj') | Sort-Object FullName | ForEach-Object {
        @{ Name = $_.BaseName; Project = [IO.Path]::GetRelativePath($repository, $_.FullName) }
    })
}
$results = [System.Collections.Generic.List[object]]::new()
function Test-Project([hashtable]$Check, [string[]]$Options = @()) {
    $started = [DateTimeOffset]::UtcNow
    & dotnet test (Join-Path $repository $Check.Project) -c $Configuration @Options --verbosity minimal --logger "trx;LogFileName=$($Check.Name).trx" --results-directory $ResultsDirectory
    $results.Add([pscustomobject]@{ name = $Check.Name; exitCode = $LASTEXITCODE; durationSeconds = ([DateTimeOffset]::UtcNow - $started).TotalSeconds })
}
foreach ($check in $appChecks) { Test-Project $check }
$build = $null
if ($pluginChecks.Count -gt 0) {
    # Restore and build every plugin test project through one generated solution, so the shared SDK and
    # host projects compile once instead of once per plugin. The tests then reuse that output.
    $solution = Join-Path $ResultsDirectory 'PluginTests.slnx'
    $entries = foreach ($check in $pluginChecks) {
        $relative = [IO.Path]::GetRelativePath($ResultsDirectory, (Join-Path $repository $check.Project)).Replace('\', '/')
        "  <Project Path=""$relative"" />"
    }
    Set-Content -LiteralPath $solution -Value (@('<Solution>') + @($entries) + @('</Solution>'))
    $started = [DateTimeOffset]::UtcNow
    & dotnet restore $solution
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0) {
        # Projects referenced from outside a solution otherwise drop the solution's configuration and
        # build as Debug, which loses the Release portable-host output the package tests load.
        & dotnet build $solution -c $Configuration --no-restore --verbosity minimal '-p:ShouldUnsetParentConfigurationAndPlatform=false'
        $exitCode = $LASTEXITCODE
    }
    $build = [pscustomobject]@{ solution = $solution; exitCode = $exitCode; durationSeconds = ([DateTimeOffset]::UtcNow - $started).TotalSeconds }
    if ($exitCode -eq 0) {
        foreach ($check in $pluginChecks) { Test-Project $check @('--no-build') }
    }
}
$failed = @($results | Where-Object { $_.exitCode -ne 0 })
$buildFailed = $null -ne $build -and $build.exitCode -ne 0
[pscustomobject]@{
    passed = -not $buildFailed -and $failed.Count -eq 0
    configuration = $Configuration
    suite = $Suite
    checks = $results.ToArray()
    pluginBuild = $build
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'summary.json')
$problems = @()
if ($buildFailed) { $problems += "Plugin test build failed (exit code $($build.exitCode))" }
if ($failed.Count -gt 0) { $problems += "Headless checks failed: $($failed.name -join ', ')" }
if ($problems.Count -gt 0) { throw "$($problems -join '. '). See $ResultsDirectory" }
Write-Host "Headless checks passed. Results: $ResultsDirectory"
