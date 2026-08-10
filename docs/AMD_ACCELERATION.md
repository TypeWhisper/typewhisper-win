# AMD acceleration on Windows

TypeWhisper exposes acceleration per transcription engine. Selecting an acceleration preference does not mean that every engine or model can use that backend. The status below the selector reports the backend that is active for the currently selected engine.

## Compatibility

| Engine | CPU | NVIDIA CUDA | AMD Vulkan | AMD ROCm |
|--------|-----|-------------|------------|----------|
| whisper.cpp | Supported | Supported on Windows x64 | Recommended AMD path on Windows x64 | Manual advanced hook |
| sherpa-onnx, including Parakeet and Canary | Supported | Supported on Windows x64 | Not supported | Not supported |
| Cohere Transcribe (Local) | Supported | Supported when its verified runtime is available | Supported when its verified runtime is available | Not supported |

Other local engines can expose their own subset. The acceleration selector is filtered to the capabilities advertised by the selected engine.

## Reading the status

- `Using CPU`, `Using CUDA`, `Using Vulkan`, or `Using ROCm` identifies the active backend reported after the model loads.
- A message such as `Vulkan will be used when the model loads` means the preference changed but the model has not loaded with that runtime yet.
- `Vulkan unavailable` means whisper.cpp fell back to CPU or the native Vulkan runtime failed to load. Update the AMD graphics driver and reload the model.
- `Restart required` means a native runtime is already loaded in the process and TypeWhisper must restart before switching it safely.

For an AMD GPU, start with whisper.cpp and AMD Vulkan. Select a whisper.cpp model, choose AMD Vulkan, then load or reload the model. Confirm that the final status is `Using Vulkan` before evaluating performance.

## Manual ROCm hook

TypeWhisper does not ship or discover a supported ROCm build of whisper.cpp. The ROCm/TheRock SDK by itself is not a loadable TypeWhisper runtime.

Advanced users can set `TYPEWHISPER_WHISPERCPP_ROCM_LIBRARY_PATH` to either:

- the full path of a custom ROCm-compatible `whisper.dll`, or
- a directory that contains that `whisper.dll`.

Restart TypeWhisper after changing the environment variable. The custom DLL and all of its native dependencies remain the user's responsibility.

## ZLUDA

ZLUDA is not an officially supported TypeWhisper backend. If a ZLUDA setup makes the whisper.cpp CUDA runtime load, Whisper.net reports the backend only as CUDA. TypeWhisper cannot reliably distinguish native NVIDIA CUDA from CUDA translated through ZLUDA, so `Using CUDA` is not proof that native NVIDIA CUDA is active.

ZLUDA does not add AMD acceleration to sherpa-onnx. Parakeet and Canary continue to use the CPU unless their supported NVIDIA CUDA runtime is available.

## Diagnostics

Use **Settings > About > Export Diagnostics** after reproducing a load failure. The `transcription_acceleration` object includes:

- selected engine ID and name,
- selected acceleration preference,
- active backend,
- loaded or attempted native runtime path when known, and
- the most recent native error message when available.

The whisper.cpp plugin also treats Windows native `SEHException` failures, including the common `External component has thrown an exception` message, as native runtime load failures so the compact status and exported diagnostics stay available.
