# Whisper (Local) portable plugin

Local whisper.cpp transcription with Whisper.net CPU/CUDA/Vulkan native runtimes and model management.

Version `1.2.9`; plugin ID `com.typewhisper.whisper-cpp`; minimum host `1.1.2`.
Independent branch: `seofood/whispercpp-portable`, based on `4db8f6ac`.

## Setup

Download a model explicitly, select the processing device and save the settings, then load the model. The shared Save button persists the processing-device choice. Model download, use and removal remain explicit actions. GPU capability and driver requirements must be checked on the target machine.

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

Create distributable packages on Windows, where the required Visual C++ runtime DLLs are staged. Non-Windows builds skip that Windows-only step for headless tests; their staging directories are not complete distribution packages, and `CopyPackage` rejects non-Windows hosts.

68 plugin tests pass, covering model/runtime contracts, download integrity, native package contents, package lifecycle, processing-device persistence, language choices and PCM validation. Large V3 Turbo was downloaded and verified on an NVIDIA RTX 4060 Ti using CUDA. A synthetic English WAV and both partial and complete PCM buffers transcribed successfully. The saved model is loaded on the first WAV or PCM decode after activation, including after an app restart. Cold-start inference was verified without calling LoadModelAsync first. Local live preview uses repeated decoding of recording buffers; this is not a native streaming model. All packages have isolated install, enable, restart, disable, uninstall and reinstall coverage through the real portable package loader and host services.

CUDA downloads are stored in the persistent plugin asset directory. A version-specific native runtime cache combines those downloads with packaged Whisper libraries, keeping installed packages immutable and reusing cuBLAS across upgrades. Source 1.2.6 passed cold PCM-first and WAV CUDA inference from this cache, with loaded module paths verified.

Review regressions also cover readiness with missing model files, English-only decoder language, cancelled model loads, multilingual segment spacing, timestamp export and no-speech aggregation. CUDA installer tests verify Windows behavior and reject unsupported hosts without attempting downloads.

The ZIP was installed and loaded in the WinUI development profile, preserving existing installation receipts. No credentials were copied from the legacy profile.

Marco confirmed German microphone dictation and local live preview on Large V3 Turbo after the restart-loading fix. The installed package and UI screenshots are version 1.2.4, with the Whisper (Local) name and chip icon. CPU, Vulkan, custom ROCm and ARM64 inference remain unverified. Public catalog publication and production-profile migration are pending.

Source 1.2.9 verifies the persisted CUDA package identity and freshly computed DLL hashes on every check, stages complete files before atomic replacement and publishes its installation receipt last. Regression tests cover corruption, interrupted installation and package changes.

Model removal persists deselection before deleting weights. Download progress uses the known model size when the response stream cannot seek; abandoned GUID temporary files are cleaned without touching active or unrelated downloads. Staged native-cache DLLs are compared with their sources and repaired when damaged.
