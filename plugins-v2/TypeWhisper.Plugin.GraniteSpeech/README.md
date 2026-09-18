# IBM Granite Speech (Local) portable plugin

Local Granite Speech transcription with the Windows Python/ONNX sidecar, model management and packaged scripts.

Version `1.2.0`; plugin ID `com.typewhisper.granite-speech`; minimum host `1.1.2`.
Independent branch: `seofood/granitespeech-portable`, based on `4db8f6ac`.

## Setup

Download and load the model through the engine controls. The managed embedded Python runtime requires Windows x64.

This package uses host-rendered portable settings and an independent WinUI data directory. Legacy settings, credentials and model files are not imported automatically.

## Source and platform scope

Protocol/runtime sources and applicable fixtures were snapshotted from `plugins/TypeWhisper.Plugin.GraniteSpeech` at `4db8f6ac`, then adapted under `plugins-v2`. The package does not reference the legacy provider DLL or compile WPF settings views. Legacy sources, catalogs and published packages remain unchanged.

The macOS repository was compared at `ac00e39ea63e4789de8427d034d2085b3d898159`; it was not modified. The Windows ONNX/Python backend is retained. macOS MLX implementation and live-streaming behavior are not ported.

## Build and verification

From this branch's repository root:

```powershell
dotnet msbuild plugins-v2/TypeWhisper.Plugin.GraniteSpeech/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins-v2/TypeWhisper.Plugin.GraniteSpeech/Tests -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.granite-speech` inside the plugin project. Package that directory as the ZIP root.

6 plugin tests pass. Portable settings, model contract, script packaging and package lifecycle. Actual Python provisioning, model download and inference remain pending. All packages have isolated install, enable, restart, disable, uninstall and reinstall coverage through the real portable package loader and host services.

The ZIP was installed and loaded in the WinUI development profile, preserving existing installation receipts. No credentials were copied from the legacy profile.

The shared portable SDK/host suite passed 259 tests on the Live Transcript host branch. Automated fixture tests do not replace authenticated provider, native model/device, microphone or visual UI acceptance. Public catalog publication and production-profile migration are pending.
