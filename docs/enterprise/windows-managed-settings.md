# Managed settings on Windows (TypeWhisper 1.2 and later)

Administrators can configure TypeWhisper for Windows through registry policies, the
same mechanism Group Policy and Intune write. This page lists the keys the app
understands today. ADMX/ADML templates and deployment guidance from
[#470](https://github.com/TypeWhisper/typewhisper-win/issues/470) follow in a later
release.

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
