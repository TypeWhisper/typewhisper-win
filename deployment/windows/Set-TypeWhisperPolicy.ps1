<#
.SYNOPSIS
Writes, removes or shows TypeWhisper machine policy values in the registry, for deployments without Group Policy or
Intune and for tests. Run elevated. Works in Windows PowerShell 5.1 and PowerShell 7.

.DESCRIPTION
Values land under HKLM:\SOFTWARE\Policies\TypeWhisper (enforced) or its Recommended subkey (-Recommended). Lists
are written as the app and the ADMX template expect them: a <Name>Configured DWORD of 1 next to a <Name> subkey with
numbered REG_SZ values 1, 2, ...; existing numbered entries are replaced. The key reference is
docs/enterprise/windows-managed-settings.md.

.EXAMPLE
.\Set-TypeWhisperPolicy.ps1 -Show
.\Set-TypeWhisperPolicy.ps1 -Dword @{ AllowCloudTranscription = 0; UpdateMode = 1 }
.\Set-TypeWhisperPolicy.ps1 -String @{ TranscriptionProviderId = 'com.typewhisper.sherpa-onnx'; TranscriptionModelId = 'parakeet-tdt-0.6b' }
.\Set-TypeWhisperPolicy.ps1 -List @{ AllowedPlugins = @('com.typewhisper.sherpa-onnx', 'com.typewhisper.groq') }
.\Set-TypeWhisperPolicy.ps1 -List @{ AllowedUpdateChannels = @() }      # configured and empty: updates off
.\Set-TypeWhisperPolicy.ps1 -Recommended -Dword @{ UpdateChannel = 0 }
.\Set-TypeWhisperPolicy.ps1 -Remove AllowedPlugins, ManagedLicenseKey
.\Set-TypeWhisperPolicy.ps1 -Clear
#>
[CmdletBinding()]
param(
    # Name/value pairs written as REG_DWORD (booleans 0/1, enumerations).
    [hashtable]$Dword,
    # Name/value pairs written as REG_SZ (IDs, the managed license key).
    [hashtable]$String,
    # Name/array pairs written as list subkeys with the <Name>Configured marker set to 1.
    [hashtable]$List,
    # Value or list names to remove from the selected scope. A list removes its marker and subkey.
    [string[]]$Remove,
    # Target the Recommended subkey instead of the enforced root.
    [switch]$Recommended,
    # Remove the whole policy key including Recommended.
    [switch]$Clear,
    # Print the current policy (always printed when nothing else is requested).
    [switch]$Show
)
$ErrorActionPreference = 'Stop'
$root = 'HKLM:\SOFTWARE\Policies\TypeWhisper'
$path = if ($Recommended) { "$root\Recommended" } else { $root }
if (-not $Dword) { $Dword = @{} }
if (-not $String) { $String = @{} }
if (-not $List) { $List = @{} }
if (-not $Remove) { $Remove = @() }
$writes = $Dword.Count + $String.Count + $List.Count + $Remove.Count

function Assert-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Writing HKLM policy requires an elevated PowerShell session.'
    }
}

if ($Clear) {
    Assert-Elevated
    if (Test-Path $root) { Remove-Item -Path $root -Recurse -Force; Write-Host "Removed $root" } else { Write-Host "$root did not exist" }
    return
}
if ($writes -gt 0) {
    Assert-Elevated
    if (($Dword.Count + $String.Count + $List.Count) -gt 0 -and -not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
    foreach ($entry in $Dword.GetEnumerator()) {
        Set-ItemProperty -Path $path -Name $entry.Key -Value ([int]$entry.Value) -Type DWord
        Write-Host "$($entry.Key) = $($entry.Value) (REG_DWORD)"
    }
    foreach ($entry in $String.GetEnumerator()) {
        Set-ItemProperty -Path $path -Name $entry.Key -Value ([string]$entry.Value) -Type String
        $shown = if ($entry.Key -eq 'ManagedLicenseKey') { '(set, not shown)' } else { $entry.Value }
        Write-Host "$($entry.Key) = $shown (REG_SZ)"
    }
    foreach ($entry in $List.GetEnumerator()) {
        if ($Recommended) { throw "Lists such as $($entry.Key) are restrictions and cannot be recommended." }
        $listPath = "$path\$($entry.Key)"
        if (Test-Path $listPath) { Remove-Item -Path $listPath -Recurse -Force }
        New-Item -Path $listPath -Force | Out-Null
        $index = 1
        foreach ($item in @($entry.Value)) {
            Set-ItemProperty -Path $listPath -Name ([string]$index) -Value ([string]$item) -Type String
            $index++
        }
        Set-ItemProperty -Path $path -Name "$($entry.Key)Configured" -Value 1 -Type DWord
        Write-Host "$($entry.Key) = [$(@($entry.Value) -join ', ')] (configured list)"
    }
    foreach ($name in $Remove) {
        if (-not (Test-Path $path)) { continue }
        Remove-ItemProperty -Path $path -Name $name -ErrorAction SilentlyContinue
        Remove-ItemProperty -Path $path -Name "${name}Configured" -ErrorAction SilentlyContinue
        if (Test-Path "$path\$name") { Remove-Item -Path "$path\$name" -Recurse -Force }
        Write-Host "Removed $name"
    }
}
if ($Show -or $writes -eq 0) {
    foreach ($key in @($root, "$root\Recommended")) {
        if (-not (Test-Path $key)) { Write-Host "$key : absent"; continue }
        Write-Host "$key :"
        $item = Get-Item $key
        foreach ($name in $item.GetValueNames()) {
            $value = if ($name -eq 'ManagedLicenseKey') { '(set, not shown)' } else { $item.GetValue($name) }
            Write-Host ("  {0} = {1} ({2})" -f $name, $value, $item.GetValueKind($name))
        }
        foreach ($sub in ($item.GetSubKeyNames() | Where-Object { $_ -ne 'Recommended' })) {
            $listKey = Get-Item "$key\$sub"
            $entries = $listKey.GetValueNames() | Sort-Object { [int]$_ } | ForEach-Object { $listKey.GetValue($_) }
            Write-Host ("  {0}\ : [{1}]" -f $sub, ($entries -join ', '))
        }
    }
}
