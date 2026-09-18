# Gemma 3 (Local) portable plugin

Local Gemma 3 GGUF completion through LLamaSharp with portable model selection and download/load/unload/remove actions.

Version `1.2.0`; plugin ID `com.typewhisper.gemma-local`; minimum host `1.1.2`.
Independent branch: `seofood/gemmalocal-portable`, based on `4db8f6ac`.

## Setup

Choose a model, download it explicitly, then load it before selecting it in a workflow. No model is downloaded or loaded automatically on activation.

This package uses host-rendered portable settings and an independent WinUI data directory. Legacy settings, credentials and model files are not imported automatically.

## Source and platform scope

Protocol/runtime sources and applicable fixtures were snapshotted from `plugins/TypeWhisper.Plugin.GemmaLocal` at `4db8f6ac`, then adapted under `plugins-v2`. The package does not reference the legacy provider DLL or compile WPF settings views. Legacy sources, catalogs and published packages remain unchanged.

The macOS repository was compared at `ac00e39ea63e4789de8427d034d2085b3d898159`; it was not modified. The legacy Windows files were Gemma 3 despite Gemma 4 labels; the new model IDs/names reflect the actual files. macOS Gemma 4 MLX is a different backend and is not included. No server backend is advertised.

## Build and verification

From this branch's repository root:

```powershell
dotnet msbuild plugins-v2/TypeWhisper.Plugin.GemmaLocal/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins-v2/TypeWhisper.Plugin.GemmaLocal/Tests -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.gemma-local` inside the plugin project. Package that directory as the ZIP root.

6 plugin tests pass. Model identity, portable settings, requested-model validation and package lifecycle. Native libraries are packaged; model download, native inference and hardware performance remain untested. All packages have isolated install, enable, restart, disable, uninstall and reinstall coverage through the real portable package loader and host services.

The ZIP was installed and loaded in the WinUI development profile, preserving existing installation receipts. No credentials were copied from the legacy profile.

The shared portable SDK/host suite passed 259 tests on the Live Transcript host branch. Automated fixture tests do not replace authenticated provider, native model/device, microphone or visual UI acceptance. Public catalog publication and production-profile migration are pending.
