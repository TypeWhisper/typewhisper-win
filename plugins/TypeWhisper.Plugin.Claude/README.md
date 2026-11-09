# Claude for the portable host

Portable .NET 10 implementation of `com.typewhisper.claude`, version `1.2.0`, requiring host `1.1.2` or later.

## Features

- Text processing through Anthropic `POST /v1/messages`, with the required API version and API-key headers, separate system/user content, and a bounded output budget with reasoning reserve.
- Explicit `GET /v1/models` discovery follows pagination, preserves API order, deduplicates IDs and rejects incomplete catalogs, repeated cursors and more than 20 pages. Unknown model IDs remain usable. Known models have friendly names; other IDs are shown verbatim.
- The initial catalog offers Sonnet 5, Opus 5, Sonnet 4.6 and Haiku 4.5. Model refresh preserves an existing default even if the account no longer lists it. Explicit workflow model IDs take precedence and are never silently remapped. A model-not-found error directs the user to refresh and reselect.
- English/German host-rendered settings, connection checks using the entered key, model refresh, encrypted key storage/removal, a selected default model, and optional temperature. Temperature accepts 0–1 and is sent only to explicitly supported older models; newer or unknown models use the provider default, as explained in settings.
- Native Claude branding reuses the existing Windows image.

Only complete `end_turn` responses are delivered. All text blocks are concatenated; thinking and redacted-thinking blocks are excluded. Empty, malformed, truncated, refused, paused and tool-use responses fail explicitly. HTTP errors retain status, retry information and failure classification; timeouts and user cancellation stay distinct. Redirects are disabled.

Opening settings is offline. Activation and a key change reload the saved catalog in the background once it is a day old, has no fetch date (lists saved before 1.2.0), or has a date in the future; the request never blocks activation. The saved list stays in use until a complete catalog replaces it, and a failed or empty refresh keeps it and logs a warning without the key or the response. A new key discards the previous account's list and refreshes with the new key; the selected default model stays. The fetch date is saved with the list in the `configuration` setting (`ModelsFetchedAtUtc`); a damaged date counts as missing.

Refresh models stages a catalog until Save settings, and saving records when it was fetched. The background refresh never changes that draft: Save keeps the draft because the user fetched it, while a Save without a draft keeps whatever list is saved at that moment, including one the background refresh saved while the settings were open. Failed refreshes preserve the existing catalog; changed credentials require a new refresh. A refresh finishing after a key change, deactivation or newer action cannot stage or save obsolete results. Configuration and its encrypted-secret reference commit in one settings write, and failed writes retain the preceding configuration.

This is an API-key provider. Claude Code sign-in is part of the separate Authenticated CLI integration. This package does not advertise transcription, TTS, tools or streaming. Compared with macOS, Windows does not expose per-request temperature overrides.

## Verification

```powershell
dotnet test plugins/TypeWhisper.Plugin.Claude/Tests/TypeWhisper.Plugin.Claude.Portable.Tests.csproj -c Release
dotnet test tests/TypeWhisper.PluginSDK.Portable.Tests/TypeWhisper.PluginSDK.Portable.Tests.csproj -c Release
```

Tests cover request bodies and headers, Unicode, multi-block text, incomplete output, HTTP failures, cancellation, pagination bounds, stale refreshes, draft settings, persistence failures, default preservation, temperature compatibility, retired model errors, and the background refresh: staleness, key changes, late results, failures, the draft rule and damaged dates. Tests use the internal constructor with the background refresh off, or turn it on with a fake transport and clock.

The package lifecycle test uses the real immutable store and runtime to exercise ZIP/hash/identity validation, installation, activation, configuration, restart, duplicate-install rejection, uninstall/reinstall and minimum host version. The package contains the provider DLL, dependency manifest and plugin manifest. The test project sets the `TypeWhisper.Plugins.DisableAutomaticModelRefresh` switch, so the packaged plugin's background refresh stays off and no request leaves the machine.

Authenticated provider requests and microphone-to-Claude workflow execution are deferred because no Anthropic account is available for acceptance testing. Fake-transport tests do not establish live provider behavior.

Validate light-theme/German layouts, package updates, ARM64 execution and installed 1.0/1.1 coexistence separately. See the [settings screenshots](../../docs/screenshots/claude/README.md).

## References

- [Messages API](https://platform.claude.com/docs/en/api/messages/create)
- [Model discovery and pagination](https://platform.claude.com/docs/en/api/models/list)
- [Current model overview](https://platform.claude.com/docs/en/models/overview)
- [macOS Claude implementation](https://github.com/TypeWhisper/typewhisper-mac/tree/main/TypeWhisperPluginSDK/Plugins/ClaudePlugin)
