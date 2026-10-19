[CmdletBinding()]
param([Parameter(Mandatory)][string]$MsiPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Velopack's MSI cleanup removes the install folder and also %LOCALAPPDATA%\<package title> of the account running
# msiexec. The title is "TypeWhisper", so uninstalling or upgrading the per-machine MSI from an administrator's
# session deleted that administrator's own per-user installation. Without the title in the cleanup data the action
# removes only the install folder. Runs before the MSI is signed.
$expected = '[INSTALLFOLDER]"[RustAppId]"[TempFolder]'
$scoped = '[INSTALLFOLDER]'
$path = (Resolve-Path -LiteralPath $MsiPath).Path
# Windows Installer's automation interface; the in-box Windows PowerShell binds its COM methods reliably.
$script = @"
`$ErrorActionPreference = 'Stop'
`$ProgressPreference = 'SilentlyContinue'
`$installer = New-Object -ComObject WindowsInstaller.Installer
`$database = `$installer.OpenDatabase('$($path.Replace("'", "''"))', 1)
`$read = `$database.OpenView("SELECT ``Target`` FROM ``CustomAction`` WHERE ``Action`` = 'SetRustCleanupData'")
`$read.Execute()
`$record = `$read.Fetch()
if (`$null -eq `$record) { throw 'The MSI has no SetRustCleanupData action.' }
`$target = `$record.StringData(1)
`$read.Close()
if (`$target -ne '$scoped') {
    if (`$target -ne '$($expected.Replace("'", "''"))') { throw "Unexpected cleanup data: `$target" }
    `$update = `$database.OpenView("UPDATE ``CustomAction`` SET ``Target`` = '$scoped' WHERE ``Action`` = 'SetRustCleanupData'")
    `$update.Execute()
    `$update.Close()
    `$database.Commit()
}
"@
$encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
& "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -EncodedCommand $encoded
if ($LASTEXITCODE -ne 0) { throw "Could not limit the MSI cleanup in $path." }
Write-Host "MSI cleanup limited to the install folder: $(Split-Path $path -Leaf)"
