# Third-party notices

This package contains only TypeWhisper code. The native runtime and the model are downloaded on first use and each
is verified against a pinned SHA-256 before it is installed.

## Downloaded on first use

- transcribe.cpp 0.3.1: the transcribe.cpp authors, MIT. https://github.com/handy-computer/transcribe.cpp
  The plugin downloads the `transcribe-native-0.3.1-windows-x86_64-cpu-vulkan` release archive and keeps its DLLs,
  `contract.json` and the license texts in its `licenses` folder.
- ggml (vendored in transcribe.cpp): the ggml authors, MIT. https://github.com/ggml-org/ggml
- miniz (vendored in transcribe.cpp): Rich Geldreich, Tenacious Software LLC, RAD Game Tools and Valve Software, MIT. https://github.com/richgel999/miniz
- Voxtral Mini 4B Realtime (`Voxtral-Mini-4B-Realtime-2602-Q4_K_M.gguf`): Mistral AI's
  https://huggingface.co/mistralai/Voxtral-Mini-4B-Realtime-2602, Apache-2.0. Converted to GGUF and quantized to
  Q4_K_M by the transcribe.cpp project (https://huggingface.co/handy-computer/Voxtral-Mini-4B-Realtime-2602-gguf,
  revision 7fd3d424fc558edca2c53808578b0541ef52a81a), whose NOTICE reads: the conversion repackages the weights,
  renaming and re-laying-out tensors, and the quantized files store them at reduced numeric precision; the weights
  were not retrained or fine-tuned.

TypeWhisper does not modify the model weights.
