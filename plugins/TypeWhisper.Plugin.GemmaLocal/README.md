# Gemma 3 (Local) portable plugin

Local Gemma 3 GGUF text processing through LLamaSharp. Host-rendered model cards show download progress, cancellation, download completion and loaded state. CPU settings use one Save settings button. Gemma is a workflow text provider, not a speech recognition engine.

Version `1.2.5`; plugin ID `com.typewhisper.gemma-local`; minimum host `1.1.3`.
Host 1.1.3 adds `ILocalLlmModelManagement`; older hosts reject this package before loading its types.

## Setup

Download a model from its card, then choose Load model before selecting it in a workflow. No model is downloaded or loaded automatically on activation. TypeWhisper remembers the model you loaded: after it was released for inactivity (Settings > Advanced > Recording > Unload idle models) or after a restart, the next request loads it again. Unload releases memory and stops this automatic loading; Remove unloads the chosen model before deleting its GGUF file.

Switching directly to another model keeps the current model available until the replacement loads successfully, temporarily requiring memory for both. On memory-constrained machines, choose Unload on the current model first, then load the replacement. This explicit path releases memory before loading; automatic replacement preserves the working model on failure or cancellation.

The model URLs are pinned to immutable Hugging Face revisions. Interrupted or canceled downloads keep a `.download` file. Click Download again to continue with a validated HTTP byte range. If the server ignores ranges, the download restarts safely from zero. A complete saved file is checked locally before any network request. The expected length and SHA-256 must match before publication; an invalid full payload is discarded for a fresh retry. Discard incomplete downloads removes only known partial files, preserving completed models and unrelated files.

Gemma 3 instructions are included in the initial user turn, matching Google's [prompt format](https://ai.google.dev/gemma/docs/core/prompt-structure). The bundled backend is CPU-only; the shared CPU-thread preference takes effect at the next model load.

## Platform scope

The model catalog contains Gemma 3 GGUF files. macOS Gemma 4 MLX uses a different backend and is not included. No server backend is advertised.

## Build and verification

From the repository root:

```powershell
dotnet msbuild plugins/TypeWhisper.Plugin.GemmaLocal/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins/TypeWhisper.Plugin.GemmaLocal/Tests -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.gemma-local` inside the plugin project. Package that directory as the ZIP root.

Coverage includes settings persistence, model identity, prompt formatting, integrity checks, cancellation before setup, incomplete-model detection, selective removal, immutable package lifecycle, fragmented stop markers, rejection of truncated output, and range-based resume after network failures or cancellation. Resume-specific tests also cover ignored ranges, invalid response headers, complete saved files, hash failures, oversized responses, and partial-file cleanup.

Automated fixture tests do not replace native model, device or UI acceptance.

The 12B/27B models, non-Windows native execution and end-to-end workflows need separate acceptance.
