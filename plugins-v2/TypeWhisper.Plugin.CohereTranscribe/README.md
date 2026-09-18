# Cohere Transcribe (Local) portable plugin

Local CrispASR transcription with managed runtime/model downloads and download requirements.

Version `1.2.0`; plugin ID `com.typewhisper.cohere-transcribe`; minimum host `1.1.2`.
Independent branch: `seofood/coheretranscribe-portable`, based on `4db8f6ac`.

## Setup

Select the engine, review its download requirements, download the model/runtime and then load it. Downloads and inference still require acceptance on the target machine.

This package uses host-rendered portable settings and an independent WinUI data directory. Legacy settings, credentials and model files are not imported automatically.

## Source and platform scope

Protocol/runtime sources and applicable fixtures were snapshotted from `plugins/TypeWhisper.Plugin.CohereTranscribe` at `4db8f6ac`, then adapted under `plugins-v2`. The package does not reference the legacy provider DLL or compile WPF settings views. Legacy sources, catalogs and published packages remain unchanged.

The macOS repository was compared at `ac00e39ea63e4789de8427d034d2085b3d898159`; it was not modified. Compared with macOS CohereLocal: this package uses the Windows CrispASR runtime and Windows process isolation, not the macOS runtime assets.

## Build and verification

From this branch's repository root:

```powershell
dotnet msbuild plugins-v2/TypeWhisper.Plugin.CohereTranscribe/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins-v2/TypeWhisper.Plugin.CohereTranscribe/Tests -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.cohere-transcribe` inside the plugin project. Package that directory as the ZIP root.

51 plugin tests pass. Runtime provisioning, archive/path validation, download credentials, process contracts and package lifecycle. No real runtime/model download or inference was performed. All packages have isolated install, enable, restart, disable, uninstall and reinstall coverage through the real portable package loader and host services.

The ZIP was installed and loaded in the WinUI development profile, preserving existing installation receipts. No credentials were copied from the legacy profile.

The shared portable SDK/host suite passed 259 tests on the Live Transcript host branch. Automated fixture tests do not replace authenticated provider, native model/device, microphone or visual UI acceptance. Public catalog publication and production-profile migration are pending.
