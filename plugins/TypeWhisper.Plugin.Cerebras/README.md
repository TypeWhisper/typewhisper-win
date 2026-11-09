# Cerebras for the portable host

Portable .NET 10 implementation of `com.typewhisper.cerebras`, version `1.2.0`, requiring host `1.1.2` or later.

## Features

- Text processing through `POST /v1/chat/completions`, with separate system/user messages and Unicode preserved.
- Authenticated model discovery through `GET /v1/models`. The initial catalog contains GPT-OSS 120B and Qwen 3.8 27B, following the current provider catalog. A refresh replaces this list with the account's available models. Unknown discovered IDs are preserved.
- The saved list reloads in the background on activation and after a key change once it is a day old, has no fetch date (lists saved before 1.2.0) or has a date in the future. While the plugin is active, it checks again every hour, so a list that ages while the app keeps running reloads and a failed refresh is retried. The request never blocks activation, and the saved list stays in use until a complete catalog replaces it. A failed or empty refresh keeps it and logs a warning without the key or the response. A new key discards the previous account's list and refreshes with the new key. The fetch date is saved with the list in the `configuration` setting (`ModelsFetchedAtUtc`); a damaged date counts as missing.
- Default text model and provider-default or custom temperature from 0 to 2, matching the macOS settings. An explicit workflow model takes precedence. When the list does not offer the saved default model, GPT-OSS 120B is used, then Qwen 3.8 27B, then the first listed model; the saved choice is kept and applies again once a later list offers it. Known reasoning models receive the shared bounded output budget plus its reasoning reserve; reasoning text is never used as the answer.
- Host-rendered settings with one Save settings action, encrypted API-key storage/removal, draft connection checks, model refresh, English/German labels, and native Cerebras branding.

Opening settings performs no network requests. Refresh models stages the catalog until Save settings, and saving records when it was fetched. The background refresh never changes that draft: Save keeps the draft because the user fetched it, while a Save without a draft keeps whatever list is saved at that moment, including one the background refresh saved while the settings were open. Failed or empty refreshes preserve the saved list. A refreshed catalog is bound to the key used for discovery; changing that key requires another refresh. Settings and the encrypted-key reference commit in one write, with failed writes retaining the preceding configuration. Removing a key disables the provider and discards the account's model list without deleting model preferences.

HTTP failures retain status and retry information; network failures, timeouts, cancellation, malformed responses, missing completion markers and truncated/filtered answers fail explicitly. HTTP 402 reports the need to check Cerebras billing and is not transient. Redirect following is disabled. Transcription, tools, streaming and per-request temperature overrides are not advertised.

## Verification

```powershell
dotnet test plugins/TypeWhisper.Plugin.Cerebras/Tests/TypeWhisper.Plugin.Cerebras.Portable.Tests.csproj -c Release
dotnet test tests/TypeWhisper.PluginSDK.Portable.Tests/TypeWhisper.PluginSDK.Portable.Tests.csproj -c Release
./eng/Test-WinUILocalAudio.Tests.ps1
dotnet msbuild plugins/TypeWhisper.Plugin.Cerebras/portable.proj '-t:Build;CopyPackage' -p:Configuration=Release -p:PluginDestination=<staging-directory>
```

The package test exercises the real immutable store and runtime: ZIP hash/identity, installation, grouped configuration, restart, duplicate-install rejection, uninstall/reinstall, retained secrets/settings and minimum host version. The test project sets the `TypeWhisper.Plugins.DisableAutomaticModelRefresh` switch, so the packaged plugin's background refresh stays off and no request leaves the machine. Unit tests use the internal constructor with the background refresh off, or turn it on with a fake transport and clock. The package contains only the provider DLL, dependency manifest and plugin manifest. Build and test discovery already includes `plugins`.

Use the [local audio acceptance script](../../docs/WINUI-LOCAL-AUDIO-TEST.md) with an explicit workflow ID and expected final text to check microphone → transcription → Cerebras workflow → Notepad delivery.

## Validation limits

Live inference and microphone-to-paste acceptance require an account with available quota. HTTP 402 is a billing failure. Validate native light-theme/German layouts, ARM64 execution and installed 1.0/1.1 coexistence separately. See the [settings screenshots](../../docs/screenshots/cerebras/README.md).

## Provider references

- [Chat completions](https://inference-docs.cerebras.ai/api-reference/chat-completions)
- [List models](https://inference-docs.cerebras.ai/api-reference/models/list-models)
- [Current model catalog](https://inference-docs.cerebras.ai/models/overview)
- [Reasoning output](https://inference-docs.cerebras.ai/capabilities/reasoning)
