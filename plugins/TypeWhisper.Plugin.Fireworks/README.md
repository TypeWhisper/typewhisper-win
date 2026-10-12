# Fireworks AI for the portable host

Portable .NET 10 package `com.typewhisper.fireworks`, version `1.1.8`, requiring host `1.1.5`.

## Behavior

Adds Whisper V3/V3 Turbo transcription with the model-specific audio endpoints, translation and dictionary prompts. Text processing uses the correct `/inference/v1/chat/completions` path, selected/custom model IDs, temperature and native/OpenAI model catalog discovery.

Mac streaming is REST preview polling rather than the Windows WebSocket session contract; this package advertises recorded-audio transcription only. No live streaming is claimed.

Setup: **API key**. Settings are rendered by the host in English/German. API keys use the host secret store, with a staged encrypted-key reference and one configuration commit. Failed writes keep the active configuration. Removing a key retains nonsecret preferences. Redirects are disabled and provider HTTP failures retain status/retry metadata. Opening the settings page sends no network request.

## Verification

```powershell
dotnet test plugins/TypeWhisper.Plugin.Fireworks/Tests/TypeWhisper.Plugin.Fireworks.Portable.Tests.csproj -c Release
dotnet msbuild plugins/TypeWhisper.Plugin.Fireworks/portable.proj '-t:Build;CopyPackage' -p:Configuration=Release -p:PluginDestination=<staging-directory>
```

The provider test suite covers protocol requests/responses, HTTP errors, malformed JSON, cancellation, key persistence/failure/removal, host settings rendering and independent ZIP installation/configuration/restart/uninstall/reinstall through the immutable portable store. The resulting package contains only the provider DLL, dependency manifest and plugin manifest.

Validate native settings, workflow execution and ARM64 separately.

Reference: [provider documentation](https://docs.fireworks.ai/).

## Provider availability

Fireworks [deprecated audio inference on June 10, 2026](https://docs.fireworks.ai/updates/changelog#audio-inference-and-image-generation-deprecation). Audio endpoints may be unavailable even when a key works for text processing. Turbo availability is not a renewed provider support commitment. Settings explain this limitation and recorded-audio-only behavior. Fresh configurations default to Turbo and `gpt-oss-120b`; explicit saved selections remain intact.

Dictionary terms use the structured host contract 1.1.5 or newer, preserving literal commas inside each entry. Older plugin packages keep their original prompt contract.
