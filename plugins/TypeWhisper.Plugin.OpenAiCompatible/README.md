# OpenAI Compatible for the portable host

Portable .NET 10 provider for OpenAI-compatible servers. Plugin ID `com.typewhisper.openai-compatible`; package version `1.1.3`.

Each server profile has its own URL, encrypted key, model catalog, transcription model, text model, thinking preference and text request timeout. A key is optional for local servers. Model discovery is explicit, preserves manual model IDs and retains the saved catalog when the server returns no models. Additional profiles expose stable selection IDs to the host and workflows.

The settings page selects which profile its key controls operate on. The host clears unsaved key input when `IPluginConnectionSettings.ConnectionIdentity` changes. Text field and action IDs include their profile identity, so delayed saves from another profile are rejected. The default profile cannot be removed.

The WinUI profile editor has a persistent profile list, one continuous form and a fixed **Save profile** footer. All fields and an entered API key are committed together through `IPluginProfileSettings`; an empty key input preserves the saved key. Replacement keys are staged in encrypted storage and become active only when the profile write succeeds. Free-text model IDs can use suggestions from that profile's fetched catalog. Unsaved field edits survive profile switches while the page stays open; switching with an unsaved key requires an explicit discard. Leaving the plugin page or closing Settings also guards unsaved profile edits and keys. Removed-profile keys that cannot be deleted immediately are tracked by secret name and retried on activation. Profile removal names the affected profile and asks for confirmation. Newly created profiles receive distinct initial names.

Each profile supports an optional API version, Chat Completions or Responses, Responses reasoning effort, provider-default or custom temperature (0–2), and Auto/Batch/Realtime transcription. Auto selects realtime for the `gpt-live-transcribe` and `gpt-realtime-whisper` families, including dated snapshots; custom deployment aliases require explicit Realtime mode and can select Live Transcribe or Realtime Whisper as their protocol family. Automatic protocol selection recognizes the canonical Whisper model family and otherwise uses Live Transcribe. Realtime uses the configured `/v1/realtime` WebSocket endpoint, converts host PCM16 from 16 to 24 kHz, and rejects translation. Batch can use `/v1/audio/transcriptions` or `/deployments/{model}/audio/transcriptions`; the latter requires a dated API version. Azure endpoint families receive both Bearer and `api-key` authentication. Responses reasoning omits temperature. Chat retries a rejected output-token parameter once only when the server explicitly names both parameter alternatives.

Temperature defaults to the provider's own behavior; no temperature is sent unless Custom is selected. The existing Windows 300-second text timeout default is retained. Base URLs follow the validation rule (no credentials, query or fragment in the base URL); API versions have their own field.

The provider supports translation, UTF-8 message bodies, truncation checks, caller cancellation, independent profile timeouts and DeepInfra-specific `reasoning_effort`. Local/private endpoints do not opt into duplicate request hedging. Model IDs remain editable because compatible servers do not consistently classify their catalog by capability.

Connection tests and model discovery use the current editor values, including an unsaved key. They do not persist those values. A fetched catalog becomes available as suggestions immediately and is committed with **Save profile** only if its URL and API version still match. This allows entering the connection, fetching and choosing models, then saving once.

## Isolation

Credentials are stored only through the host secret store, scoped by profile. The host owns application-level [1.0 profile import](../../docs/DAILY-1.1-CANDIDATE.md#profile-migration).

## Verification

```powershell
dotnet test plugins/TypeWhisper.Plugin.OpenAiCompatible/Tests/TypeWhisper.Plugin.OpenAiCompatible.Portable.Tests.csproj -c Release
dotnet test tests/TypeWhisper.PluginSDK.Portable.Tests/TypeWhisper.PluginSDK.Portable.Tests.csproj -c Release
```

The plugin suite covers transport behavior, profile/key isolation, failed persistence, cancellation, URL validation, manual models, draft connection checks, catalog refreshes and package install/restart/uninstall/reinstall with both provider roles. Responses and keys are fixtures; no paid provider requests are made. Azure and realtime behavior need separate live acceptance.

Build output is staged under `bin/Release/portable-host/Plugins/com.typewhisper.openai-compatible/`, including the assembly, dependency manifest and plugin manifest. The host supplies the SDK assembly.

For native acceptance, create a profile, test an unsaved connection, fetch and select a model, select Responses, switch profiles with draft edits, save once and reopen after restarting. Check conditional temperature controls, the fixed save footer and remove-key visibility.

## Release acceptance still required

- Extend the native settings checks to switching profiles with an unsaved key, complete keyboard navigation, and the German layout.
- Run transcription and text processing against a selected compatible server, then verify an actual package update in an isolated profile.
- Verify that the published minimum host version includes the connection-identity guard, profile editor and `IPluginProfileSettings` SDK capability.
- Complete the remaining portable package lifecycle and release checks in [portable plugin development](../../docs/PLUGIN-MIGRATION-1.1.md) before publication.
