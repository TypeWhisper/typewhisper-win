# Supertonic TTS portable plugin

Local Supertonic 3 speech synthesis with ONNX assets, portable license/speed/denoising settings and host voice/output-device selection.

Version `1.2.0`; plugin ID `com.typewhisper.supertonic-tts`; minimum host `1.1.2`.
Independent branch: `seofood/supertonictts-portable`, based on `4db8f6ac`.

## Setup

Review and explicitly accept the displayed model license, download assets, then select the voice and playback output in the host. Optional Hugging Face credentials are supported by the model-requirement contract but have no dedicated portable TTS settings field yet.

This package uses host-rendered portable settings and an independent WinUI data directory. Legacy settings, credentials and model files are not imported automatically.

## Source and platform scope

Protocol/runtime sources and applicable fixtures were snapshotted from `plugins/TypeWhisper.Plugin.SupertonicTts` at `4db8f6ac`, then adapted under `plugins-v2`. The package does not reference the legacy provider DLL or compile WPF settings views. Legacy sources, catalogs and published packages remain unchanged.

The macOS repository was compared at `ac00e39ea63e4789de8427d034d2085b3d898159`; it was not modified. The Windows ONNX backend is retained. WASAPI playback replaces WPF-era playback and honors per-request voice and output device.

## Build and verification

From this branch's repository root:

```powershell
dotnet msbuild plugins-v2/TypeWhisper.Plugin.SupertonicTts/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins-v2/TypeWhisper.Plugin.SupertonicTts/Tests -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.supertonic-tts` inside the plugin project. Package that directory as the ZIP root.

12 plugin tests pass. Fake synthesis/assets, voice metadata, explicit portable license gate and package lifecycle. Native runtime files are included; no model download, audible playback or real synthesis was performed. All packages have isolated install, enable, restart, disable, uninstall and reinstall coverage through the real portable package loader and host services.

The ZIP was installed and loaded in the WinUI development profile, preserving existing installation receipts. No credentials were copied from the legacy profile.

The shared portable SDK/host suite passed 259 tests on the Live Transcript host branch. Automated fixture tests do not replace authenticated provider, native model/device, microphone or visual UI acceptance. Public catalog publication and production-profile migration are pending.
