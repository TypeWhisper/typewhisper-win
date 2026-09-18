# xAI / Grok portable plugin

xAI batch/realtime transcription, Responses LLM completion and TTS with voice/output-device selection.

Version `1.2.0`; plugin ID `com.typewhisper.xai`; minimum host `1.1.2`.
Independent branch: `seofood/xai-portable`, based on `4db8f6ac`.

## Setup

Enter the API key, refresh available models/voices as needed and select xAI in transcription, LLM or speech settings.

This package uses host-rendered portable settings and an independent WinUI data directory. Legacy settings, credentials and model files are not imported automatically.

## Source and platform scope

Protocol/runtime sources and applicable fixtures were snapshotted from `plugins/TypeWhisper.Plugin.Xai` at `4db8f6ac`, then adapted under `plugins-v2`. The package does not reference the legacy provider DLL or compile WPF settings views. Legacy sources, catalogs and published packages remain unchanged.

The macOS repository was compared at `ac00e39ea63e4789de8427d034d2085b3d898159`; it was not modified. Compared with the macOS provider. The Windows package retains the existing cloud protocols and replaces legacy playback with WASAPI. Streaming completion waits for transcript.done and reports one final result.

## Build and verification

From this branch's repository root:

```powershell
dotnet msbuild plugins-v2/TypeWhisper.Plugin.Xai/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins-v2/TypeWhisper.Plugin.Xai/Tests -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.xai` inside the plugin project. Package that directory as the ZIP root.

17 plugin tests pass. Fake HTTP, voice/model contracts, real local WebSocket finalization/premature close and package lifecycle. No authenticated text/audio request or audible playback was performed. All packages have isolated install, enable, restart, disable, uninstall and reinstall coverage through the real portable package loader and host services.

The ZIP was installed and loaded in the WinUI development profile, preserving existing installation receipts. No credentials were copied from the legacy profile.

The shared portable SDK/host suite passed 259 tests on the Live Transcript host branch. Automated fixture tests do not replace authenticated provider, native model/device, microphone or visual UI acceptance. Public catalog publication and production-profile migration are pending.
