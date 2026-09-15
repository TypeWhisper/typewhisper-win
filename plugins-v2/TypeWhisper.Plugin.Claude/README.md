# Claude for the portable host

Independent .NET 10 implementation of `com.typewhisper.claude`, version `1.1.0`, requiring host `1.1.2` or later. Protocol behavior was compared with the Windows provider at `8631b9a0`, the macOS Claude plugin, and the official Anthropic API documentation on September 15, 2026.

## Features

- Text processing through Anthropic `POST /v1/messages`, with the required API version and API-key headers, separate system/user content, and a bounded output budget with reasoning reserve.
- Explicit `GET /v1/models` discovery follows pagination, preserves API order, deduplicates IDs and rejects incomplete catalogs, repeated cursors and more than 20 pages. Unknown model IDs remain usable. Known models have friendly names; other IDs are shown verbatim.
- The initial catalog offers Sonnet 5, Opus 5, Sonnet 4.6 and Haiku 4.5. Model refresh preserves an existing default even if the account no longer lists it. Explicit workflow model IDs take precedence and are never silently remapped. A model-not-found error directs the user to refresh and reselect.
- English/German host-rendered settings, connection checks using the entered key, model refresh, encrypted key storage/removal, a selected default model, and optional temperature. Temperature accepts 0–1 and is sent only to explicitly supported older models; newer or unknown models use the provider default, as explained in settings.
- Native Claude branding reuses the existing Windows image.

Only complete `end_turn` responses are delivered. All text blocks are concatenated; thinking and redacted-thinking blocks are excluded. Empty, malformed, truncated, refused, paused and tool-use responses fail explicitly. HTTP errors retain status, retry information and failure classification; timeouts and user cancellation stay distinct. Redirects are disabled.

Opening settings and activating the plugin are offline. Refresh stages a catalog until Save settings. Failed refreshes preserve the existing catalog; changed credentials require a new refresh. A refresh finishing after a key change, deactivation or newer action cannot stage obsolete results. Configuration and its encrypted-secret reference commit in one settings write, and failed writes retain the preceding configuration.

This is an API-key provider. Claude Code sign-in is part of the separate Authenticated CLI integration. This package does not advertise transcription, TTS, tools or streaming. It neither imports legacy data nor changes legacy source or catalogs. Compared with macOS, Windows uses explicit refresh instead of automatic background refresh, and does not expose per-request temperature overrides.

## Verification

```powershell
dotnet test plugins-v2/TypeWhisper.Plugin.Claude/Tests/TypeWhisper.Plugin.Claude.Portable.Tests.csproj -c Release
dotnet test tests/TypeWhisper.PluginSDK.Portable.Tests/TypeWhisper.PluginSDK.Portable.Tests.csproj -c Release
```

All 57 provider/package tests and 259 portable SDK/host tests passed. Tests cover request bodies and headers, Unicode, multi-block text, incomplete output, HTTP failures, cancellation, pagination bounds, stale refreshes, draft settings, persistence failures, default preservation, temperature compatibility and retired model errors.

The package lifecycle test uses the real immutable store and runtime to exercise ZIP/hash/identity validation, installation, activation, configuration, restart, duplicate-install rejection, uninstall/reinstall and minimum host version. The package contains the provider DLL, dependency manifest and plugin manifest, with no WPF references.

The release package was installed into the existing WinUI development profile through `PortablePluginStore`; all unrelated installation receipts were preserved. Local package and installation evidence are in ignored `artifacts/claude/`.

The prescribed development helper built and launched the WinUI checkout successfully. Existing setup-wizard comparison and license-source generator warnings were reported. The running app's accessibility tree exposes the Claude integration and image. Navigation input failed with `GetCursorPos failed: Access is denied (0x80070005)`, including one recovery attempt, so no visual layout or settings-interaction acceptance is claimed.

Authenticated provider requests and microphone-to-Claude workflow execution are deferred because no Anthropic account is available for acceptance testing. Fake-transport tests do not establish live provider behavior.

Native settings/layout checks, native update acceptance, ARM64 execution and installed legacy/v2 side-by-side acceptance remain pending. A later screenshot attempt also failed with `IGraphicsCaptureItemInterop.CreateForMonitor: Could not capture the given monitor (0x80070057)`, including one retry. No public package or catalog was published.

## References

- [Messages API](https://platform.claude.com/docs/en/api/messages/create)
- [Model discovery and pagination](https://platform.claude.com/docs/en/api/models/list)
- [Current model overview](https://platform.claude.com/docs/en/models/overview)
- [macOS Claude implementation](https://github.com/TypeWhisper/typewhisper-mac/tree/main/TypeWhisperPluginSDK/Plugins/ClaudePlugin)
