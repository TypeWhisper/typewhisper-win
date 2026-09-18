# Smallest AI Pulse portable plugin

Smallest.ai Pulse batch and realtime transcription with portable API-key configuration.

Version `1.2.0`; plugin ID `com.typewhisper.smallest-ai`; minimum host `1.1.2`.
Independent branch: `seofood/smallestai-portable`, based on `4db8f6ac`.

## Setup

Enter the API key, select Pulse as the transcription engine and run a recording after account setup.

This package uses host-rendered portable settings and an independent WinUI data directory. Legacy settings, credentials and model files are not imported automatically.

## Source and platform scope

Protocol/runtime sources and applicable fixtures were snapshotted from `plugins/TypeWhisper.Plugin.SmallestAi` at `4db8f6ac`, then adapted under `plugins-v2`. The package does not reference the legacy provider DLL or compile WPF settings views. Legacy sources, catalogs and published packages remain unchanged.

The macOS repository was compared at `ac00e39ea63e4789de8427d034d2085b3d898159`; it was not modified. The existing Windows Pulse protocol was compared with macOS; the portable version keeps the Windows language/audio conventions.

## Build and verification

From this branch's repository root:

```powershell
dotnet msbuild plugins-v2/TypeWhisper.Plugin.SmallestAi/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins-v2/TypeWhisper.Plugin.SmallestAi/Tests -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.smallest-ai` inside the plugin project. Package that directory as the ZIP root.

18 plugin tests pass. Fake HTTP and actual local WebSocket tests including an empty final terminal message, premature close and package lifecycle. All packages have isolated install, enable, restart, disable, uninstall and reinstall coverage through the real portable package loader and host services.

The ZIP was installed and loaded in the WinUI development profile, preserving existing installation receipts. No credentials were copied from the legacy profile.

The shared portable SDK/host suite passed 259 tests on the Live Transcript host branch. Automated fixture tests do not replace authenticated provider, native model/device, microphone or visual UI acceptance. Public catalog publication and production-profile migration are pending.
