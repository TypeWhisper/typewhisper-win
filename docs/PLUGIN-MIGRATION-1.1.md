# Portable plugin development

The WinUI host is the only application. All portable implementations live under `plugins/`; WPF provider assemblies, settings views and the legacy plugin publishing workflow have been removed.

## Current source layout

- `plugins/`: all portable providers and internal components, with provider-owned projects, manifests, settings contracts and tests.
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

The source-directory consolidation does not rename the public `plugins-v2.json` feed. Existing 1.1 clients depend on that URL. Application upgrades from 1.0 use the profile migration described in [Daily delivery and migration](DAILY-1.1-CANDIDATE.md).
