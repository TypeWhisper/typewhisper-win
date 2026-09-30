# Local LLM (llama.cpp) portable plugin

Local GGUF text processing through LLamaSharp and the bundled llama.cpp CPU runtime. It is the Windows counterpart of the macOS Local LLM (MLX) plugin and offers the same model families: Gemma 4, Qwen3.5 and LFM2.5. Host-rendered model cards show download progress, cancellation, download completion and loaded state. Local LLM is a workflow text provider, not a speech recognition engine.

Version `1.0.0`; plugin ID `com.typewhisper.local-llm-llamacpp`; minimum host `1.1.3`.
Host 1.1.3 adds `ILocalLlmModelManagement`; older hosts reject this package before loading its types.

## Relationship to Gemma 3 (Local)

This plugin started as a copy of `TypeWhisper.Plugin.GemmaLocal` (`com.typewhisper.gemma-local` 1.2.4) and has its own plugin ID, so both can be installed side by side. Models, the model selection and the CPU-thread setting are not migrated from Gemma 3 (Local). Workflows that select Gemma 3 (Local) keep using it until the user picks Local LLM instead.

## Models

| Model | File | Size | Notes |
| --- | --- | --- | --- |
| Gemma 4 E2B (Q4_K_M) | `unsloth/gemma-4-E2B-it-GGUF` | ~3.1 GB | Recommended default |
| Gemma 4 E4B (Q4_K_M) | `unsloth/gemma-4-E4B-it-GGUF` | ~5 GB | |
| Gemma 4 E4B (Q8_0) | `unsloth/gemma-4-E4B-it-GGUF` | ~8.2 GB | |
| Gemma 4 26B-A4B (Q4_K_M, MoE) | `unsloth/gemma-4-26B-A4B-it-GGUF` | ~17 GB | Needs 32 GB RAM or more |
| Qwen3.5 2B (Q4_K_M) | `unsloth/Qwen3.5-2B-GGUF` | ~1.3 GB | Smallest download |
| LFM2.5 2.6B (Q4_K_M) | `LiquidAI/LFM2.5-2.6B-GGUF` | ~1.7 GB | Always reasons first, so answers take several seconds |

The model URLs are pinned to immutable Hugging Face revisions and checked against their expected length and SHA-256 before loading. Interrupted downloads resume through validated HTTP byte ranges, as in Gemma 3 (Local).

Gemma 4 and Qwen3.5 are Apache 2.0 models. LFM2.5 uses the LFM Open License v1.0, which allows free commercial use only below USD 10 million annual revenue.

## Prompt format

Each family uses the generation prompt from its published chat template:

- Gemma 4: `<|turn>system`, `<|turn>user` and `<|turn>model` turns closed by `<turn|>`, with thinking disabled.
- Qwen3.5: ChatML with the empty `<think>` block that the template emits when thinking is disabled.
- LFM2.5: ChatML with the opening `<think>` that its template always emits. The reasoning is removed from the answer, and 2,048 extra output tokens are reserved for it.

Only the template pieces are tokenized with special-token parsing. Instructions and dictated text are tokenized as plain text, so typed markers such as `<|im_end|>` or `<turn|>` cannot open or close turns. Generation stops on the family's turn-end token IDs, not on matching text. The context window is 8,192 tokens, and the sampling temperature is 0.1, matching the macOS plugin.

## Setup

Download a model from its card, then choose Load model before selecting it in a workflow. No model is downloaded or loaded automatically on activation. Unload releases memory; Remove unloads the chosen model before deleting its GGUF file. Discard incomplete downloads removes only known partial files. The bundled backend is CPU-only; the CPU-thread preference takes effect at the next model load.

## Build and verification

From the repository root:

```powershell
dotnet msbuild plugins/TypeWhisper.Plugin.LocalLlm/portable.proj -t:Build -p:Configuration=Release
dotnet test plugins/TypeWhisper.Plugin.LocalLlm/Tests -c Release
```

Run restore and build as separate steps on a clean checkout, as CI does, so the llama.cpp native runtimes from `LLamaSharp.Backend.Cpu` are copied into the package. The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.local-llm-llamacpp` inside the plugin project. Package that directory as the ZIP root.

The live model check is skipped unless `TYPEWHISPER_LOCAL_LLM_MODELS` names a folder containing downloaded catalog GGUF files. For each file it finds, the check verifies the hash, loads the model, runs a German correction and a German-to-English translation, and confirms that typed turn markers do not become stop tokens:

```powershell
$env:TYPEWHISPER_LOCAL_LLM_MODELS = "$env:TEMP\tw-localllm-models"
dotnet test plugins/TypeWhisper.Plugin.LocalLlm/Tests -c Release --filter LiveModelTests
```

On Windows x64 with the CPU backend, the live check passed for Gemma 4 E2B, Qwen3.5 2B and LFM2.5 2.6B. Loading took 2 to 6 seconds. Gemma 4 E2B and Qwen3.5 2B answered the correction and the translation in 0.6 to 1.5 seconds. LFM2.5 took 6 to 22 seconds because it reasons first, and its correction was less reliable. The E4B and 26B-A4B models have not been run yet.
