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

# Turns a Compile Include relative to its project directory into a repository path,
# normalizing '\', '.' and '..'. MSBuild wildcards are kept for Test-PatternMatch.
function Resolve-RepositoryPath([string]$Directory, [string]$Relative) {
    $segments = [Collections.Generic.List[string]]::new()
    foreach ($segment in ($Directory + '/' + $Relative.Replace('\', '/')).Split('/')) {
        if ($segment -eq '' -or $segment -eq '.') { continue }
        if ($segment -eq '..') {
            if ($segments.Count -gt 0) { $segments.RemoveAt($segments.Count - 1) }
            continue
        }
        $segments.Add($segment)
    }
    $segments -join '/'
}

# Lists the repository paths (or wildcard patterns) a project compiles through explicit Compile items.
function Get-LinkedSourcePatterns([string]$ProjectPath) {
    try { $project = [xml](Get-Content -LiteralPath (Join-Path $Repository $ProjectPath) -Raw) }
    catch { throw "Plugin discovery failed: cannot read $ProjectPath ($($_.Exception.Message))" }
    $directory = $ProjectPath -replace '/[^/]+$', ''
    foreach ($compile in $project.SelectNodes("//*[local-name()='Compile'][@Include]")) {
        foreach ($include in $compile.GetAttribute('Include').Split(';', [StringSplitOptions]::RemoveEmptyEntries)) {
            # Property-based includes cannot be resolved without MSBuild; they never point at plugins/shared.
            if ($include.Trim() -eq '' -or $include.Contains('$(')) { continue }
            Resolve-RepositoryPath $directory $include.Trim()
        }
    }
}

function Test-PatternMatch([string]$Pattern, [string]$Path) {
    if ($Pattern.IndexOfAny([char[]]'*?') -lt 0) { return $Pattern -ceq $Path }
    # MSBuild wildcards: '**/' spans directories, '*' and '?' stay within one path segment.
    $regex = [regex]::Escape($Pattern) -replace '\\\*\\\*/', '(?:.*/)?' -replace '\\\*', '[^/]*' -replace '\\\?', '[^/]'
    $Path -cmatch ('^' + $regex + '$')
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
$changedShared = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($file in $changedFiles) {
    if ($file -cmatch '^plugins/shared/') {
        [void]$changedShared.Add($file)
    } elseif ($file -cmatch '^(plugins)/([^/]+)/') {
        [void]$selectedDirs.Add($Matches[1] + '/' + $Matches[2])
    }
}

$trackedPaths = @(Invoke-RepositoryGit @('ls-files', '--', 'plugins') | Sort-Object)

# A source under plugins/shared compiles into every plugin that links it, so a change there
# selects each package whose project or test project links one of the changed files.
if ($changedShared.Count -gt 0) {
    foreach ($path in $trackedPaths) {
        if ($path -cmatch '^(plugins/[^/]+)/.*\.csproj$' -and $Matches[1] -ne 'plugins/shared' -and
            (Test-Path -LiteralPath (Join-Path $Repository $path) -PathType Leaf)) {
            $directory = $Matches[1]
            foreach ($pattern in @(Get-LinkedSourcePatterns $path)) {
                if (@($changedShared | Where-Object { Test-PatternMatch $pattern $_ }).Count -gt 0) {
                    [void]$selectedDirs.Add($directory)
                    break
                }
            }
        }
    }
}

# Only tracked, top-level package projects; the Plugins workflow also runs all plugin tests.
# Host/SDK/workflow changes do not expand this matrix. Use a manual run
# when a cross-plugin compatibility sweep is needed.
$projects = @(foreach ($path in $trackedPaths) {
    if ($path -cmatch '^(plugins)/([^/]+)/([^/]+)\.csproj$') {
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
