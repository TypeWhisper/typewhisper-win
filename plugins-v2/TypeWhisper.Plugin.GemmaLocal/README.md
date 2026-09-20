# Gemma 3 (Local) portable plugin

Local Gemma 3 GGUF text processing through LLamaSharp. Host-rendered model cards show download progress, cancellation, download completion and loaded state. CPU settings use one Save settings button. Gemma is a workflow text provider, not a speech recognition engine.

Version `1.2.3`; plugin ID `com.typewhisper.gemma-local`; minimum host `1.1.3`.
Host 1.1.3 adds `ILocalLlmModelManagement`; older hosts reject this package before loading its types.
Independent branch: `seofood/gemmalocal-portable`, based on `4db8f6ac`.

## Setup

Download a model from its card, then choose Load model before selecting it in a workflow. No model is downloaded or loaded automatically on activation. Unload releases memory; Remove unloads the chosen model before deleting its GGUF file.

The model URLs are pinned to immutable Hugging Face revisions. Downloads are checked against the expected length and SHA-256 before publication, and partial files are removed on cancellation. Gemma 3 instructions are included in the initial user turn, matching Google's [prompt format](https://ai.google.dev/gemma/docs/core/prompt-structure). The bundled backend is CPU-only; the shared CPU-thread preference takes effect at the next model load.

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

15 plugin tests and 266 portable SDK/host tests pass. Coverage includes settings persistence, model identity, prompt formatting, integrity checks, cancellation before setup, incomplete-model detection, selective removal and immutable package lifecycle.

On Windows x64, the actual 4B Q4_K_M download (2,489,894,016 bytes) passed SHA-256 verification. Native CPU loading took approximately 4.8 seconds. German spelling/capitalization correction and German-to-English translation returned the expected text in approximately 1.5 and 1.2 seconds respectively. Unloading released provider availability. In-flight cancellation rejected partial output, and a subsequent request returned the expected translation. These are two short acceptance examples, not a quality benchmark. The 12B/27B models and non-Windows native execution remain untested.

The ZIP was installed and loaded in the WinUI development profile, preserving existing installation receipts. No credentials were copied from the legacy profile.

The shared portable SDK/host suite passed 266 tests on this branch. Automated fixture tests do not replace authenticated provider, native model/device, microphone or visual UI acceptance. Public catalog publication and production-profile migration are pending.

On the WinUI-only acceptance host, model cards rendered correctly and the downloaded 4B model loaded through the settings button. A settings screenshot is included under docs/screenshots/gemma. End-to-end workflow acceptance remains separate from this model-loading check.
