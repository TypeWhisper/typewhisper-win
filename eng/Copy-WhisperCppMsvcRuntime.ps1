[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string[]]$RuntimeIdentifiers = @("win-x64", "win-arm64"),

    [switch]$Required
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$requiredDlls = @(
    "msvcp140.dll",
    "vcruntime140.dll",
    "vcruntime140_1.dll",
    "VCOMP140.DLL"
)

$architectureByRuntime = @{
    "win-x64" = "x64"
    "win-arm64" = "arm64"
}

$normalizedRuntimeIdentifiers = @(
    $RuntimeIdentifiers |
        ForEach-Object { $_ -split "[,;]" } |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_.Length -gt 0 } |
        Select-Object -Unique
)

function Add-UniquePath {
    param(
        [System.Collections.Generic.List[string]]$Paths,
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return
    }

    try {
        $resolvedPath = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
    }
    catch {
        return
    }

    if (-not $Paths.Contains($resolvedPath)) {
        [void]$Paths.Add($resolvedPath)
    }
}

function Get-VisualStudioInstallPaths {
    $paths = New-Object "System.Collections.Generic.List[string]"
    $vswhereCandidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"),
        (Join-Path $env:ProgramFiles "Microsoft Visual Studio\Installer\vswhere.exe")
    )

    foreach ($vswhere in $vswhereCandidates) {
        if (Test-Path -LiteralPath $vswhere) {
            $installPaths = & $vswhere -all -products * -property installationPath 2>$null
            foreach ($installPath in $installPaths) {
                Add-UniquePath -Paths $paths -Path $installPath
            }
        }
    }

    $visualStudioRoots = @(
        (Join-Path $env:ProgramFiles "Microsoft Visual Studio"),
        (Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio")
    )

    foreach ($root in $visualStudioRoots) {
        if (-not (Test-Path -LiteralPath $root)) {
            continue
        }

        foreach ($yearDirectory in Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue) {
            foreach ($editionDirectory in Get-ChildItem -LiteralPath $yearDirectory.FullName -Directory -ErrorAction SilentlyContinue) {
                Add-UniquePath -Paths $paths -Path $editionDirectory.FullName
            }
        }
    }

    return $paths.ToArray()
}

function Convert-ToVersion {
    param([string]$Value)

    $version = $null
    if ([Version]::TryParse($Value, [ref]$version)) {
        return $version
    }

    return [Version]"0.0"
}

function Find-RedistFile {
    param(
        [string[]]$InstallPaths,
        [string]$Architecture,
        [string]$FileName
    )

    $matches = New-Object "System.Collections.Generic.List[object]"

    foreach ($installPath in $InstallPaths) {
        $redistRoot = Join-Path $installPath "VC\Redist\MSVC"
        if (-not (Test-Path -LiteralPath $redistRoot)) {
            continue
        }

        foreach ($versionDirectory in Get-ChildItem -LiteralPath $redistRoot -Directory -ErrorAction SilentlyContinue) {
            $architectureDirectory = Join-Path $versionDirectory.FullName $Architecture
            if (-not (Test-Path -LiteralPath $architectureDirectory)) {
                continue
            }

            foreach ($componentDirectory in Get-ChildItem -LiteralPath $architectureDirectory -Directory -Filter "Microsoft.VC*" -ErrorAction SilentlyContinue) {
                $candidate = Join-Path $componentDirectory.FullName $FileName
                if (Test-Path -LiteralPath $candidate) {
                    [void]$matches.Add([pscustomobject]@{
                        Path = $candidate
                        Version = Convert-ToVersion -Value $versionDirectory.Name
                    })
                }
            }
        }
    }

    return $matches |
        Sort-Object -Property @{ Expression = { $_.Version }; Descending = $true }, @{ Expression = { $_.Path }; Descending = $false } |
        Select-Object -First 1
}

$installPaths = @(Get-VisualStudioInstallPaths)
if ($installPaths.Count -eq 0) {
    $message = "Could not find a Visual Studio installation containing VC redistributable files."
    if ($Required) {
        throw $message
    }

    Write-Warning $message
    exit 0
}

$missing = New-Object "System.Collections.Generic.List[string]"

foreach ($runtimeIdentifier in $normalizedRuntimeIdentifiers) {
    if (-not $architectureByRuntime.ContainsKey($runtimeIdentifier)) {
        Write-Warning "Skipping unsupported runtime identifier: $runtimeIdentifier"
        continue
    }

    $architecture = $architectureByRuntime[$runtimeIdentifier]
    $runtimeDirectory = Join-Path (Join-Path $OutputDirectory "runtimes") $runtimeIdentifier
    New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null

    foreach ($dll in $requiredDlls) {
        $source = Find-RedistFile -InstallPaths $installPaths -Architecture $architecture -FileName $dll
        if ($null -eq $source) {
            [void]$missing.Add("$runtimeIdentifier/$dll")
            continue
        }

        $destination = Join-Path $runtimeDirectory $dll
        Copy-Item -LiteralPath $source.Path -Destination $destination -Force
        Write-Host "Copied $dll for $runtimeIdentifier from $($source.Path)"
    }
}

if ($missing.Count -gt 0) {
    $message = "Missing required MSVC redistributable files: $($missing -join ', ')"
    if ($Required) {
        throw $message
    }

    Write-Warning $message
}
