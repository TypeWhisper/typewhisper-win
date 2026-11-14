# IBM Granite Speech (Local) portable plugin

Local Granite Speech transcription with the Windows Python/PyTorch sidecar, model management and packaged scripts.

Version `1.2.9`; plugin ID `com.typewhisper.granite-speech`; minimum host `1.1.2`.

## Setup

Download the model and managed runtime through the engine controls. The runtime requires Windows x64. Choose CPU, NVIDIA CUDA or automatic device selection and save settings together. Transcription uses only local cached model files, including the pinned chat template; the selected model loads automatically after restart. Audio and explicit language are passed through both final transcription and local PCM previews.

The managed runtime pins patched PyTorch 2.13.0 and TorchAudio 2.11.0. Older runtime markers require setup again; cached model files are retained. Switching a CPU-only runtime to CUDA also requires setup again. The explicit Remove model and runtime action can remove this provider's only model even while selected, preserving settings. CUDA devices without bfloat16 support use float32. Generation budgets scale with audio duration and detect output-limit termination instead of silently accepting truncated text.

Python packages install from a fully resolved lock file per device flavor (`Scripts/requirements-cpu.lock.txt`, `Scripts/requirements-cu130.lock.txt`) with `pip --require-hashes --no-deps --only-binary :all:`. pip accepts only wheels whose SHA-256 is listed, whichever of the PyTorch and PyPI indexes serves them, and resolves no further packages. `requirements.txt` keeps the direct pins; regenerate both lock files after changing it, with the `uv pip compile` command noted at the top of each lock file.

Python is launched through Windows extended-length paths so pip can install deeply nested wheel contents inside the profile directory without changing Windows settings or relying on short-name aliases.

Model files download straight into `managed-runtime\model` (`hf_hub_download(..., local_dir=...)`), with `HF_HOME` at `managed-runtime\hf`. Up to 1.2.8 the model went into the Hugging Face cache at `managed-runtime\hf-cache`. Its repository, blob hash and partial-file levels pushed paths past the Windows 260-character limit for user names of 23 characters or more (#782). Installs that still have that cache keep loading from it. The next setup moves its files into the model folder instead of downloading them again, checks them against the pinned revision and then deletes the old cache. Before downloading, setup checks that the model folder path leaves room for the longest model file name. If Windows long path support is off and the path is too long, setup stops with a message that names the folder. When setup fails, the reason appears under the processing device setting and in `setup.log` in the plugin data folder, because the host shows only a generic download error.

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
