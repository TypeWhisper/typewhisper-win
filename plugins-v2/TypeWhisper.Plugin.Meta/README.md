# Meta portable plugin

Meta transcription, realtime transcription, LLM completion, model refresh, language/dictionary/diarization and reasoning settings.

Version `1.2.10`; plugin ID `com.typewhisper.meta`; minimum host `1.1.5`.
Independent branch: `seofood/meta-portable`, based on `4db8f6ac`.

## Setup

Enter the API key, refresh models if needed and choose the engine/model in a workflow.

This package uses host-rendered portable settings and an independent WinUI data directory. Legacy settings, credentials and model files are not imported automatically.

## Source and platform scope

Protocol/runtime sources and applicable fixtures were snapshotted from `plugins/TypeWhisper.Plugin.Meta` at `4db8f6ac`, then adapted under `plugins-v2`. The package does not reference the legacy provider DLL or compile WPF settings views. Legacy sources, catalogs and published packages remain unchanged.

The macOS repository was compared at `ac00e39ea63e4789de8427d034d2085b3d898159`; it was not modified. Compared with macOS Meta provider sources; this preserves the Windows provider protocol surface and exposes it through portable host settings. Provider rollout/model availability still requires an authenticated check.

## Build and verification

From this branch's repository root:

```powershell
dotnet msbuild plugins-v2/TypeWhisper.Plugin.Meta/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins-v2/TypeWhisper.Plugin.Meta/Tests -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.meta` inside the plugin project. Package that directory as the ZIP root.

38 plugin tests pass. Fake HTTP protocol, key storage failure, real local WebSocket finalization/premature close and package lifecycle. Realtime completion emits one terminal final result. All packages have isolated install, enable, restart, disable, uninstall and reinstall coverage through the real portable package loader and host services.

The ZIP was installed and loaded in the WinUI development profile, preserving existing installation receipts. No credentials were copied from the legacy profile.

The shared portable SDK/host suite passed 259 tests on the Live Transcript host branch. Automated fixture tests do not replace authenticated provider, native model/device, microphone or visual UI acceptance. Public catalog publication and production-profile migration are pending.

## Current development validation

The settings page groups speaker labels under transcription and model/reasoning choices under text processing. Model choices come from the account API catalog; settings use the host’s shared save action. The host displays the Meta brand icon in navigation and provider selectors.

Authenticated validation on September 21, 2026 passed connection validation, model discovery (one transcription and five text models), German batch transcription, Muse Spark 1.3 Contributor text processing, and realtime transcription. The realtime fixture produced ten partial updates and one complete final transcript. Marco confirmed dictation and accepted the settings in the development app.

Diarization finalization drains all completed speaker turns and requires a clean WebSocket close after endStream. Loopback tests cover delayed final turns, abnormal closure, and missing turn completion.

Version 1.2.2 was additionally validated against the live API with speaker labels enabled: 13 interim updates, one complete final transcript, and clean stream closure.

Push-to-talk accepts the explicit final transcript after stop even when the server later closes the transport without a close frame. Diarization still drains all completed turns through clean stream closure; incomplete or prematurely closed responses remain errors.

Dictionary prompts follow the current shared SDK comma-separated contract. A phrase containing a literal comma cannot retain that internal boundary; lossless structured dictionary terms require a separate host/SDK change for all keyword-based providers.

Dictionary terms use the structured host contract 1.1.5 or newer, preserving literal commas inside each entry. Older plugin packages keep their original prompt contract.
