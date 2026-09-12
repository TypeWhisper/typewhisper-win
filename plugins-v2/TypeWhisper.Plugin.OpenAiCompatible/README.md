# OpenAI Compatible for the portable host

Independent .NET 10 port of the Windows provider at `3282e9ca`, with host-rendered settings instead of WPF views. The logical plugin ID remains `com.typewhisper.openai-compatible`; package version is `1.1.0`.

Each server profile has its own URL, encrypted key, model catalog, transcription model, text model, thinking preference and text request timeout. A key is optional for local servers. Model discovery is explicit, preserves manual model IDs and retains the saved catalog when the server returns no models. Additional profiles expose stable selection IDs to the host and workflows.

The settings page selects which profile its key controls operate on. The host clears unsaved key input when `IPluginConnectionSettings.ConnectionIdentity` changes. Text field and action IDs include their profile identity, so delayed saves from another profile are rejected. The default profile cannot be removed.

The protocol port retains translation, UTF-8 message bodies, truncation checks, caller cancellation, independent profile timeouts and DeepInfra-specific `reasoning_effort`. Local/private endpoints do not opt into duplicate request hedging. Model IDs remain editable because compatible servers do not consistently classify their catalog by capability.

## Isolation

The project references the portable SDK and contains no WPF references. It neither links legacy provider source nor reads historical profile directories. Fresh profiles do not import the legacy flat settings. The legacy project, manifest, feed and packages remain unchanged. Credentials are stored only through the host secret store, scoped by profile.

## Verification

```powershell
dotnet test plugins-v2/TypeWhisper.Plugin.OpenAiCompatible/Tests/TypeWhisper.Plugin.OpenAiCompatible.Portable.Tests.csproj -c Release
dotnet test tests/TypeWhisper.PluginSDK.Portable.Tests/TypeWhisper.PluginSDK.Portable.Tests.csproj -c Release
```

The plugin suite covers transport behavior, profile/key isolation, failed persistence, cancellation, URL validation, manual models, failed catalog refreshes, and isolated package install/restart/uninstall/reinstall with both provider roles. Network responses and keys are fixtures; no paid provider requests are made.

Build output is staged under `bin/Release/portable-host/Plugins/com.typewhisper.openai-compatible/`, including the assembly, dependency manifest and plugin manifest. The host supplies the SDK assembly.

## Release acceptance still required

- Build and inspect the native settings page, including switching profiles with an unsaved key, keyboard navigation and both German and English layouts.
- Run transcription and text processing against a selected compatible server, then verify an actual package update in an isolated profile.
- Ship the host connection-identity guard before making this package available; set the published minimum host version to the release containing that guard.
- Complete the remaining side-by-side legacy regression and release checks in `docs/PLUGIN-MIGRATION-1.1.md` before publication.

The package has not been published and user settings have not been migrated.
