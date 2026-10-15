# Managed settings on Windows (TypeWhisper 1.2 and later)

Administrators can configure TypeWhisper for Windows through registry policies, the
same mechanism Group Policy and Intune write. This page lists the keys the app
understands today. ADMX/ADML templates and the enforced provider/model pairs from
[#470](https://github.com/TypeWhisper/typewhisper-win/issues/470) follow in later
releases.

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
| `AllowCloudTranscription` | `REG_DWORD` 0/1 | enforced | 0 blocks audio from reaching any transcription plugin that is not local: cloud providers, user-configured endpoints and packages without a known locality. Dictation, file transcription, recovery, training and the HTTP API refuse with a message instead of switching providers. |
| `AllowCloudTextProcessing` | `REG_DWORD` 0/1 | enforced | 0 blocks transcript, prompt and context text from reaching non-local plugins: LLM providers, text processors, actions, speech output and memory sources. Workflows report the refusal; the HTTP API answers 403. |
| `AllowedTranscriptionProviders` | list | enforced | Stable transcription selection IDs (plugin IDs, or profile IDs such as `openai-compatible-<id>`) that may receive audio. Applies to every entry point including workflow and API overrides. The local Parakeet engine is `com.typewhisper.sherpa-onnx`. |
| `AllowedLlmProviders` | list | enforced | Stable LLM selection IDs that may receive text, including workflows, translation and alias suggestions. |
| `AllowedPlugins` | list | enforced | Package IDs that may be installed, loaded, enabled and executed. Packages outside the list are never loaded; their plugin page shows the reason. |
| `AllowPluginDownloads` | `REG_DWORD` 0/1 | enforced | 0 blocks plugin installation and updates, including automatic updates and setup-time installs. Installed packages keep running. |
| `AllowModelDownloads` | `REG_DWORD` 0/1 | enforced | 0 blocks model downloads started by the app: local transcription models, registry plugin models and local text models. Existing downloaded models keep working. |

Restriction keys placed under `Recommended` are reported as problems and ignored.
Unknown keys are reported and ignored.

## Plugin locality

Every plugin manifest declares where it processes content through the `locality`
field: `local` (nothing leaves the device), `cloud` (the vendor's fixed service) or
`configurable` (a user-defined endpoint, script or command the app cannot verify).
The plugin catalog publishes the same field. Under `AllowCloudTranscription` and
`AllowCloudTextProcessing` only `local` plugins keep running; `cloud`,
`configurable` and packages without a recognized value are refused. A manifest
without the field falls back to its older `isLocal` flag.

Known limits of this release:

- A plugin that fetches runtime files while loading a model (for example GPU
  runtimes inside the transcription worker, or the vocabulary assets of the local
  Parakeet plugin) is not stopped by `AllowModelDownloads`; the key covers the
  downloads the app itself starts. Provision those assets with the deployment.
- Configuration actions such as validating an API key still run under
  `AllowCloudTextProcessing`; they carry credentials, not transcripts.
- The policy page shows which plugins and providers are refused; the provider
  pickers themselves are not yet filtered.

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
