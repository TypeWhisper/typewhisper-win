# Third-party notices

- Qwen3-ASR 0.6B and 1.7B: Alibaba Qwen team, Apache-2.0. Models: https://huggingface.co/Qwen/Qwen3-ASR-0.6B and https://huggingface.co/Qwen/Qwen3-ASR-1.7B
- sherpa-onnx 1.13.8: Xiaomi Corporation and contributors, Apache-2.0. https://github.com/k2-fsa/sherpa-onnx
- ONNX Runtime (distributed by sherpa-onnx): Microsoft Corporation, MIT. https://github.com/microsoft/onnxruntime
- SharpCompress 0.50.4: Adam Hathcock and contributors, MIT. https://github.com/adamhathcock/sharpcompress

The models are downloaded separately. The 0.6B model comes from the official sherpa-onnx ASR model release; see https://k2-fsa.github.io/sherpa/onnx/qwen3-asr/pretrained.html for its provenance. The 1.7B model comes from the community export https://huggingface.co/thieunv-asilla/sherpa-onnx-qwen3-asr-1.7B-int8 (Apache-2.0), pinned to revision 69eb686fd94a4a865bb5340a3d6ac0d7f1fec0d5. Both conversions use https://github.com/Wasser1462/Qwen3-ASR-onnx.

The packaged Windows sherpa-onnx import name is changed from `onnxruntime.dll` to `qwenort.dll` to isolate the CPU runtime from other plugins. Model weights are not modified.
