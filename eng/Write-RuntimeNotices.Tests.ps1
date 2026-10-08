$ErrorActionPreference = 'Stop'
$fixture = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('runtime-notices-' + [guid]::NewGuid().ToString('N'))))
$cache = Join-Path $fixture 'cache'
$output = Join-Path $fixture 'publish'
$assetsPath = Join-Path $fixture 'project.assets.json'
try {
    foreach ($id in @('sample.runtime', 'sample.download')) {
        $package = Join-Path $cache ($id + '/1.0.0')
        New-Item -ItemType Directory -Force -Path (Join-Path $package 'legal') | Out-Null
        [IO.File]::WriteAllText((Join-Path $package ($id + '.nuspec')), '<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata><id>' + $id + '</id><version>1.0.0</version><authors>Original author</authors><copyright>Original copyright</copyright><license type="file">legal/LICENSE.txt</license></metadata></package>')
        [IO.File]::WriteAllText((Join-Path $package 'legal/LICENSE.txt'), 'Original license text')
        [IO.File]::WriteAllText((Join-Path $package 'NOTICE.txt'), 'Original third-party attribution')
    }
    @{ libraries = @{ 'Sample.Runtime/1.0.0' = @{ type = 'package'; path = 'sample.runtime/1.0.0' } }; packageFolders = @{ $cache = @{} } } | ConvertTo-Json -Depth 8 | Set-Content $assetsPath
    & "$PSScriptRoot/Write-RuntimeNotices.ps1" -AssetsPath $assetsPath -OutputDirectory $output -AdditionalPackages 'Sample.Download@[1.0.0]'
    $notice = Get-Content (Join-Path $output 'THIRD-PARTY-NOTICES.txt') -Raw
    if (!$notice.Contains('Sample.Runtime/1.0.0') -or !$notice.Contains('Sample.Download/1.0.0') -or !$notice.Contains('Original copyright')) { throw 'The inventory lost package attribution.' }
    foreach ($id in @('Sample.Runtime', 'Sample.Download')) {
        $folder = Join-Path $output ('Licenses/' + $id + '-1.0.0')
        if ([IO.File]::ReadAllText((Join-Path $folder 'legal/LICENSE.txt')) -cne 'Original license text' -or
            [IO.File]::ReadAllText((Join-Path $folder 'NOTICE.txt')) -cne 'Original third-party attribution') { throw 'License text was not preserved.' }
    }
    $rejected = $false
    try { & "$PSScriptRoot/Write-RuntimeNotices.ps1" -AssetsPath $assetsPath -OutputDirectory $output -AdditionalPackages 'Missing.Package@[1.0.0]' }
    catch { $rejected = $_.Exception.Message.Contains('Restored package not found') }
    if (!$rejected) { throw 'Missing package metadata was silently omitted.' }
    Write-Host 'Runtime notice checks passed.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    if ($resolved -ne $fixture -or !$resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Force -Recurse }
}
