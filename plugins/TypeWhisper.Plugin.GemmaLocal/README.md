# Gemma 3 (Local) portable plugin

Local Gemma 3 GGUF text processing through LLamaSharp. Host-rendered model cards show download progress, cancellation, download completion and loaded state. CPU settings use one Save settings button. Gemma is a workflow text provider, not a speech recognition engine.

Version `1.2.4`; plugin ID `com.typewhisper.gemma-local`; minimum host `1.1.3`.
Host 1.1.3 adds `ILocalLlmModelManagement`; older hosts reject this package before loading its types.
Resume follow-up branch: `seofood/gemma-resumable-downloads`; original portable migration based on `4db8f6ac`.

## Setup

Download a model from its card, then choose Load model before selecting it in a workflow. No model is downloaded or loaded automatically on activation. Unload releases memory; Remove unloads the chosen model before deleting its GGUF file.

Switching directly to another model keeps the current model available until the replacement loads successfully, temporarily requiring memory for both. On memory-constrained machines, choose Unload on the current model first, then load the replacement. This explicit path releases memory before loading; automatic replacement preserves the working model on failure or cancellation.

The model URLs are pinned to immutable Hugging Face revisions. Interrupted or canceled downloads keep a `.download` file. Click Download again to continue with a validated HTTP byte range. If the server ignores ranges, the download restarts safely from zero. A complete saved file is checked locally before any network request. The expected length and SHA-256 must match before publication; an invalid full payload is discarded for a fresh retry. Discard incomplete downloads removes only known partial files, preserving completed models and unrelated files.

Gemma 3 instructions are included in the initial user turn, matching Google's [prompt format](https://ai.google.dev/gemma/docs/core/prompt-structure). The bundled backend is CPU-only; the shared CPU-thread preference takes effect at the next model load.

This package uses host-rendered portable settings and an independent WinUI data directory. Legacy settings, credentials and model files are not imported automatically.

## Source and platform scope

Protocol/runtime sources and applicable fixtures were snapshotted from `plugins/TypeWhisper.Plugin.GemmaLocal` at `4db8f6ac`, then adapted under `plugins`. The package does not reference the legacy provider DLL or compile WPF settings views. Legacy sources, catalogs and published packages remain unchanged.

The macOS repository was compared at `ac00e39ea63e4789de8427d034d2085b3d898159`; it was not modified. The legacy Windows files were Gemma 3 despite Gemma 4 labels; the new model IDs/names reflect the actual files. macOS Gemma 4 MLX is a different backend and is not included. No server backend is advertised.

## Build and verification

From this branch's repository root:

```powershell
dotnet msbuild plugins/TypeWhisper.Plugin.GemmaLocal/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins/TypeWhisper.Plugin.GemmaLocal/Tests -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.gemma-local` inside the plugin project. Package that directory as the ZIP root.

50 plugin tests pass. Coverage includes settings persistence, model identity, prompt formatting, integrity checks, cancellation before setup, incomplete-model detection, selective removal, immutable package lifecycle, fragmented stop markers, rejection of truncated output, and range-based resume after network failures or cancellation. Resume-specific tests also cover ignored ranges, invalid response headers, complete saved files, hash failures, oversized responses, and partial-file cleanup. The merged base passed 271 portable SDK/host tests.

A live resume check against the pinned Hugging Face URL used an isolated copy of the existing 4B model with its final 65,536 bytes removed. The downloader requested `bytes=2489828480-`, received HTTP 206, fetched only the missing tail, and verified the complete 2,489,894,016-byte model against its expected SHA-256 before publication. The original cached model was preserved.

On Windows x64, the actual 4B Q4_K_M download (2,489,894,016 bytes) passed SHA-256 verification. Native CPU loading took approximately 4.8 seconds. German spelling/capitalization correction and German-to-English translation returned the expected text in approximately 1.5 and 1.2 seconds respectively. Unloading released provider availability. In-flight cancellation rejected partial output, and a subsequent request returned the expected translation. These are two short acceptance examples, not a quality benchmark. The 12B/27B models and non-Windows native execution remain untested.

The ZIP was installed and loaded in the WinUI development profile, preserving existing installation receipts. No credentials were copied from the legacy profile.

Automated fixture tests do not replace authenticated provider, native model/device, microphone or visual UI acceptance. Public catalog publication and production-profile migration are pending.

On the WinUI-only acceptance host, model cards rendered correctly and the downloaded 4B model loaded through the settings button. A settings screenshot is included under docs/screenshots/gemma. End-to-end workflow acceptance remains separate from this model-loading check.

The user confirmed the resume flow in the WinUI development app: canceling a model download and starting it again continued from the previously downloaded position.
