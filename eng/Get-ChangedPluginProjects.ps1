param(
    [Parameter(Mandatory)]
    [ValidateSet('pull_request', 'push', 'workflow_dispatch')]
    [string]$EventName,
    [string]$BaseSha,
    [string]$HeadSha = 'HEAD',
    [string]$Repository = (Split-Path -Parent $PSScriptRoot)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-RepositoryGit([string[]]$Arguments) {
    $result = @(& git -C $Repository -c core.quotepath=false @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "Plugin discovery failed: git $($Arguments -join ' ')" }
    $result
}

$runAll = $EventName -eq 'workflow_dispatch'
$changedFiles = @()
if (-not $runAll) {
    if ([string]::IsNullOrWhiteSpace($BaseSha)) { throw 'BaseSha is required for automatic plugin discovery.' }
    if ($EventName -eq 'push' -and $BaseSha -match '^0+$') {
        # A newly created ref has no previous tree: every file at its tip is new.
        $changedFiles = @(Invoke-RepositoryGit @('ls-tree', '-r', '--name-only', $HeadSha, '--'))
    } else {
        $comparisonBase = $BaseSha
        if ($EventName -eq 'pull_request') {
            $comparisonBase = (Invoke-RepositoryGit @('merge-base', $BaseSha, $HeadSha)) -join ''
        }
        # Include both sides of moves so an old directory is rebuilt if it still exists.
        $changedFiles = @(Invoke-RepositoryGit @('diff', '--name-only', '--no-renames', $comparisonBase, $HeadSha, '--'))
    }
}

$selectedDirs = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($file in $changedFiles) {
    if ($file -cmatch '^(plugins|plugins-v2)/([^/]+)/') {
        [void]$selectedDirs.Add($Matches[1] + '/' + $Matches[2])
    }
}

# Only tracked, top-level package projects; nested test projects run in headless CI.
# Shared host/SDK/workflow changes do not expand this matrix. Use a manual run
# when a cross-plugin compatibility sweep is needed.
$projects = @(foreach ($path in (Invoke-RepositoryGit @('ls-files', '--', 'plugins', 'plugins-v2') | Sort-Object)) {
    if ($path -cmatch '^(plugins|plugins-v2)/([^/]+)/([^/]+)\.csproj$') {
        $root = $Matches[1]
        $directory = $root + '/' + $Matches[2]
        if (($runAll -or $selectedDirs.Contains($directory)) -and
            (Test-Path -LiteralPath (Join-Path $Repository $path) -PathType Leaf)) {
            [ordered]@{ name = $root + '-' + [IO.Path]::GetFileNameWithoutExtension($path); path = $path }
        }
    }
})

[pscustomobject]@{
    projects = $projects
    has_projects = $projects.Count -gt 0
    scan_mode = if ($runAll) { 'all' } elseif ($projects.Count -gt 0) { 'changed' } else { 'none' }
}
