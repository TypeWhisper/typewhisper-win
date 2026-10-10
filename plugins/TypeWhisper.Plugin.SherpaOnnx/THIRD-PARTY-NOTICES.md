# Third-party notices

## CPU

- sherpa-onnx (NuGet `org.k2fsa.sherpa.onnx`, bundled): Xiaomi Corporation and the k2-fsa authors, Apache-2.0. https://github.com/k2-fsa/sherpa-onnx
- ONNX Runtime (bundled with sherpa-onnx as `sherpaort.dll`): Microsoft Corporation, MIT. https://github.com/microsoft/onnxruntime
- The ONNX models are downloaded on demand, pinned to a revision and a SHA-256 each:
  - Parakeet TDT 0.6B v3: NVIDIA Corporation, CC-BY-4.0, in csukuangfj's sherpa-onnx int8 export.
  - Parakeet Ultra 0.6B: Moondream's retrain of nvidia/parakeet-tdt-0.6b-v3 (https://huggingface.co/moondream/parakeet-ultra), CC-BY-4.0, exported by TypeWhisper with `eng/export_parakeet_sherpa.py`.
  - Canary 180M Flash: NVIDIA Corporation, CC-BY-4.0, in csukuangfj's sherpa-onnx int8 export.

## GPU (Vulkan)

Neither the native runtime nor the GPU models are part of this package. When the GPU is chosen as the processing
device, the plugin downloads them and verifies each against a pinned SHA-256 before it is installed.

- transcribe.cpp 0.3.1: the transcribe.cpp authors, MIT. https://github.com/handy-computer/transcribe.cpp
  The plugin downloads the `transcribe-native-0.3.1-windows-x86_64-cpu-vulkan` release archive and keeps its DLLs,
  `contract.json` and the license texts in its `licenses` folder.
- ggml (vendored in transcribe.cpp): the ggml authors, MIT. https://github.com/ggml-org/ggml
- miniz (vendored in transcribe.cpp): Rich Geldreich, Tenacious Software LLC, RAD Game Tools and Valve Software, MIT. https://github.com/richgel999/miniz
- Parakeet TDT 0.6B v3 GGUF: NVIDIA's parakeet-tdt-0.6b-v3, CC-BY-4.0, converted to GGUF and quantized to Q8_0 by the
  transcribe.cpp project (https://huggingface.co/handy-computer/parakeet-tdt-0.6b-v3-gguf, revision
  90f082450fcbacdb54e5900c44ef697c9ea59622).
- Parakeet Ultra 0.6B GGUF: Moondream's parakeet-ultra, CC-BY-4.0, converted by TypeWhisper from the same checkpoint
  as the ONNX export with transcribe.cpp 0.3.1's `scripts/convert-parakeet.py` and quantized to Q8_0 with its
  `transcribe-quantize`.

TypeWhisper does not otherwise modify the model weights.
