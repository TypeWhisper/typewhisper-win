# IBM Granite Speech (Local) portable plugin

Local Granite Speech transcription with the Windows Python/PyTorch sidecar, model management and packaged scripts.

Version `1.2.1`; plugin ID `com.typewhisper.granite-speech`; minimum host `1.1.2`.
Independent branch: `seofood/granitespeech-portable`, based on `4db8f6ac`.

## Setup

Download the model and managed runtime through the engine controls. The runtime requires Windows x64. Choose CPU, NVIDIA CUDA or automatic device selection and save settings together. Transcription uses only local cached model files, including the pinned chat template; the selected model loads automatically after restart. Audio and explicit language are passed through both final transcription and local PCM previews.

This package uses host-rendered portable settings and an independent WinUI data directory. Legacy settings, credentials and model files are not imported automatically.

## Source and platform scope

Protocol/runtime sources and applicable fixtures were snapshotted from `plugins/TypeWhisper.Plugin.GraniteSpeech` at `4db8f6ac`, then adapted under `plugins-v2`. The package does not reference the legacy provider DLL or compile WPF settings views. Legacy sources, catalogs and published packages remain unchanged.

The macOS repository was compared at `ac00e39ea63e4789de8427d034d2085b3d898159`; it was not modified. The Windows PyTorch/Python backend is retained. macOS uses MLX; Windows provides local PCM preview through the portable host.

## Build and verification

From this branch's repository root:

```powershell
dotnet msbuild plugins-v2/TypeWhisper.Plugin.GraniteSpeech/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins-v2/TypeWhisper.Plugin.GraniteSpeech/Tests -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.granite-speech` inside the plugin project. Package that directory as the ZIP root.

8 .NET plugin tests and one Python setup test pass. Coverage includes saved device selection, invalid/canceled PCM, managed asset removal that preserves settings, packaged scripts, and pinned chat-template downloads with byte-based progress. Native acceptance installed matching PyTorch/torchaudio 2.11.0 CUDA packages, downloaded the pinned IBM model, and correctly transcribed synthetic English audio. Cold load plus transcription took 8.81 seconds; warm two- and five-second PCM previews took 0.44 and 1.08 seconds on the development machine. Manual microphone and visual UI acceptance remain pending. All packages have isolated install, enable, restart, disable, uninstall and reinstall coverage through the real portable package loader and host services.

The ZIP was installed and loaded in the WinUI development profile, preserving existing installation receipts. No credentials were copied from the legacy profile.

The shared portable SDK/host suite passed 259 tests on the Live Transcript host branch. Automated fixture tests do not replace authenticated provider, native model/device, microphone or visual UI acceptance. Public catalog publication and production-profile migration are pending.
