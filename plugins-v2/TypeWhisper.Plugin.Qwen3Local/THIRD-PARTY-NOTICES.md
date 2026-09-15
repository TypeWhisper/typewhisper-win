# Third-party notices

- Qwen3-ASR 0.6B: Alibaba Qwen team, Apache-2.0. Model: https://huggingface.co/Qwen/Qwen3-ASR-0.6B
- sherpa-onnx 1.13.0: Xiaomi Corporation and contributors, Apache-2.0. https://github.com/k2-fsa/sherpa-onnx
- ONNX Runtime (distributed by sherpa-onnx): Microsoft Corporation, MIT. https://github.com/microsoft/onnxruntime
- SharpCompress 0.48.0: Adam Hathcock and contributors, MIT. https://github.com/adamhathcock/sharpcompress

The model is downloaded separately from the official sherpa-onnx ASR model release. Its conversion uses https://github.com/Wasser1462/Qwen3-ASR-onnx. See https://k2-fsa.github.io/sherpa/onnx/qwen3-asr/pretrained.html for model provenance.

The packaged Windows sherpa-onnx import name is changed from `onnxruntime.dll` to `qwenort.dll` to isolate the CPU runtime from other plugins. Model weights are not modified.
