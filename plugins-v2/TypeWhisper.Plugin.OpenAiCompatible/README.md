# OpenAI Compatible for the portable host

Independent .NET 10 port of the Windows provider at `3282e9ca`, extended from the local Swift plugin (checkout `d0171b38`) with host-rendered settings instead of WPF views. The logical plugin ID remains `com.typewhisper.openai-compatible`; package version is `1.1.0`.

Each server profile has its own URL, encrypted key, model catalog, transcription model, text model, thinking preference and text request timeout. A key is optional for local servers. Model discovery is explicit, preserves manual model IDs and retains the saved catalog when the server returns no models. Additional profiles expose stable selection IDs to the host and workflows.

The settings page selects which profile its key controls operate on. The host clears unsaved key input when `IPluginConnectionSettings.ConnectionIdentity` changes. Text field and action IDs include their profile identity, so delayed saves from another profile are rejected. The default profile cannot be removed.

The WinUI profile editor has a persistent profile list, one continuous form and a fixed **Save profile** footer. All fields and an entered API key are committed together through `IPluginProfileSettings`; an empty key input preserves the saved key. Replacement keys are staged in encrypted storage and become active only when the profile write succeeds. Free-text model IDs can use suggestions from that profile's fetched catalog. Unsaved field edits survive profile switches while the page stays open; switching with an unsaved key requires an explicit discard. Profile removal names the affected profile and asks for confirmation. Newly created profiles receive distinct initial names.

Each profile supports an optional API version, Chat Completions or Responses, Responses reasoning effort, provider-default or custom temperature (0–2), and Auto/Batch/Realtime transcription. Auto selects realtime only for `gpt-live-transcribe` and `gpt-realtime-whisper`; custom deployment aliases require explicit Realtime mode. Realtime uses the configured `/v1/realtime` WebSocket endpoint, converts host PCM16 from 16 to 24 kHz, and rejects translation. Batch can use `/v1/audio/transcriptions` or `/deployments/{model}/audio/transcriptions`; the latter requires a dated API version. Azure endpoint families receive both Bearer and `api-key` authentication. Responses reasoning omits temperature. Chat retries a rejected output-token parameter once only when the server explicitly names both parameter alternatives.

Temperature defaults to the provider's own behavior; no temperature is sent unless Custom is selected. The existing Windows 300-second text timeout default is retained. The port intentionally retains the Windows URL validation rule (no credentials, query or fragment in the base URL); API versions have their own field.

The protocol port retains translation, UTF-8 message bodies, truncation checks, caller cancellation, independent profile timeouts and DeepInfra-specific `reasoning_effort`. Local/private endpoints do not opt into duplicate request hedging. Model IDs remain editable because compatible servers do not consistently classify their catalog by capability.

Connection tests and model discovery use the current editor values, including an unsaved key. They do not persist those values. A fetched catalog becomes available as suggestions immediately and is committed with **Save profile** only if its URL and API version still match. This allows entering the connection, fetching and choosing models, then saving once.

## Isolation

The project references the portable SDK and contains no WPF references. It neither links legacy provider source nor reads historical profile directories. Fresh profiles do not import the legacy flat settings. The legacy project, manifest, feed and packages remain unchanged. Credentials are stored only through the host secret store, scoped by profile.

## Verification

```powershell
dotnet test plugins-v2/TypeWhisper.Plugin.OpenAiCompatible/Tests/TypeWhisper.Plugin.OpenAiCompatible.Portable.Tests.csproj -c Release
dotnet test tests/TypeWhisper.PluginSDK.Portable.Tests/TypeWhisper.PluginSDK.Portable.Tests.csproj -c Release
```

The plugin suite covers transport behavior, profile/key isolation, failed persistence, cancellation, URL validation, manual models, draft connection checks and catalog refreshes, and isolated package install/restart/uninstall/reinstall with both provider roles. Network responses and keys are fixtures; no paid provider requests are made. A separate local smoke test verified model discovery, Chat Completions and Responses against LM Studio with `qwen/qwen3-4b` on 2026-09-12. Azure and realtime were tested with protocol fixtures, not a live provider.

Build output is staged under `bin/Release/portable-host/Plugins/com.typewhisper.openai-compatible/`, including the assembly, dependency manifest and plugin manifest. The host supplies the SDK assembly.

The English WinUI settings page was manually exercised on 2026-09-12: create a profile, test an unsaved connection, fetch and select a model, select Responses, retain edits across profile switches, save once, and reopen after restarting the host. The saved model and connection survived the restart. Conditional temperature controls and the fixed save footer were also checked. Profile rows have an inset from the selection indicator; profiles without a stored key hide the remove-key action, and the default provider's setup status is omitted from the profile editor.

## Release acceptance still required

- Extend the native settings checks to switching profiles with an unsaved key, complete keyboard navigation, and the German layout.
- Run transcription and text processing against a selected compatible server, then verify an actual package update in an isolated profile.
- Ship the host connection-identity guard, profile editor and `IPluginProfileSettings` SDK capability before making this package available; set the published minimum host version to the release containing those changes.
- Complete the remaining side-by-side legacy regression and release checks in `docs/PLUGIN-MIGRATION-1.1.md` before publication.

The package has not been published and user settings have not been migrated.
