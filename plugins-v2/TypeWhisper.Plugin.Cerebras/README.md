# Cerebras for the portable host

Independent .NET 10 implementation of `com.typewhisper.cerebras`, version `1.1.1`, requiring host `1.1.2` or later. The package has no WPF references and does not import legacy settings or credentials. Protocol behavior was compared with the Windows provider at `21dc546a` and the [macOS Cerebras plugin](https://github.com/TypeWhisper/typewhisper-mac/tree/main/TypeWhisperPluginSDK/Plugins/CerebrasPlugin) on September 14, 2026.

## Features

- Text processing through `POST /v1/chat/completions`, with separate system/user messages and Unicode preserved.
- Authenticated model discovery through `GET /v1/models`. The initial catalog contains GPT-OSS 120B and Qwen 3.8 27B, following the current provider catalog. An explicit refresh replaces this list with the account's available models. Unknown discovered IDs are preserved.
- Default text model and provider-default or custom temperature from 0 to 2, matching the macOS settings. An explicit workflow model takes precedence. Known reasoning models receive the shared bounded output budget plus its reasoning reserve; reasoning text is never used as the answer.
- Host-rendered settings with one Save settings action, encrypted API-key storage/removal, draft connection checks, model refresh, English/German labels, and native Cerebras branding.

Opening settings performs no network requests. Refresh stages the catalog until Save settings. Failed or empty refreshes preserve the saved list. A refreshed catalog is bound to the key used for discovery; changing that key requires another refresh. Settings and the encrypted-key reference commit in one write, with failed writes retaining the preceding configuration. Removing a key disables the provider without deleting model preferences.

HTTP failures retain status and retry information; network failures, timeouts, cancellation, malformed responses, missing completion markers and truncated/filtered answers fail explicitly. HTTP 402 reports the need to check Cerebras billing and is not transient. Redirect following is disabled. Transcription, tools, streaming and per-request temperature overrides are not advertised.

## Verification

```powershell
dotnet test plugins-v2/TypeWhisper.Plugin.Cerebras/Tests/TypeWhisper.Plugin.Cerebras.Portable.Tests.csproj -c Release
dotnet test tests/TypeWhisper.PluginSDK.Portable.Tests/TypeWhisper.PluginSDK.Portable.Tests.csproj -c Release
./eng/Test-WinUILocalAudio.Tests.ps1
dotnet msbuild plugins-v2/TypeWhisper.Plugin.Cerebras/portable.proj '-t:Build;CopyPackage' -p:Configuration=Release -p:PluginDestination=<staging-directory>
```

All 43 provider/package tests and 259 portable SDK/host tests passed. The first shared-suite run encountered a Windows DLL cleanup lock in an existing fixture; a subsequent unchanged run passed all 259 tests. The package test exercises the real immutable store and runtime: ZIP hash/identity, installation, grouped configuration, restart, duplicate-install rejection, uninstall/reinstall, retained secrets/settings and minimum host version. The package contains only the provider DLL, dependency manifest and plugin manifest. Build and test discovery already includes `plugins-v2`.

The existing [local audio acceptance script](../../docs/WINUI-LOCAL-AUDIO-TEST.md) now accepts an explicit workflow ID and expected final text. Seven headless lifecycle cases verify orchestration, including workflow selection, final-output mismatches, recording cleanup and model restoration. This enables a future physical microphone → transcription provider → Cerebras workflow → Notepad check.

## Development acceptance and limits

The prescribed Windows development helper built and launched this checkout. The package was installed and enabled through the real development-profile store, preserving the preceding plugin receipts. Marco saved the key through the native settings page. Authenticated connection validation and model discovery succeeded; the returned catalog contained `gemma-4-31b`, `gpt-oss-120b` and `qwen-3.8-27b`.

Real text requests through the installed `1.1.0` package were attempted with GPT-OSS 120B and Qwen 3.8 27B. Both returned HTTP 402 (`payment_required`, quota). No successful real inference is claimed. Marco chose not to change billing or credentials, so no further inference or microphone workflow requests were made. Local evidence is in ignored `artifacts/cerebras/`; no credentials, profile exports or recordings belong in Git.

Version `1.1.1` adds a clear non-transient billing failure and stricter completion validation. The development update from `1.1.0` was staged through the immutable store and promoted on host restart without warnings. Settings and encrypted-secret file hashes remained unchanged, as did all unrelated installation receipts. Native dark-theme settings, the logo, saved state, conditional temperature field and draft Save behavior were verified. See the [screenshots and acceptance notes](../../docs/screenshots/cerebras/README.md). The original temperature mode was restored without saving the draft.

Successful live inference, physical microphone-to-paste execution, native light-theme/German layout checks, ARM64 execution and installed legacy/v2 side-by-side acceptance remain pending. No public catalog or release was changed. Legacy provider source, manifests and projects remain unchanged.

## Provider references

- [Chat completions](https://inference-docs.cerebras.ai/api-reference/chat-completions)
- [List models](https://inference-docs.cerebras.ai/api-reference/models/list-models)
- [Current model catalog](https://inference-docs.cerebras.ai/models/overview)
- [Reasoning output](https://inference-docs.cerebras.ai/capabilities/reasoning)
