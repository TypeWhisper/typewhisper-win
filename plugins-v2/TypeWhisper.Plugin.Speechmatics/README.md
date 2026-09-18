# Speechmatics for the portable host

Independent .NET 10 package `com.typewhisper.speechmatics`, version `1.1.0`, requiring host `1.1.2`. Implemented on its own `seofood/speechmatics-portable` branch, based directly on Windows `4db8f6ac`. No other migration branch is required. Legacy code, projects, manifests and published catalogs remain unchanged.

## Behavior and macOS comparison

Ports standard/enhanced accuracy, regional batch endpoints and dictionary vocabulary from Mac. Uses asynchronous jobs with bounded status polling, cancellation, transcript parsing and punctuation handling.

The corresponding Mac sources were inspected at `ac00e39e` in `TypeWhisperPluginSDK/Plugins/`. Mac realtime WebSocket streaming is not included. The package advertises batch transcription only; recordings/jobs follow provider retention.

Setup: **Speechmatics API key; EU or US region**. Settings are rendered by the host in English/German. API keys use the host secret store, with a staged encrypted-key reference and one configuration commit. Failed writes keep the active configuration. Removing a key retains nonsecret preferences. No legacy credentials or settings are imported. Redirects are disabled and provider HTTP failures retain status/retry metadata. Opening the settings page sends no network request.

## Verification

```powershell
dotnet test plugins-v2/TypeWhisper.Plugin.Speechmatics/Tests/TypeWhisper.Plugin.Speechmatics.Portable.Tests.csproj -c Release
dotnet msbuild plugins-v2/TypeWhisper.Plugin.Speechmatics/portable.proj '-t:Build;CopyPackage' -p:Configuration=Release -p:PluginDestination=<staging-directory>
```

All **20 provider tests passed**. The provider test suite covers protocol requests/responses, HTTP errors, malformed JSON, cancellation, key persistence/failure/removal, host settings rendering and independent ZIP installation/configuration/restart/uninstall/reinstall through the immutable portable store. The resulting package contains only the provider DLL, dependency manifest and plugin manifest, with no WPF dependencies. The unchanged portable SDK/host baseline passed all 259 tests in the Gemini checkout.

On 2026-09-18, the ZIP was installed in the Windows development profile and loaded with the real portable host services and Windows secret-store implementation. Settings were read successfully and the plugin was enabled. Existing unrelated package receipts were preserved. The WinUI development build and launch succeeded. No authenticated provider requests were sent. Native visual inspection was unavailable because the computer-use service could not connect.

Authenticated provider requests, microphone/workflow execution, native visual inspection, version-upgrade acceptance and ARM64 execution remain pending. Marco will enter credentials and perform live acceptance later. No public package or catalog was published.

Reference: [provider documentation](https://docs.speechmatics.com/).
