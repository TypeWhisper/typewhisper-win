# Managed settings on Windows (TypeWhisper 1.2 and later)

Administrators can configure TypeWhisper for Windows through registry policies, the
same mechanism Group Policy and Intune write. This page lists the keys the app
understands, the Group Policy templates in [`deployment/windows`](../../deployment/windows),
and what a deployment needs around the app: network hosts, the microphone
permission and the Intune packaging of the per-user installer. The contract is
[#470](https://github.com/TypeWhisper/typewhisper-win/issues/470).

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
| `TranscriptionProviderId`, `TranscriptionModelId` | `REG_SZ` pair | enforced or recommended | Enforced fixes the transcription provider and model for every entry point; recommended is the initial selection of a profile without an explicit choice. Both values must be present at the same scope. See [provider and model pairs](#provider-and-model-pairs). |
| `LlmProviderId`, `LlmModelId` | `REG_SZ` pair | enforced or recommended | Same rules for the text model used by workflows, translation and text processing, including the default workflow LLM. The pair does not enable optional processing by itself. |
| `UpdateMode` | `REG_DWORD` 0–2 | enforced | 0 = External: no update checks, downloads or installations; the app points to the organization's deployment. 1 = CheckOnly: the app checks and announces versions but never installs. 2 = UserInitiated: users install explicitly; nothing installs unattended. Unset keeps the release behavior. |
| `UpdateChannel` | `REG_DWORD` 0–2 | enforced or recommended | 0 = Stable, 1 = Daily, 2 = ReleaseCandidate. Enforced fixes the channel; recommended is the default for profiles without an explicit choice. The separate Daily installation only receives Daily builds. |
| `AllowedUpdateChannels` | list | enforced | Entries `stable`, `daily`, `release-candidate`. Checks and installations run only on an allowed channel. An empty configured list turns app updates off. An enforced channel outside the list is an error that blocks updates; the app never switches channels on its own. |
| `LaunchAtLogin` | `REG_DWORD` 0/1 | enforced or recommended | Enforced sets the app-owned startup registration and locks the setting; the user's earlier choice is restored when the value is removed. Recommended applies only to a new profile that has not completed setup and has no explicit choice. Windows can still disable startup in Settings > Apps > Startup. |
| `SkipOnboardingWhenReady` | `REG_DWORD` 0/1 | enforced | 1 keeps the initial setup closed when a dictation shortcut, a ready transcription model and a microphone are available. Otherwise setup opens as usual. Nothing is marked as completed. 0 follows the normal rules. |
| `SuppressOptionalPrompts` | `REG_DWORD` 0/1 | enforced or recommended | 1 hides the Premium purchase prompt on the Premium page. Required setup, permission requests, policy problems, license errors and operational errors stay visible. |
| `AllowCloudTranscription` | `REG_DWORD` 0/1 | enforced | 0 blocks audio from reaching any transcription plugin that is not local: cloud providers, user-configured endpoints and packages without a known locality. Dictation, file transcription, recovery, training and the HTTP API refuse with a message instead of switching providers. |
| `AllowCloudTextProcessing` | `REG_DWORD` 0/1 | enforced | 0 blocks transcript, prompt and context text from reaching non-local plugins: LLM providers, text processors, actions, speech output and memory sources. Dictionary terms are not sent as prompts to cloud transcription providers either. Workflows report the refusal; the HTTP API answers 403. |
| `AllowedTranscriptionProviders` | list | enforced | Stable transcription selection IDs (plugin IDs, or profile IDs such as `openai-compatible-<id>`) that may receive audio. Applies to every entry point including workflow and API overrides. The local Parakeet engine is `com.typewhisper.sherpa-onnx`. |
| `AllowedLlmProviders` | list | enforced | Stable LLM selection IDs that may receive text, including workflows, translation and alias suggestions. |
| `AllowedPlugins` | list | enforced | Package IDs that may be installed, loaded, enabled and executed. Packages outside the list are never loaded; their plugin page shows the reason. A package that is running when the list changes is unloaded, and it loads again once the list allows it. The upgrade from a 1.0 profile also skips packages outside the list. |
| `AllowPluginDownloads` | `REG_DWORD` 0/1 | enforced | 0 blocks plugin installation and updates from the catalog, including automatic updates and setup-time installs. Installed packages keep running. |
| `AllowModelDownloads` | `REG_DWORD` 0/1 | enforced | 0 blocks model downloads started by the app: local transcription models, registry plugin models, local text models and local speech models. Existing downloaded models keep working. |

Restriction keys placed under `Recommended` are reported as problems and ignored.
Unknown keys are reported and ignored.

## Provider and model pairs

A pair names a provider by its stable selection ID and a model by the ID the
provider lists:

- The local Parakeet engine is `com.typewhisper.sherpa-onnx`; its model IDs are the
  ones shown in Dictation and by `GET /v1/models` (`provider_id` and `id`), for
  example `parakeet-tdt-0.6b`.
- Cloud plugins use their package ID (`com.typewhisper.groq`, `com.typewhisper.openai`,
  …) and the model IDs their settings page lists, for example `whisper-large-v3`.
- Endpoint profiles of the OpenAI-compatible plugin use `openai-compatible-<id>`.
- LLM providers use the same package IDs; the model IDs are the ones the workflow
  editor offers for that provider.

An **enforced transcription pair** is selected by the app itself and wins over the
user's selection, dictation workflows with their own model, queued and recovered
jobs and HTTP API requests: a request that asks for another engine or model gets
403 `forbidden`. The dictation and setup pickers show the pair and are locked; the
"Provider settings" button stays available for API keys and downloads. The user's
own selection is kept in their stores, never overwritten, and applies again when
the pair is removed. A registry plugin's own model selection is changed to the
enforced model and restored once the policy is removed.

An **enforced LLM pair** replaces the provider and model of every workflow, of the
default workflow LLM and of API translation. The workflow editor keeps the saved
selections but locks the pickers and names the enforced pair; the Default LLM
dialog is read-only. Text sent to another provider or model is refused.

Unknown or unavailable IDs never cause a silent switch:

- A required provider that is not installed, not enabled, or refused by
  `AllowedPlugins`, `AllowedTranscriptionProviders`, `AllowedLlmProviders` or a cloud
  key stays visibly selected and dictation or processing is blocked with the reason.
- A required model that the provider does not offer or that is not downloaded blocks
  the same way. With `AllowModelDownloads` off the deployment has to provision the
  model files; with downloads allowed the user can download it from the provider page.
- Settings > Advanced > Managed settings lists these conflicts under "Policy
  problems" next to the pair itself.
- An incomplete pair (only one of the two values) is invalid: the pair is ignored and
  the affected capability waits until the pair is complete.

A **recommended pair** is the initial selection of a profile that has not chosen a
provider (transcription) or saved a default workflow LLM (text). The user can change
it; an existing choice is never replaced. A recommended model that is not
downloaded yet has to be downloaded by the user or provisioned by the deployment;
until then setup asks for a ready model as usual.

## Plugin locality

Every plugin manifest declares where it processes content through the `locality`
field: `local` (nothing leaves the device), `cloud` (the vendor's fixed service) or
`configurable` (a user-defined endpoint, script or command the app cannot verify).
The plugin catalog publishes the same field. Under `AllowCloudTranscription` and
`AllowCloudTextProcessing` only `local` plugins keep running; `cloud`,
`configurable` and packages without a recognized value are refused. A manifest
without the field falls back to its older `isLocal` flag. A remote package whose
every declared capability (its manifest categories) is turned off is refused as a
whole: it is neither installed, loaded nor enabled, and the Marketplace and plugin
page name the reason.

Known limits of this release:

- A plugin that fetches runtime files while loading a model (for example GPU
  runtimes inside the transcription worker, or the vocabulary assets of the local
  Parakeet plugin) is not stopped by `AllowModelDownloads`; the key covers the
  downloads the app itself starts. Provision those assets with the deployment.
- Configuration actions such as validating an API key still run under
  `AllowCloudTextProcessing`; they carry credentials, not transcripts.
- The pickers for dictation providers, models, workflow text models, memory
  sources and actions show refused entries grayed out with the reason and never
  switch a selection on their own; a refused provider that is still selected is
  refused when it is used, with the same message.
- Audio that a streaming provider already received before a stricter policy arrived
  cannot be recalled; the policy is checked again at the next provider call.

## Group Policy templates

[`deployment/windows`](../../deployment/windows) contains `TypeWhisper.admx` with
`en-US` and `de-DE` ADML files. The template is self-contained (no dependency on
`Windows.admx`), uses only machine policies and writes exactly the keys above.

1. Copy `TypeWhisper.admx` to the Central Store
   `\\<domain>\SYSVOL\<domain>\Policies\PolicyDefinitions` and the ADML files to its
   `en-US` and `de-DE` subfolders. Without a Central Store, use
   `%SystemRoot%\PolicyDefinitions` on the machine that edits the policy.
2. Open the policy in the Group Policy Management Editor (or `gpedit.msc` for the
   local policy) under Computer Configuration > Administrative Templates > TypeWhisper.
   Recommended settings are in the subfolder "Recommended settings (users can change)".
3. Enabled, Disabled and Not Configured map to the registry contract: booleans write
   1 or 0; text, enumeration and list policies write their values when enabled and
   remove them when disabled or not configured; an enabled list with no entries sets
   the `<Name>Configured` marker and denies everything. Replacing a list removes the
   old numbered entries.

The policy applies at the next policy refresh (`gpupdate /target:computer`) and the
running app notices it within seconds.

## Intune

Intune imports third-party ADMX templates through Devices > Configuration >
Import ADMX (settings catalog). Upload `TypeWhisper.admx` with the `en-US` ADML; the
import accepts only `en-US`. Then create a configuration profile from the imported
template, configure the policies and assign it to device groups. Removing a setting
from the profile, or unassigning the profile, returns the registry to "Not
Configured", which the app treats as removal: enforced values disappear and the
users' own settings apply again. Microsoft documents the custom ADMX import as a
public preview at the time of writing; validate the import in a pilot tenant.

ADMX ingestion through a custom OMA-URI profile is the alternative for tenants
without the settings catalog import. The URIs derive from the template: the ADMX
is installed at
`./Device/Vendor/MSFT/Policy/ConfigOperations/ADMXInstall/TypeWhisper/Policy/TypeWhisperAdmx`
with the file content as the string value, and a policy is then addressed as
`./Device/Vendor/MSFT/Policy/Config/TypeWhisper~Policy~TypeWhisper/<PolicyName>`
(recommended settings under `TypeWhisper~Policy~TypeWhisper~TypeWhisper_Recommended`)
with `<enabled/>` or `<disabled/>` plus the `<data id="…" value="…"/>` elements of the
policy. Validate the exact URIs against a pilot device before rolling them out.

## Registry deployment without templates

[`deployment/windows/Set-TypeWhisperPolicy.ps1`](../../deployment/windows/Set-TypeWhisperPolicy.ps1)
writes the same values from an elevated PowerShell session, for Group Policy
Preferences, Intune scripts, other management tools and tests:

```powershell
.\Set-TypeWhisperPolicy.ps1 -Dword @{ UpdateMode = 1; LaunchAtLogin = 1; AllowCloudTranscription = 0 }
.\Set-TypeWhisperPolicy.ps1 -String @{ TranscriptionProviderId = 'com.typewhisper.sherpa-onnx'; TranscriptionModelId = 'parakeet-tdt-0.6b' }
.\Set-TypeWhisperPolicy.ps1 -List @{ AllowedUpdateChannels = @('stable'); AllowedPlugins = @('com.typewhisper.sherpa-onnx') }
.\Set-TypeWhisperPolicy.ps1 -Recommended -Dword @{ UpdateChannel = 0 }
.\Set-TypeWhisperPolicy.ps1 -Show
```

The equivalent plain registry commands:

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

## Network hosts

The app itself contacts these hosts. All connections use HTTPS and the system proxy
configuration, including WPAD and PAC scripts.

| Host | Used for |
| --- | --- |
| `api.polar.sh` | Activation and validation of commercial license keys, including `ManagedLicenseKey`. |
| `app.typewhisper.com` | Premium account sign-in and account status. |
| `api.github.com`, `github.com`, `release-assets.githubusercontent.com` | App update checks and downloads (unless `UpdateMode` is 0) and plugin package downloads. |
| `typewhisper.github.io` | The plugin catalog (`plugins-v2.json`). |
| `huggingface.co` and the `*.hf.co` content hosts it redirects to | Model downloads of the local plugins: Parakeet (sherpa-onnx), dictionary boosting assets, whisper.cpp, local text models, local speech output. |
| `download.pytorch.org`, `developer.download.nvidia.com`, `api.nuget.org` | Runtime files some local plugins fetch while loading a model (Granite Speech, whisper.cpp with CUDA, ONNX runtimes). Not covered by `AllowModelDownloads`; see the known limits above. |
| `graph.microsoft.com`, `www.googleapis.com` | Calendar access for the meeting feature, only when a user connects a calendar. |
| `www.typewhisper.com` | Documentation links opened in the browser. |

Cloud plugins contact their vendor; the table lists the hosts of the plugins in
the catalog at the time of writing. Plugins of the `configurable` locality contact
whatever endpoint the user enters.

| Plugin | Hosts |
| --- | --- |
| AssemblyAI | `api.assemblyai.com`, `cdn.assemblyai.com` |
| Cerebras | `api.cerebras.ai` |
| Claude | `api.anthropic.com` |
| Cloudflare ASR | `api.cloudflare.com` |
| Cohere | `api.cohere.com` |
| Deepgram | `api.deepgram.com` |
| ElevenLabs | `api.elevenlabs.io` |
| Fireworks | `api.fireworks.ai` |
| Gemini | `generativelanguage.googleapis.com` |
| Gladia | `api.gladia.io`, `files.gladia.io` |
| Groq | `api.groq.com` |
| Meta | `api.meta.ai` |
| OpenAI | `api.openai.com`; `auth.openai.com` and `chatgpt.com` for ChatGPT sign-in |
| OpenAI Compatible | the configured endpoint |
| OpenRouter | `openrouter.ai` |
| Reson8 | `api.reson8.dev` |
| Smallest.ai | `api.smallest.ai` |
| Soniox | `api.soniox.com`, `api.eu.soniox.com`, `api.jp.soniox.com` |
| Speechmatics | `eu1.asr.api.speechmatics.com`, `us1.asr.api.speechmatics.com` |
| Voxtral (Mistral) | `api.mistral.ai` |
| xAI | `api.x.ai` |
| Authenticated CLI, Webhook, Script | the command, CLI or URL the user configures |
| Local plugins (Parakeet, whisper.cpp, local LLM, Gemma, Qwen, Cohere Transcribe, Granite Speech, Supertonic TTS) | `huggingface.co` and `*.hf.co` for models; see the runtime hosts above |

## Microphone and other permissions

TypeWhisper is a desktop (Win32) app. Windows does not prompt per app for desktop
microphone access, so there is nothing to pre-grant; the app records as long as two
switches are on in Settings > Privacy & security > Microphone: "Microphone access"
(device-wide) and "Let desktop apps access your microphone" (per user). Both are
registry values a deployment can set and verify:

| Setting | Registry value |
| --- | --- |
| Microphone access (device) | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone`, `Value` = `Allow` |
| Let desktop apps access your microphone (per user) | `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone\NonPackaged`, `Value` = `Allow` |

The Group Policy "Let Windows apps access the microphone" (Computer Configuration >
Administrative Templates > Windows Components > App Privacy) governs packaged apps.
It applies to the Microsoft Store build of TypeWhisper, whose package family name
goes into the policy's "force allow" list; it does not control the desktop
installer. If a user turns a switch off, the app reports the missing microphone in
its status and setup instead of failing silently.

The app needs no administrative rights, no driver and no firewall exception: it
only makes outbound connections, and the optional HTTP API listens on the loopback
interface only.

## Packaging the per-user installer for Intune

TypeWhisper installs per user: the setup writes to `%LocalAppData%\TypeWhisper`,
registers itself under the user's uninstall key and starts the app when it finishes.
Package it as a Win32 app that installs in the user context:

| Item | Value |
| --- | --- |
| Source | `TypeWhisper-win-x64-Setup.exe` from the [GitHub release](https://github.com/TypeWhisper/typewhisper-win/releases) (`https://github.com/TypeWhisper/typewhisper-win/releases/latest/download/TypeWhisper-win-x64-Setup.exe`), with the `.sha256` next to it for verification. |
| Package | `IntuneWinAppUtil.exe -c <folder> -s TypeWhisper-win-x64-Setup.exe -o <output>` |
| Install command | `TypeWhisper-win-x64-Setup.exe --silent` |
| Uninstall command | `"%LocalAppData%\TypeWhisper\Update.exe" --uninstall --silent` |
| Install behavior | User |
| Detection rule | Registry: `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\TypeWhisper`, value `DisplayVersion`, version comparison "greater than or equal to" the packaged version. Alternatively the file `%LocalAppData%\TypeWhisper\current\TypeWhisper.exe`. |
| Upgrade | Deploy the newer setup the same way; it upgrades in place and keeps the user's data. |

Under `UpdateMode` 0 the organization owns updates: deploy new installers through
the same tooling. Under 1 the app announces new versions and shows them in
Settings > Account & about without an install button. A machine-wide installer that
installs for all users from the system context and implies `UpdateMode` 0 is tracked
in [#660](https://github.com/TypeWhisper/typewhisper-win/issues/660).

## Deployment notes

- On shared devices each Windows user runs the installer once, or the deployment tool
  installs it in the user's context. The policy key is device-wide and applies to
  every user.
- Policy is read before plugins and background work start, so a value present at the
  first launch applies immediately. A policy that appears or changes while the app
  runs is applied without a restart.
- Models and plugins live in the user's profile. With `AllowModelDownloads` or
  `AllowPluginDownloads` turned off, stage the required packages and model files in
  the user's profile before the restriction applies, and never copy another user's
  settings, credentials or DPAPI-protected data.
- The app uses the Windows system proxy settings, including WPAD and PAC scripts;
  no separate proxy configuration exists.
