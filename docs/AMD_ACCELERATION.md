# AMD acceleration on Windows

Scope: provider/backend investigation. The matrix records the documented runtime
paths; it is not certification for every GPU or package architecture. Check the
selected portable plugin's settings and active runtime for the installed version.

Acceleration is a setting of each local transcription plugin, not a global choice. For whisper.cpp it is **Processing device** in the plugin's settings (Automatic, CPU, NVIDIA CUDA, Vulkan, AMD ROCm). A choice only offers the backends that plugin supports.

## Compatibility

| Engine | CPU | NVIDIA CUDA | AMD Vulkan | AMD ROCm |
|--------|-----|-------------|------------|----------|
| whisper.cpp | Supported | Supported on Windows x64 | Recommended AMD path on Windows x64 | Manual advanced hook |
| NVIDIA Parakeet (Parakeet, Parakeet Ultra) | Supported | Up to plugin 1.2; replaced by Vulkan | Supported on Windows x64 from plugin 1.3, also on NVIDIA and Intel | Not supported |
| NVIDIA Parakeet (Canary) | Supported | Up to plugin 1.2 | Not supported | Not supported |
| Cohere Transcribe (Local) | Supported | Supported when its verified runtime is available | Supported when its verified runtime is available | Not supported |

Other local engines can expose their own subset.

## Reading the status

After a whisper.cpp model has loaded (plugin 1.2.20 or later), the Processing device description ends with what it actually runs on (German UI: `In Verwendung:`):

- `In use: Vulkan · <GPU name>` names the GPU. `(integrated graphics)` marks the processor's built-in graphics.
- `In use: NVIDIA CUDA`, `In use: AMD ROCm` or `In use: CPU` name the other backends.
- `In use: CPU (Vulkan unavailable)` or `In use: CPU (CUDA unavailable)` means the chosen GPU runtime could not load, or the Vulkan runtime found no GPU, and whisper.cpp fell back to CPU. Update the graphics driver and restart TypeWhisper. The next dictation loads the model again.

Nothing is shown before the first dictation or file transcription loads the model. If the description instead says to restart TypeWhisper, a different native runtime is already loaded in the process. Restart before switching.

For an AMD GPU, start with whisper.cpp and Vulkan. Select a whisper.cpp model, choose Vulkan, dictate once, then reopen the plugin settings and check that `In use:` names your AMD GPU before evaluating performance.

## NVIDIA Parakeet on the graphics card

From plugin 1.3, the NVIDIA Parakeet plugin's **Processing device** offers Automatic, CPU and Graphics card. The graphics card runs Parakeet and Parakeet Ultra through transcribe.cpp on any graphics card with Vulkan, from NVIDIA, AMD or Intel. The first switch downloads the transcribe.cpp runtime (17 MB) and the model as a GGUF file (about 740 MB); the CPU keeps using its own ONNX files. Automatic uses the graphics card once these files are present and the CPU otherwise. Canary always runs on the CPU. The graphics card is chosen as for whisper.cpp below, and In use: names it.

The first load after a graphics driver update can take about half a minute while the driver compiles its shaders; later loads take about a second. A NVIDIA CUDA choice saved by plugin 1.2 reads as Automatic, and the plugin deletes the CUDA runtime that version downloaded.

## Which GPU Vulkan uses

whisper.cpp places the model on one Vulkan device. From plugin 1.2.20, TypeWhisper picks the first dedicated GPU and uses integrated graphics only when there is no dedicated GPU. On systems with both, for example a Ryzen processor with Radeon graphics next to a dedicated card, the dedicated card is used even when Windows lists the integrated graphics first. On a system with an NVIDIA card and AMD integrated graphics, Vulkan runs on the NVIDIA card. Choose NVIDIA CUDA there for the best speed.

## Overlay and capture layers

Overlay and capture tools such as Steam, OBS or graphics driver utilities install implicit Vulkan layers, which the Vulkan loader adds to every Vulkan process. They can crash or slow Vulkan transcription. TypeWhisper therefore starts with `VK_LOADER_LAYERS_DISABLE=~implicit~`, which also applies to its transcription worker and to Cohere Transcribe (Local). It does not change the overlay in games or other apps. The variable needs Vulkan loader 1.3.234 or later, which current graphics drivers install as `vulkan-1.dll` in `System32`. Older loaders ignore it and keep loading the layers; updating the graphics driver updates the loader.

If you set `VK_LOADER_LAYERS_DISABLE` yourself, TypeWhisper keeps your value. To keep the implicit layers, for example to capture TypeWhisper's GPU work with a profiling tool, set `TYPEWHISPER_KEEP_VULKAN_IMPLICIT_LAYERS=1` and restart TypeWhisper.

## Manual ROCm hook

TypeWhisper does not ship or discover a supported ROCm build of whisper.cpp. The ROCm/TheRock SDK by itself is not a loadable TypeWhisper runtime.

Advanced users can set `TYPEWHISPER_WHISPERCPP_ROCM_LIBRARY_PATH` to either:

- the full path of a custom ROCm-compatible `whisper.dll`, or
- a directory that contains that `whisper.dll`.

Restart TypeWhisper after changing the environment variable. The custom DLL and all of its native dependencies remain the user's responsibility.

## ZLUDA

ZLUDA is not an officially supported TypeWhisper backend. If a ZLUDA setup makes the whisper.cpp CUDA runtime load, Whisper.net reports the backend only as CUDA. TypeWhisper cannot reliably distinguish native NVIDIA CUDA from CUDA translated through ZLUDA, so `In use: NVIDIA CUDA` is not proof that native NVIDIA CUDA is active.

ZLUDA is not needed for Parakeet: from plugin 1.3 it runs on AMD graphics through Vulkan.

## Diagnostics

Report the TypeWhisper and plugin versions, Windows architecture, model, chosen
processing device and the `In use:` text after loading. Include the native error if shown,
without credentials or private transcripts. The old WPF diagnostics-export path
and `transcription_acceleration` payload are not available in the current WinUI
source; do not follow older instructions for that menu.
