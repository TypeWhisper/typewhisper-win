Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('plugin-selection-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$checks = 0

function Invoke-FixtureGit([string[]]$Arguments) {
    $result = @(& git -C $fixture @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "Fixture git failed: $($Arguments -join ' ')" }
    $result
}
function Write-Fixture([string]$Path, [string]$Text = 'fixture') {
    $target = Join-Path $fixture $Path
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    [IO.File]::WriteAllText($target, $Text)
}
function Commit-Fixture {
    Invoke-FixtureGit @('add', '--all') | Out-Null
    Invoke-FixtureGit @('-c', 'user.name=Plugin Selection Test', '-c', 'user.email=ci@example.invalid',
        '-c', 'commit.gpgsign=false', 'commit', '-qm', 'Update fixture') | Out-Null
    (Invoke-FixtureGit @('rev-parse', 'HEAD')) -join ''
}
function Expect-Selection([string]$EventName, [string]$BaseSha, [string]$HeadSha, [string[]]$Expected, [string]$Mode) {
    $selection = & "$PSScriptRoot/Get-ChangedPluginProjects.ps1" -Repository $fixture -EventName $EventName -BaseSha $BaseSha -HeadSha $HeadSha
    $actual = @($selection.projects | ForEach-Object { $_.path })
    if (($actual -join '|') -cne (($Expected | Sort-Object) -join '|') -or
        $selection.scan_mode -ne $Mode -or $selection.has_projects -ne ($Expected.Count -gt 0)) {
        throw "Unexpected $EventName selection: $($selection | ConvertTo-Json -Depth 4 -Compress)"
    }
    if (@($selection.projects | ForEach-Object { $_.name } | Select-Object -Unique).Count -ne $actual.Count) {
        throw 'Plugin names must be unique across legacy and v2 roots.'
    }
    $script:checks++
}

try {
    Invoke-FixtureGit @('init', '-q', '--initial-branch=main') | Out-Null
    $legacy = 'plugins/Plugin.A/Plugin.A.csproj'
    $portable = 'plugins-v2/Plugin.A/Plugin.A.csproj'
    $other = 'plugins/Plugin.B/Plugin.B.csproj'
    Write-Fixture $legacy
    Write-Fixture $portable
    Write-Fixture $other
    Write-Fixture 'plugins-v2/Plugin.A/Tests/Plugin.A.Tests.csproj'
    $initial = Commit-Fixture
    Expect-Selection 'push' ('0' * 40) $initial @($legacy, $portable, $other) 'changed'

    Invoke-FixtureGit @('checkout', '-qb', 'feature') | Out-Null
    Write-Fixture 'plugins-v2/Plugin.A/Code.cs'
    Write-Fixture '.github/workflows/plugins-smoke.yml'
    Write-Fixture 'src/TypeWhisper.PluginSDK/Shared.cs'
    Write-Fixture 'Directory.Build.props'
    $feature = Commit-Fixture
    Expect-Selection 'pull_request' $initial $feature @($portable) 'changed'
    Expect-Selection 'push' $initial $feature @($portable) 'changed'

    # Changes arriving on the base branch must not be attributed to the PR.
    Invoke-FixtureGit @('checkout', '-q', 'main') | Out-Null
    Write-Fixture 'plugins/Plugin.B/BaseOnly.cs'
    $advancedBase = Commit-Fixture
    Invoke-FixtureGit @('checkout', '-q', 'feature') | Out-Null
    Expect-Selection 'pull_request' $advancedBase $feature @($portable) 'changed'

    Write-Fixture 'docs/readme.md'
    Write-Fixture 'src/TypeWhisper.PluginSDK/Shared.cs' 'shared-only edit'
    $shared = Commit-Fixture
    Expect-Selection 'push' $feature $shared @() 'none'
    Expect-Selection 'pull_request' $feature $shared @() 'none'
    Expect-Selection 'workflow_dispatch' '' $shared @($legacy, $portable, $other) 'all'

    Write-Fixture 'plugins-v2/Plugin.A/Tests/Plugin.A.Tests.csproj' 'test edit'
    $testEdit = Commit-Fixture
    Expect-Selection 'push' $shared $testEdit @($portable) 'changed'

    # A moved source affects both surviving packages, even when Git detects a rename.
    Invoke-FixtureGit @('mv', 'plugins-v2/Plugin.A/Code.cs', 'plugins/Plugin.B/Moved.cs') | Out-Null
    $moved = Commit-Fixture
    Expect-Selection 'push' $testEdit $moved @($portable, $other) 'changed'

    Invoke-FixtureGit @('rm', '--', $other, 'plugins/Plugin.B/Moved.cs') | Out-Null
    $deleted = Commit-Fixture
    Expect-Selection 'push' $moved $deleted @() 'none'

    $rejected = $false
    try { & "$PSScriptRoot/Get-ChangedPluginProjects.ps1" -Repository $fixture -EventName push -BaseSha ('1' * 40) -HeadSha $deleted 2>$null }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Invalid comparison revisions must fail instead of skipping builds.' }
    $checks++
    Write-Host "$checks plugin selection checks passed."
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $temporary = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($temporary, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolved) -notlike 'plugin-selection-test-*') { throw 'Unexpected fixture path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

# The invalid-revision test intentionally leaves a nonzero native exit code.
# Report the successful assertions explicitly to the GitHub PowerShell wrapper.
exit 0

