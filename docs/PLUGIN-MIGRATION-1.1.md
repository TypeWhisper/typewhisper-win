# Portable plugin development

The WinUI host is the only application. Portable implementations live under `plugins/` and `plugins-v2/`; WPF provider assemblies, settings views and the legacy plugin publishing workflow have been removed.

## Current source layout

- `plugins/`: Deepgram, FillerWords, Groq, Obsidian, SherpaOnnx and its internal ParakeetCtc component. These projects target `net10.0` only.
- `plugins-v2/`: independent portable providers, with provider-owned projects, manifests, settings contracts and tests.
- `src/TypeWhisper.PluginSDK`: UI-independent contracts. Settings are rendered by WinUI through those contracts.
- `src/TypeWhisper.PluginHost`: package verification, activation, configuration and lifecycle management.

Providers that existed only for WPF are no longer included. Their removal does not imply that equivalent WinUI functionality has been implemented. Existing published legacy packages and catalogs are not modified by source cleanup.

## Acceptance before publication

1. Build the portable project and run its independent tests, including requests, responses, errors and cancellation.
2. Verify the complete package, manifest, version and hash, and reject UI-framework dependencies.
3. Check that each advertised capability has a WinUI consumer and usable host-rendered configuration.
4. Use an isolated profile to install, configure, execute, restart, update, uninstall and reinstall the package.
5. Complete provider-specific native and live acceptance. Normal automated tests do not require accounts or paid requests.

See [plugin package documentation](PLUGIN-PACKAGES-1.1.md) for the package contract. Run `eng/Test-WinUIHeadless.ps1` for the host, application and provider suites. Published packages continue to use the v2 catalog and immutable package store.
