# IBM Granite Speech (Local) portable plugin

Local Granite Speech transcription with the Windows Python/PyTorch sidecar, model management and packaged scripts.

Version `1.2.7`; plugin ID `com.typewhisper.granite-speech`; minimum host `1.1.2`.

## Setup

Download the model and managed runtime through the engine controls. The runtime requires Windows x64. Choose CPU, NVIDIA CUDA or automatic device selection and save settings together. Transcription uses only local cached model files, including the pinned chat template; the selected model loads automatically after restart. Audio and explicit language are passed through both final transcription and local PCM previews.

The managed runtime pins patched PyTorch 2.13.0 and TorchAudio 2.11.0. Older runtime markers require setup again; cached model files are retained. Switching a CPU-only runtime to CUDA also requires setup again. The explicit Remove model and runtime action can remove this provider's only model even while selected, preserving settings. CUDA devices without bfloat16 support use float32. Generation budgets scale with audio duration and detect output-limit termination instead of silently accepting truncated text.

Python is launched through Windows extended-length paths so pip can install deeply nested wheel contents inside the profile directory without changing Windows settings or relying on short-name aliases.

## Platform scope

The Windows PyTorch/Python backend is retained. macOS uses MLX; Windows provides local PCM preview through the portable host.

## Build and verification

From the repository root:

```powershell
dotnet msbuild plugins/TypeWhisper.Plugin.GraniteSpeech/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins/TypeWhisper.Plugin.GraniteSpeech/Tests -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.granite-speech` inside the plugin project. Package that directory as the ZIP root.

Tests cover saved device selection, invalid/canceled PCM, managed asset removal that preserves settings, packaged scripts, pinned chat-template downloads, and package install/restart/uninstall/reinstall.

Bootstrap downloads require pinned SHA-256 hashes before extraction or execution, and the pip bootstrap script is pinned to a PyPA commit. Readiness checks use the same pinned model revision as setup. Regression coverage includes CPU-to-CUDA readiness, wheel-flavor selection, removal of the selected model, dtype fallback, output-limit handling, translation language hints and termination of canceled native processes.

Recognition quality, including German dictation, needs separate evaluation. Working live preview and final transcription do not establish accuracy. Recheck microphone behavior after native runtime upgrades.

Settings screenshots: [model and device settings](../../docs/screenshots/granite/settings-dark.png), [enabled live preview](../../docs/screenshots/granite/live-enabled.png).

Automated fixture tests do not replace native model, device or UI acceptance.
