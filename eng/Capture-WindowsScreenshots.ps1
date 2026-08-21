[CmdletBinding()]
param(
    [ValidateSet('App', 'Plugins', 'All')]
    [string]$Scope = 'All',

    [ValidateSet('de', 'en', 'ja', 'ru', 'zh-Hans')]
    [string[]]$Locales = @('en'),

    [string[]]$Plugins = @(),

    [string]$OutputRoot,

    [string]$DisplayVersion,

    [ValidateRange(1, 300)]
    [int]$TimeoutSeconds = 45
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($DisplayVersion)) {
    $stableVersions = @(git -C $repoRoot tag --list 'v*' |
        Where-Object { $_ -match '^v(\d+\.\d+\.\d+)$' } |
        ForEach-Object { [version]$_.Substring(1) } |
        Sort-Object -Descending)
    if ($LASTEXITCODE -ne 0 -or $stableVersions.Count -eq 0) {
        throw 'Could not determine the latest stable version tag. Pass -DisplayVersion explicitly.'
    }
    $DisplayVersion = $stableVersions[0].ToString(3)
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot 'artifacts\screenshots\windows'
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$buildRoot = Join-Path $repoRoot 'artifacts\ui-automation'
$appOutput = Join-Path $buildRoot 'app'
$runnerOutput = Join-Path $buildRoot 'runner'
$registryFile = Join-Path $buildRoot 'plugins.json'
$appProject = Join-Path $repoRoot 'src\TypeWhisper.Windows\TypeWhisper.Windows.csproj'
$runnerProject = Join-Path $repoRoot 'tools\TypeWhisper.UiAutomation\TypeWhisper.UiAutomation.csproj'
$appPath = Join-Path $appOutput 'TypeWhisper.exe'

if (Test-Path $appOutput) {
    $resolvedAppOutput = [System.IO.Path]::GetFullPath($appOutput)
    $resolvedBuildRoot = [System.IO.Path]::GetFullPath($buildRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedAppOutput.StartsWith($resolvedBuildRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear app output outside the UI automation build root: $resolvedAppOutput"
    }
    Remove-Item -LiteralPath $resolvedAppOutput -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $appOutput, $runnerOutput, $OutputRoot | Out-Null

dotnet publish $appProject -c Debug -r win-x64 --self-contained false -p:Version=$DisplayVersion -p:PublishDir=$appOutput
if ($LASTEXITCODE -ne 0) {
    throw 'The TypeWhisper debug publish failed.'
}

dotnet build $runnerProject -c Debug -o $runnerOutput
if ($LASTEXITCODE -ne 0) {
    throw 'The UI automation runner build failed.'
}

$pluginProjects = Get-ChildItem (Join-Path $repoRoot 'plugins') -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName 'manifest.json') } |
    Sort-Object Name

if ($Plugins.Count -gt 0) {
    $requested = [System.Collections.Generic.HashSet[string]]::new(
        $Plugins,
        [System.StringComparer]::OrdinalIgnoreCase)
    $pluginProjects = @($pluginProjects | Where-Object {
        $manifest = Get-Content (Join-Path $_.FullName 'manifest.json') -Raw | ConvertFrom-Json
        $requested.Contains($_.Name) -or $requested.Contains([string]$manifest.id)
    })

    $matchedNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($pluginProject in $pluginProjects) {
        $manifest = Get-Content (Join-Path $pluginProject.FullName 'manifest.json') -Raw | ConvertFrom-Json
        [void]$matchedNames.Add($pluginProject.Name)
        [void]$matchedNames.Add([string]$manifest.id)
    }
    $missing = @($Plugins | Where-Object { -not $matchedNames.Contains($_) })
    if ($missing.Count -gt 0) {
        throw "Unknown plugin project or ID: $($missing -join ', ')"
    }
}
else {
    $pluginProjects = @()
}

if ($Scope -in @('Plugins', 'All')) {
    foreach ($pluginProject in $pluginProjects) {
        $projectFile = Get-ChildItem $pluginProject.FullName -Filter '*.csproj' -File | Select-Object -First 1
        if ($null -eq $projectFile) {
            throw "No project file found for $($pluginProject.Name)."
        }

        dotnet build $projectFile.FullName -c Debug -p:Version=$DisplayVersion -p:AppOutputDir=$appOutput
        if ($LASTEXITCODE -ne 0) {
            throw "Plugin build failed: $($pluginProject.Name)"
        }

        $manifest = Get-Content (Join-Path $pluginProject.FullName 'manifest.json') -Raw | ConvertFrom-Json
        $pluginBuildOutput = Join-Path $pluginProject.FullName 'bin\Debug\net10.0-windows'
        $pluginBuildRoot = [System.IO.Path]::GetFullPath($pluginBuildOutput)
        $pluginInstallOutput = Join-Path (Join-Path $appOutput 'Plugins') ([string]$manifest.id)
        New-Item -ItemType Directory -Force -Path $pluginInstallOutput | Out-Null
        foreach ($file in (Get-ChildItem $pluginBuildOutput -File -Recurse)) {
            if ($file.Name.StartsWith('TypeWhisper.PluginSDK.', [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $relativePath = $file.FullName.Substring($pluginBuildRoot.Length).TrimStart([char[]]@('\', '/'))
            $destination = Join-Path $pluginInstallOutput $relativePath
            $destinationDirectory = Split-Path -Parent $destination
            New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
        }
    }
}

$registry = foreach ($pluginProject in (Get-ChildItem (Join-Path $repoRoot 'plugins') -Directory | Sort-Object Name)) {
    $manifestPath = Join-Path $pluginProject.FullName 'manifest.json'
    if (-not (Test-Path $manifestPath)) {
        continue
    }

    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    [ordered]@{
        id = $manifest.id
        name = $manifest.name
        version = $manifest.version
        author = $manifest.author
        description = $manifest.description
        descriptions = $manifest.descriptions
        category = $manifest.category
        categories = $manifest.categories
        platforms = @('windows')
        size = 0
        downloadUrl = 'https://ui-automation.typewhisper.invalid/plugin.zip'
        requiresApiKey = [bool]$manifest.requiresApiKey
        hosting = if ([bool]$manifest.isLocal) { 'local' } else { 'cloud' }
    }
}
$registryJson = $registry | ConvertTo-Json -Depth 8
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($registryFile, $registryJson, $utf8NoBom)

$scopeArgument = $Scope.ToLowerInvariant()
foreach ($locale in $Locales) {
    $localeOutput = Join-Path $OutputRoot $locale
    New-Item -ItemType Directory -Force -Path $localeOutput | Out-Null

    dotnet (Join-Path $runnerOutput 'typewhisper-ui.dll') capture `
        --app $appPath `
        --output $localeOutput `
        --language $locale `
        --display-version $DisplayVersion `
        --scope $scopeArgument `
        --plugin-registry $registryFile `
        --timeout $TimeoutSeconds
    if ($LASTEXITCODE -ne 0) {
        throw "Screenshot capture failed for locale '$locale'."
    }
}

Write-Host "Windows screenshots are available in $OutputRoot"
