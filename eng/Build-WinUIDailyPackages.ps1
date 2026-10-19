[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][ValidateSet('win-x64')][string]$RuntimeIdentifier,
    [Parameter(Mandatory)][string]$Framework,
    # Daily also builds the separate early 1.1 Daily installation. RC and Stable ship only the
    # original installation, on the feeds its 1.0 updater already reads.
    [ValidateSet('daily', 'rc', 'stable')][string]$Channel = 'daily'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$signing = $env:SIGNPATH_POLICY -in @('test-signing', 'release-signing')
if ($signing) {
    # Sign the raw candidate too. Its CLI manifest hashes shared DLLs, which this
    # executable-only configuration does not modify.
    $candidateExecutables = @(Get-ChildItem -LiteralPath candidate -Filter 'TypeWhisper*.exe' -Recurse -File |
        Select-Object -ExpandProperty FullName)
    & $env:SIGNPATH_NODE $env:SIGNPATH_HELPER @candidateExecutables
    if ($LASTEXITCODE -ne 0) { throw 'Candidate signing failed.' }
}

$packages = @(switch ($Channel) {
    'daily' { @(
        @{ Id = 'TypeWhisperDaily'; Channel = "$RuntimeIdentifier-winui-daily"; Directory = 'installer' },
        @{ Id = 'TypeWhisper'; Channel = "$RuntimeIdentifier-daily"; Directory = 'legacy-installer' }) }
    'rc' { @(@{ Id = 'TypeWhisper'; Channel = "$RuntimeIdentifier-rc"; Directory = 'installer' }) }
    'stable' { @(@{ Id = 'TypeWhisper'; Channel = $RuntimeIdentifier; Directory = 'installer' }) }
})
foreach ($package in $packages) {
    $packArguments = @('pack', '--packId', $package.Id, '--packTitle', 'TypeWhisper',
        '--packVersion', $Version, '--packDir', 'candidate', '--mainExe', 'TypeWhisper.exe',
        '--channel', $package.Channel, '--runtime', $RuntimeIdentifier, '--framework', $Framework,
        '--icon', 'src/TypeWhisper.WinUI/Assets/app.ico', '--outputDir', $package.Directory)
    # The installation users download also ships a per-machine MSI for fleet deployment. It installs to Program Files
    # and leaves updates to the deployment; the separate early 1.1 Daily upgrade package has none.
    $msi = $package.Directory -eq 'installer'
    if ($msi) {
        $packArguments += @('--msi', '--instLocation', 'PerMachine',
            '--msiVersion', (./eng/Get-MsiProductVersion.ps1 -Version $Version -Channel $Channel))
    }
    if ($signing) {
        # Velopack calls this after creating the portable launcher and updater,
        # and again after embedding the signed package in the final Setup.exe.
        $packArguments += @('--signTemplate', $env:SIGNPATH_SIGN_TEMPLATE, '--signParallel', '10',
            '--signExclude', '(?i)^(?!.*[\\/](?:TypeWhisper[^\\/]*|Squirrel|Update)\.exe$).+')
    }
    # The MSI is adjusted after packing, so the signing callback leaves it for the explicit request below.
    if ($msi) { $env:SIGNPATH_DEFER_MSI = '1' }
    try { & ./tools/vpk.exe @packArguments }
    finally { Remove-Item Env:SIGNPATH_DEFER_MSI -ErrorAction SilentlyContinue }
    if ($LASTEXITCODE -ne 0) { throw "Packaging $($package.Id) failed." }
    if (-not (Get-ChildItem $package.Directory -Filter '*Setup.exe')) { throw 'Installer is missing.' }
    if ($msi) {
        $msiFiles = @(Get-ChildItem $package.Directory -Filter '*.msi' | Select-Object -ExpandProperty FullName)
        if ($msiFiles.Count -ne 1) { throw 'The machine-wide installer is missing.' }
        ./eng/Set-MsiCleanupScope.ps1 -MsiPath $msiFiles[0]
        if ($signing) {
            & $env:SIGNPATH_NODE $env:SIGNPATH_HELPER $msiFiles[0]
            if ($LASTEXITCODE -ne 0) { throw 'Signing the machine-wide installer failed.' }
        }
    }
    ./eng/Test-PortablePackage.ps1 -ReleaseDirectory $package.Directory
    if ($signing) {
        ./eng/signpath/Verify-Packages.ps1 -ReleaseDirectory $package.Directory `
            -Policy $env:SIGNPATH_POLICY -Thumbprint $env:SIGNPATH_CERTIFICATE_THUMBPRINT
    }
}
$original = @($packages | Where-Object { $_.Id -eq 'TypeWhisper' })[0]
./eng/Test-DailyUpgradePackage.ps1 -ReleaseDirectory $original.Directory `
    -RuntimeIdentifier $RuntimeIdentifier -ExpectedVersion $Version -Channel $Channel
