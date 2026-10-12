# Whisper (Local) portable plugin

Local whisper.cpp transcription with Whisper.net CPU/CUDA/Vulkan native runtimes and model management.

Version `1.2.24`; plugin ID `com.typewhisper.whisper-cpp`; minimum host `1.1.6`.

## Setup

Download a model explicitly, select the processing device and save the settings, then load the model. The shared Save button persists the processing-device choice. Model download, use and removal remain explicit actions. GPU capability and driver requirements must be checked on the target machine.

Native translation outputs English. The provider rejects translation with English-only or Large V3 Turbo weights, including quantized Turbo, before loading the model. Use a multilingual non-Turbo model such as Small or Medium for translation.

The provider passes active dictionary terms to Whisper as its initial prompt, for microphone dictation and file transcription. Terms arrive with exact boundaries and stay whole and in dictionary order: the prompt is limited to 224 UTF-8 bytes. Whisper reads at most 224 prompt tokens and each token covers at least one byte, so even identifiers or non-Latin terms never push the first terms out of the window. Without speech, Whisper tends to repeat its prompt; when every word of a transcript resembles a dictionary term, the plugin decodes the audio once more without the prompt and keeps the prompted text only if that pass still finds words. Hosts older than this change still send no terms to local PCM engines; transcription is then unchanged.

Before a model download starts, the plugin removes abandoned partial files of the same model and checks that its drive has room for the model plus a 256 MB reserve. If it does not, the download stops immediately and names the required and available space.

## Platform scope

The macOS counterpart uses WhisperKit/Apple runtime integrations. This package retains whisper.cpp for Windows. The tested package targets Windows x64; ARM64 redistribution is not validated and requires ARM64 VC runtime assets on the build machine.

## Build and verification

From the repository root:

```powershell
dotnet msbuild plugins/TypeWhisper.Plugin.WhisperCpp/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins/TypeWhisper.Plugin.WhisperCpp/Tests -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.whisper-cpp` inside the plugin project. Package that directory as the ZIP root.

Create distributable packages on Windows, where the required Visual C++ runtime DLLs are staged. Non-Windows builds skip that Windows-only step for headless tests; their staging directories are not complete distribution packages, and `CopyPackage` rejects non-Windows hosts.

Tests cover model/runtime contracts, download integrity, native package contents, package lifecycle, processing-device persistence, language choices and PCM validation. The saved model loads on the first WAV or PCM decode after activation, including after restart. Local live preview repeatedly decodes recording buffers; this is not a native streaming model.

CUDA downloads are stored in the persistent plugin asset directory. A version-specific native runtime cache combines those downloads with packaged Whisper libraries, keeping installed packages immutable and reusing cuBLAS across upgrades.

Review regressions also cover readiness with missing model files, English-only decoder language, cancelled model loads, multilingual segment spacing, timestamp export and no-speech aggregation. CUDA installer tests verify Windows behavior and reject unsupported hosts without attempting downloads.

CPU, custom ROCm and ARM64 inference need separate native acceptance.

Vulkan selects the first dedicated GPU rather than assuming device 0 is dedicated. The plugin reads the GPU list before model loading. Processing device reports the active GPU or why it fell back to CPU; the load log records the GPU name.

The CUDA installer verifies the persisted CUDA package identity and freshly computed DLL hashes on every check, stages complete files before atomic replacement and publishes its installation receipt last. Regression tests cover corruption, interrupted installation and package changes.

Model removal persists deselection before deleting weights. Download progress uses the known model size when the response stream cannot seek; abandoned GUID temporary files are cleaned without touching active or unrelated downloads. Staged native-cache DLLs are compared with their sources and repaired when damaged.

CUDA extraction honors cancellation before publication. Abandoned runtime downloads and unused older native-cache versions are removed while active files remain intact. Explicit CUDA loads reuse their integrity result within that load, and staged packages exclude non-Windows native runtimes.

Settings use a lightweight file-presence hint; model loading performs cancellable CUDA content verification on a worker thread. Downloaded and existing models must match the exact artifact sizes recorded from [whisper.net v3 revision a28c1f3](https://huggingface.co/sandrohanea/whisper.net/tree/a28c1f379c0359332f8d101184249cba28b1ff53), the model family used by Whisper.net 1.9.0. Invalid existing files remain unavailable and can be replaced through download. Exact length is not a cryptographic content guarantee. Compatible acceleration-preference changes preserve the loaded backend status.

Native model construction and CUDA staging run off the calling UI thread. Cancellation is checked after preparation before replacing a loaded factory. After first-time CUDA installation, a capability refresh exposes localized restart-and-reselect guidance in the processing-device setting.

Oversized model responses are rejected before writing beyond the trusted artifact size. Model replacement intentionally keeps at most one native context to bound RAM/VRAM usage; a cancelled or failed replacement retains the selected model, which is restored on the next decode.

After first-time CUDA installation, both explicit CUDA and Automatic selection honor the restart requirement. Selecting CPU remains available.

The CUDA installation restart gate is scoped to the runtime asset directory and the current app process. Disabling, enabling or reloading the plugin does not clear it; an actual process restart does.

The process restart gate is published immediately after successful CUDA installation, before cancellable post-install verification, so cancellation cannot lose the restart requirement.

Reusing a verified existing CUDA installation does not publish a new process restart gate; the installer reports whether it actually replaced files.
