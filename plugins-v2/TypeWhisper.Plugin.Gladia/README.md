# Gladia for the portable host

Independent .NET 10 package `com.typewhisper.gladia`, version `1.1.0`, requiring host `1.1.2`. Implemented on its own `seofood/gladia-portable` branch, based directly on Windows `4db8f6ac`. No other migration branch is required. Legacy code, projects, manifests and published catalogs remain unchanged.

## Behavior and macOS comparison

Corrects the legacy single-request assumption: upload audio, submit an asynchronous transcription job and poll its result. Ports ordered language hints/code switching and the Mac custom-vocabulary configuration, with bounded polling and cancellation.

The corresponding Mac sources were inspected at `ac00e39e` in `TypeWhisperPluginSDK/Plugins/`. The Mac live WebSocket path is not included. This package advertises recorded-audio transcription only. Uploaded recordings follow the provider retention policy; no automatic remote deletion is claimed.

Setup: **Gladia API key**. Settings are rendered by the host in English/German. API keys use the host secret store, with a staged encrypted-key reference and one configuration commit. Failed writes keep the active configuration. Removing a key retains nonsecret preferences. No legacy credentials or settings are imported. Redirects are disabled and provider HTTP failures retain status/retry metadata. Opening the settings page sends no network request.

## Verification

```powershell
dotnet test plugins-v2/TypeWhisper.Plugin.Gladia/Tests/TypeWhisper.Plugin.Gladia.Portable.Tests.csproj -c Release
dotnet msbuild plugins-v2/TypeWhisper.Plugin.Gladia/portable.proj '-t:Build;CopyPackage' -p:Configuration=Release -p:PluginDestination=<staging-directory>
```

All **20 provider tests passed**. The provider test suite covers protocol requests/responses, HTTP errors, malformed JSON, cancellation, key persistence/failure/removal, host settings rendering and independent ZIP installation/configuration/restart/uninstall/reinstall through the immutable portable store. The resulting package contains only the provider DLL, dependency manifest and plugin manifest, with no WPF dependencies. The unchanged portable SDK/host baseline passed all 259 tests in the Gemini checkout.

On 2026-09-19, all 20 provider tests passed again. The installed 1.1.0 package loaded through the real portable host using the development profile's Windows secret store. Authenticated configuration validation and recorded-audio transcription passed with a short synthetic English WAV. The result was: "This is a short test. Tomorrow we will meet at 10 in the office."

The combined development app was inspected visually: Gladia branding, a saved-key placeholder, the model selector and the shared Save settings action are present. The host-side Gladia logo mapping and SVG are included on this independent branch; the shared Save behavior comes from #479 and provider-selection logos from #482. Gladia was selected for the next manual dictation test. No credential values are included in the screenshot.

Microphone/end-to-end manual acceptance, version-upgrade acceptance and ARM64 execution remain pending. Realtime streaming is not implemented in this package. No public package or catalog was published.

![Gladia settings in the combined Windows development app](../../docs/screenshots/gladia/settings-dark.png)

Reference: [provider documentation](https://docs.gladia.io/api-reference/v2/transcription/post).
