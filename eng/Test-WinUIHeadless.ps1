param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$ResultsDirectory
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
$checks = @(
    @{ Name = 'PluginHost'; Project = 'tests/TypeWhisper.PluginSDK.Portable.Tests/TypeWhisper.PluginSDK.Portable.Tests.csproj' },
    @{ Name = 'Presentation'; Project = 'tests/TypeWhisper.Presentation.Tests/TypeWhisper.Presentation.Tests.csproj' }
)
# Each portable plugin owns its tests; discovery needs no host-side provider list.
$checks += @(Get-ChildItem -Path (Join-Path $repository 'plugins/*/Tests/*.csproj') | ForEach-Object {
    @{ Name = $_.BaseName; Project = [IO.Path]::GetRelativePath($repository, $_.FullName) }
})
$results = [System.Collections.Generic.List[object]]::new()
foreach ($check in $checks) {
    $project = Join-Path $repository $check.Project
    $started = [DateTimeOffset]::UtcNow
    & dotnet test $project -c $Configuration --verbosity minimal --logger "trx;LogFileName=$($check.Name).trx" --results-directory $ResultsDirectory
    $exitCode = $LASTEXITCODE
    $results.Add([pscustomobject]@{ name = $check.Name; exitCode = $exitCode; durationSeconds = ([DateTimeOffset]::UtcNow - $started).TotalSeconds })
}
$failed = @($results | Where-Object { $_.exitCode -ne 0 })
[pscustomobject]@{
    passed = $failed.Count -eq 0
    configuration = $Configuration
    checks = $results.ToArray()
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'summary.json')
if ($failed.Count -gt 0) { throw "Headless checks failed: $($failed.name -join ', '). See $ResultsDirectory" }
Write-Host "Headless checks passed. Results: $ResultsDirectory"
