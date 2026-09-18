# File Memory portable plugin

Local memory storage, search, listing and exact-entry deletion through portable settings; a workflow action stores its input as memory.

Version `1.2.0`; plugin ID `com.typewhisper.file-memory`; minimum host `1.1.2`.
Independent branch: `seofood/filememory-portable`, based on `4db8f6ac`.

## Setup

Add entries through settings or attach the store-memory action to a workflow. Use the query/search action to inspect stored entries.

This package uses host-rendered portable settings and an independent WinUI data directory. Legacy settings, credentials and model files are not imported automatically.

## Source and platform scope

Protocol/runtime sources and applicable fixtures were snapshotted from `plugins/TypeWhisper.Plugin.FileMemory` at `4db8f6ac`, then adapted under `plugins-v2`. The package does not reference the legacy provider DLL or compile WPF settings views. Legacy sources, catalogs and published packages remain unchanged.

The macOS repository was compared at `ac00e39ea63e4789de8427d034d2085b3d898159`; it was not modified. This exposes the Windows memory backend through actual host settings/actions. It does not add the macOS automatic LLM extraction/recall pipeline.

## Build and verification

From this branch's repository root:

```powershell
dotnet msbuild plugins-v2/TypeWhisper.Plugin.FileMemory/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins-v2/TypeWhisper.Plugin.FileMemory/Tests -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.file-memory` inside the plugin project. Package that directory as the ZIP root.

6 plugin tests pass. Persistence, search, deduplication, deletion, restart, malformed-file protection, cancellation, workflow action and package lifecycle. All packages have isolated install, enable, restart, disable, uninstall and reinstall coverage through the real portable package loader and host services.

The ZIP was installed and loaded in the WinUI development profile, preserving existing installation receipts. No credentials were copied from the legacy profile.

The shared portable SDK/host suite passed 259 tests on the Live Transcript host branch. Automated fixture tests do not replace authenticated provider, native model/device, microphone or visual UI acceptance. Public catalog publication and production-profile migration are pending.
