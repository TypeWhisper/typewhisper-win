# Soniox portable plugin

Soniox live transcription over WebSocket, plus recorded-audio transcription with upload, Windows audio compression, polling, cleanup and region selection.

Version `1.3.1`; plugin ID `com.typewhisper.soniox`; minimum host `1.1.2`.
Independent branch: `seofood/soniox-portable`, based on `4db8f6ac`.

## Setup

Enter the API key, select the account region and choose Soniox in the transcription workflow. Enable Live transcription under Appearance for text during dictation. The existing model ID stays `default`; live capture uses `stt-rt-v5`, while recorded-audio requests and the host fallback retain `stt-async-v5`.

This package uses host-rendered portable settings and an independent WinUI data directory. Legacy settings, credentials and model files are not imported automatically.

## Source and platform scope

Protocol/runtime sources and applicable fixtures were snapshotted from `plugins/TypeWhisper.Plugin.Soniox` at `4db8f6ac`, then adapted under `plugins-v2`. The package does not reference the legacy provider DLL or compile WPF settings views. Legacy sources, catalogs and published packages remain unchanged.

The macOS repository was compared at `ac00e39ea63e4789de8427d034d2085b3d898159`; it was not modified. The newer macOS provider also offers streaming, translation and TTS. Version 1.3.0 adds real-time transcription using the same regional endpoints and final/non-final token semantics. Translation and TTS remain outside this Windows package.

## Build and verification

From this branch's repository root:

```powershell
dotnet msbuild plugins-v2/TypeWhisper.Plugin.Soniox/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins-v2/TypeWhisper.Plugin.Soniox/Tests -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.soniox` inside the plugin project. Package that directory as the ZIP root.

65 plugin tests pass, including streaming protocol, host preview/final assembly, fragmented responses, region configuration, interim replacement, cancellation, timeout, and typed failures. The original batch tests cover upload/poll/delete, metadata/language/region contracts, audio encoding, cancellation and package lifecycle. On 2026-09-18 the installed 1.2.0 package passed API-key validation and authenticated transcription of the synthetic English acceptance WAV using the existing development-profile credentials. It returned the complete test sentence and two segments. The harness used an isolated settings copy; no microphone audio was uploaded. All packages have isolated install, enable, restart, disable, uninstall and reinstall coverage through the real portable package loader and host services.

The ZIP was installed and loaded in the WinUI development profile, preserving existing installation receipts. No credentials were copied from the legacy profile.

The shared portable SDK/host suite passed 259 tests on the Live Transcript host branch. Automated fixture tests do not replace authenticated provider, native model/device, microphone or visual UI acceptance. Public catalog publication and production-profile migration are pending.

The host includes the Soniox logo. Shared draft editing and a single Save settings button are provided by host PR #479; provider-selection icons are provided by host PR #482. The host streaming-completion contract is implemented: unexpected close, incomplete tokens, cancellation, or missing completion cannot succeed with a partial transcript.

On 2026-09-19 (Europe/Berlin), Marco confirmed that dictation with Soniox works in the running development app. This confirms manual recorded-audio dictation acceptance of version 1.2.0 before adding live transcription.

## Live transcription validation

The real Soniox API was tested with paced synthetic audio through the actual `StreamingDictation` host pipeline. The first preview arrived after about 0.8 seconds, 22 updates arrived during capture, and the complete expected sentence was returned after the server confirmed completion, without batch fallback. The stream sends an empty **text** frame to end audio, matching Soniox's reference clients; an empty binary frame did not complete reliably in the live test. Credentials were read through the existing development secret store and were not logged. No microphone audio was used by this harness.

Confirmed subword tokens are concatenated until an endpoint or completed session, so the host does not insert spaces inside words. Interim tokens replace the current preview. Protocol sentinel tokens are excluded. The endpoint uses the selected US/EU/Japan region; the stream never silently changes regions.

References: [WebSocket API](https://soniox.com/docs/api-reference/stt/websocket-api), [real-time transcription](https://soniox.com/docs/stt/rt/real-time-transcription), [models](https://soniox.com/docs/stt/models), [regional endpoints](https://soniox.com/docs/data-residency).

On 2026-09-19 (Europe/Berlin), Marco also confirmed successful live dictation with the installed 1.3.0 package. The development UI showed Soniox selected and Live transcription enabled. The installed-package synthetic streaming check returned 22 live updates and the complete sentence without fallback.

Version 1.3.1 drains all outstanding batch cleanup tasks before deactivation/disposal, skips untimed tokens correctly in subtitle timing, and includes the Soniox PNG in published host output. The native Media Foundation encoding test runs only on Windows; the remaining tests run on both CI platforms.
