# Cloudflare Workers AI for the portable host

Independent .NET 10 package `com.typewhisper.cloudflare-asr`, version `1.1.1`, requiring host `1.1.2`. Implemented on its own `seofood/cloudflareasr-portable` branch, based directly on Windows `4db8f6ac`. No other migration branch is required. Legacy code, projects, manifests and published catalogs remain unchanged.

## Behavior and macOS comparison

Preserves direct Cloudflare Workers AI Whisper requests and adds portable account/token configuration, token validation, response failure checks and validated account IDs.

The corresponding Mac sources were inspected at `ac00e39e` in `TypeWhisperPluginSDK/Plugins/`. Mac `CloudflareASRPlugin` is a configurable OpenAI-compatible ASR proxy, not the direct Workers AI backend used by Windows. That proxy is already configurable with the portable OpenAI-compatible provider; this package retains the distinct Workers account/token workflow. No translation or streaming is claimed.

Setup: **Cloudflare API token and 32-character account ID**. Settings are rendered by the host in English/German. API keys use the host secret store, with a staged encrypted-key reference and one configuration commit. Failed writes keep the active configuration. Removing a key retains nonsecret preferences. No legacy credentials or settings are imported. Redirects are disabled and provider HTTP failures retain status/retry metadata. Opening the settings page sends no network request.

## Verification

```powershell
dotnet test plugins-v2/TypeWhisper.Plugin.CloudflareAsr/Tests/TypeWhisper.Plugin.CloudflareAsr.Portable.Tests.csproj -c Release
dotnet msbuild plugins-v2/TypeWhisper.Plugin.CloudflareAsr/portable.proj '-t:Build;CopyPackage' -p:Configuration=Release -p:PluginDestination=<staging-directory>
```

All **21 provider tests passed**. The provider test suite covers protocol requests/responses, HTTP errors, malformed JSON, cancellation, key persistence/failure/removal, host settings rendering and independent ZIP installation/configuration/restart/uninstall/reinstall through the immutable portable store. The resulting package contains only the provider DLL, dependency manifest and plugin manifest, with no WPF dependencies. The unchanged portable SDK/host baseline passed all 259 tests in the Gemini checkout.

On 2026-09-18, the ZIP was installed in the Windows development profile and loaded with the real portable host services and Windows secret-store implementation. Settings were read successfully and the plugin was enabled. Existing unrelated package receipts were preserved. The WinUI development build and launch succeeded. No authenticated provider requests were sent. Native visual inspection was unavailable because the computer-use service could not connect.

Authenticated provider requests and microphone transcription have not been tested because no Cloudflare account is available. On 2026-09-18, Marco authorized publication without that live account test. Package lifecycle and protocol fixtures provide automated coverage; this does not establish authenticated service acceptance. ARM64 execution remains untested.

The host includes the Cloudflare logo. Shared draft editing and the single Save settings button are provided by host PR #479; provider-selection icons are provided by host PR #482. The portable package does not require either host UI change to load. Publish only to the separate V2 catalog with an immutable package URL; the legacy release workflow builds the WPF package and must not be used for this port.

Reference: [provider documentation](https://developers.cloudflare.com/workers-ai/models/whisper/).
