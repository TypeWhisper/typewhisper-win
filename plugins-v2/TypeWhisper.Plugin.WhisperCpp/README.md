# whisper.cpp (Local) portable plugin

Local whisper.cpp transcription with Whisper.net CPU/CUDA/Vulkan native runtimes and model management.

Version `1.2.0`; plugin ID `com.typewhisper.whisper-cpp`; minimum host `1.1.2`.
Independent branch: `seofood/whispercpp-portable`, based on `4db8f6ac`.

## Setup

Download a model explicitly, load it, and select the available acceleration backend. GPU capability and driver requirements must be checked on the target machine.

This package uses host-rendered portable settings and an independent WinUI data directory. Legacy settings, credentials and model files are not imported automatically.

## Source and platform scope

Protocol/runtime sources and applicable fixtures were snapshotted from `plugins/TypeWhisper.Plugin.WhisperCpp` at `4db8f6ac`, then adapted under `plugins-v2`. The package does not reference the legacy provider DLL or compile WPF settings views. Legacy sources, catalogs and published packages remain unchanged.

The macOS repository was compared at `ac00e39ea63e4789de8427d034d2085b3d898159`; it was not modified. The macOS counterpart uses WhisperKit/Apple runtime integrations. This package retains whisper.cpp for Windows. The tested package targets Windows x64; ARM64 redistribution is not validated and requires ARM64 VC runtime assets on the build machine.

## Build and verification

From this branch's repository root:

```powershell
dotnet msbuild plugins-v2/TypeWhisper.Plugin.WhisperCpp/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins-v2/TypeWhisper.Plugin.WhisperCpp/Tests -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.whisper-cpp` inside the plugin project. Package that directory as the ZIP root.

31 plugin tests pass. Model/runtime contracts, fake runtime download integrity and native package contents plus package lifecycle. No real model download, GPU execution or transcription was performed. All packages have isolated install, enable, restart, disable, uninstall and reinstall coverage through the real portable package loader and host services.

The ZIP was installed and loaded in the WinUI development profile, preserving existing installation receipts. No credentials were copied from the legacy profile.

The shared portable SDK/host suite passed 259 tests on the Live Transcript host branch. Automated fixture tests do not replace authenticated provider, native model/device, microphone or visual UI acceptance. Public catalog publication and production-profile migration are pending.
