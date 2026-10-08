[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$RuntimeIdentifier,
    [Parameter(Mandatory)][string]$Framework
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

foreach ($package in @(
    @{ Id = 'TypeWhisperDaily'; Channel = "$RuntimeIdentifier-winui-daily"; Directory = 'installer' },
    @{ Id = 'TypeWhisper'; Channel = "$RuntimeIdentifier-daily"; Directory = 'legacy-installer' }
)) {
    $packArguments = @('pack', '--packId', $package.Id, '--packTitle', 'TypeWhisper',
        '--packVersion', $Version, '--packDir', 'candidate', '--mainExe', 'TypeWhisper.exe',
        '--channel', $package.Channel, '--runtime', $RuntimeIdentifier, '--framework', $Framework,
        '--icon', 'src/TypeWhisper.WinUI/Assets/app.ico', '--outputDir', $package.Directory)
    if ($signing) {
        # Velopack calls this after creating the portable launcher and updater,
        # and again after embedding the signed package in the final Setup.exe.
        $packArguments += @('--signTemplate', $env:SIGNPATH_SIGN_TEMPLATE, '--signParallel', '10',
            '--signExclude', '(?i)^(?!.*[\\/](?:TypeWhisper[^\\/]*|Squirrel|Update)\.exe$).+')
    }
    & ./tools/vpk.exe @packArguments
    if ($LASTEXITCODE -ne 0) { throw "Packaging $($package.Id) failed." }
    if (-not (Get-ChildItem $package.Directory -Filter '*Setup.exe')) { throw 'Daily installer is missing.' }
    ./eng/Test-PortablePackage.ps1 -ReleaseDirectory $package.Directory
    if ($signing) {
        ./eng/signpath/Verify-Packages.ps1 -ReleaseDirectory $package.Directory `
            -Policy $env:SIGNPATH_POLICY -Thumbprint $env:SIGNPATH_CERTIFICATE_THUMBPRINT
    }
}
./eng/Test-DailyUpgradePackage.ps1 -ReleaseDirectory legacy-installer `
    -RuntimeIdentifier $RuntimeIdentifier -ExpectedVersion $Version
