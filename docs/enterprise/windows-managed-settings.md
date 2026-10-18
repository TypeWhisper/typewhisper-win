# Managed settings on Windows (TypeWhisper 1.2 and later)

Administrators can configure TypeWhisper for Windows through registry policies, the
same mechanism Group Policy and Intune write. This page lists the keys the app
understands today. ADMX/ADML templates and the remaining keys from
[#470](https://github.com/TypeWhisper/typewhisper-win/issues/470) (providers, cloud
restrictions, plugin and model downloads) follow in later releases.

## Registry layout

| Key | Purpose |
| --- | --- |
| `HKLM\SOFTWARE\Policies\TypeWhisper` | Enforced values. Users cannot change them in the app. |
| `HKLM\SOFTWARE\Policies\TypeWhisper\Recommended` | Administrator defaults. They apply until the user makes an explicit choice and stay editable. |

Precedence: enforced policy, then the user's explicit choice, then the recommendation,
then the application default. The app reads the native 64-bit view, never writes the
keys and notices changes while running (registry notification plus a 30-second
fallback). Booleans are `REG_DWORD` 0 or 1, enumerations are `REG_DWORD`, text is
`REG_SZ`. Lists are a subkey with numbered `REG_SZ` values (`1`, `2`, …) next to a
`<Name>Configured` `REG_DWORD`: marker absent or 0 leaves the list unrestricted,
marker 1 with an empty list denies everything.

An invalid enforced value blocks its setting until it is corrected; it never relaxes
anything. An unreadable policy key keeps the last known values and blocks managed
operations. Settings > Advanced > Managed settings shows the effective values and
any problems; the support report contains the same section.

## Supported keys

| Key | Type | Scope | Behavior |
| --- | --- | --- | --- |
| `ManagedLicenseKey` | `REG_SZ` | enforced | Activates a commercial license for the signed-in user. See [managed license activation](windows-managed-license.md). |
| `UpdateMode` | `REG_DWORD` 0–2 | enforced | 0 = External: no update checks, downloads or installations; the app points to the organization's deployment. 1 = CheckOnly: the app checks and announces versions but never installs. 2 = UserInitiated: users install explicitly; nothing installs unattended. Unset keeps the release behavior. |
| `UpdateChannel` | `REG_DWORD` 0–2 | enforced or recommended | 0 = Stable, 1 = Daily, 2 = ReleaseCandidate. Enforced fixes the channel; recommended is the default for profiles without an explicit choice. The separate Daily installation only receives Daily builds. |
| `AllowedUpdateChannels` | list | enforced | Entries `stable`, `daily`, `release-candidate`. Checks and installations run only on an allowed channel. An empty configured list turns app updates off. An enforced channel outside the list is an error that blocks updates; the app never switches channels on its own. |
| `LaunchAtLogin` | `REG_DWORD` 0/1 | enforced or recommended | Enforced sets the app-owned startup registration and locks the setting; the user's earlier choice is restored when the value is removed. Recommended applies only to a new profile that has not completed setup and has no explicit choice. Windows can still disable startup in Settings > Apps > Startup. |
| `SkipOnboardingWhenReady` | `REG_DWORD` 0/1 | enforced | 1 keeps the initial setup closed when a dictation shortcut, a ready transcription model and a microphone are available. Otherwise setup opens as usual. Nothing is marked as completed. 0 follows the normal rules. |
| `SuppressOptionalPrompts` | `REG_DWORD` 0/1 | enforced or recommended | 1 hides the Premium purchase prompt on the Premium page. Required setup, permission requests, policy problems, license errors and operational errors stay visible. |

Restriction keys placed under `Recommended` are reported as problems and ignored.
Unknown keys are reported and ignored.

## Example

Elevated PowerShell, equivalent to a Group Policy registry item or an Intune script:

```powershell
$policy = 'HKLM:\SOFTWARE\Policies\TypeWhisper'
New-Item -Path $policy -Force | Out-Null
Set-ItemProperty -Path $policy -Name UpdateMode -Value 1 -Type DWord          # check only
Set-ItemProperty -Path $policy -Name LaunchAtLogin -Value 1 -Type DWord       # start with Windows
Set-ItemProperty -Path $policy -Name AllowedUpdateChannelsConfigured -Value 1 -Type DWord
New-Item -Path "$policy\AllowedUpdateChannels" -Force | Out-Null
Set-ItemProperty -Path "$policy\AllowedUpdateChannels" -Name 1 -Value stable -Type String
New-Item -Path "$policy\Recommended" -Force | Out-Null
Set-ItemProperty -Path "$policy\Recommended" -Name UpdateChannel -Value 0 -Type DWord
```

Removing a value restores the user's own setting. Removing the whole key restores all
of them. Standard users cannot write `HKLM`, so they cannot change enforced values.

## Deployment notes

- TypeWhisper installs per user. On shared devices each Windows user runs the
  installer once, or the deployment tool installs it in the user's context. The
  policy key is device-wide and applies to every user.
- Under `UpdateMode` 0 the organization owns updates: deploy new installers through
  the same tooling. Under 1 the app announces new versions and shows them in
  Settings > Account & about without an install button.
- Policy is read before plugins and background work start, so a value present at the
  first launch applies immediately.
