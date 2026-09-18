# Fireworks AI for the portable host

Independent .NET 10 package `com.typewhisper.fireworks`, version `1.1.6`, requiring host `1.1.2`. Implemented on its own `seofood/fireworks-portable` branch, based directly on Windows `4db8f6ac`. No other migration branch is required. Legacy code, projects, manifests and published catalogs remain unchanged.

## Behavior and macOS comparison

Adds Whisper V3/V3 Turbo transcription with the model-specific audio endpoints, translation and dictionary prompts. Text processing uses the correct `/inference/v1/chat/completions` path, selected/custom model IDs, temperature and native/OpenAI model catalog discovery.

The corresponding macOS provider behavior was compared. Mac streaming is REST preview polling rather than the Windows WebSocket session contract; this package advertises recorded-audio transcription only. No live streaming is claimed.

Setup: **API key**. Settings are rendered by the host in English/German. API keys use the host secret store, with a staged encrypted-key reference and one configuration commit. Failed writes keep the active configuration. Removing a key retains nonsecret preferences. No legacy credentials or settings are imported. Redirects are disabled and provider HTTP failures retain status/retry metadata. Opening the settings page sends no network request.

## Verification

```powershell
dotnet test plugins-v2/TypeWhisper.Plugin.Fireworks/Tests/TypeWhisper.Plugin.Fireworks.Portable.Tests.csproj -c Release
dotnet msbuild plugins-v2/TypeWhisper.Plugin.Fireworks/portable.proj '-t:Build;CopyPackage' -p:Configuration=Release -p:PluginDestination=<staging-directory>
```

All **30 provider tests passed**. The provider test suite covers protocol requests/responses, HTTP errors, malformed JSON, cancellation, key persistence/failure/removal, host settings rendering and independent ZIP installation/configuration/restart/uninstall/reinstall through the immutable portable store. The resulting package contains only the provider DLL, dependency manifest and plugin manifest, with no WPF dependencies. The unchanged portable SDK/host baseline passed all 259 tests in the Gemini checkout.

On 2026-09-18, the ZIP was installed in the Windows development profile and loaded with the real portable host services and Windows secret-store implementation. Settings were read successfully and the plugin was enabled. Existing unrelated package receipts were preserved. The WinUI development build and launch succeeded. Authenticated connection checks, discovery of 25 text models, text completion with `gpt-oss-120b`, and synthetic English transcription with Whisper V3 Turbo passed. Marco also confirmed Turbo dictation in the app. The encrypted key survived the development package upgrade. The host now includes the official Fireworks icon. Native visual inspection was unavailable because the computer-use service could not connect.

Native visual inspection, broader workflow acceptance and ARM64 execution remain pending. No public package or catalog was published.

Reference: [provider documentation](https://docs.fireworks.ai/).

## Provider availability

Fireworks [deprecated audio inference on June 10, 2026](https://docs.fireworks.ai/updates/changelog#audio-inference-and-image-generation-deprecation). On September 18, the standard Whisper V3 endpoint returned HTTP 401 with the same valid key accepted by model discovery, text completion and the Turbo endpoint. Whisper V3 Turbo still worked in both automated live and manual dictation tests, but should not be interpreted as a renewed provider support commitment. The settings explain this limitation and recorded-audio-only behavior. Fresh configurations default to the tested Turbo and `gpt-oss-120b` models; explicit saved selections remain intact. The old DeepSeek V3.1 default returned HTTP 404 and was replaced.
