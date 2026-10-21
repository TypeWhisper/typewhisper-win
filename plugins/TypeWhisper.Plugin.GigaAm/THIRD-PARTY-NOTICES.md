# Third-party notices

The portable package includes the full license texts in `Licenses/`.

## Bundled

- GigaAM v3 feature extractor (`gigaam_v3_preprocessor.onnx`): from onnx-asr 0.12.0 by Ilya Stupakov, MIT, `onnx_asr/preprocessors/data/gigaam_v3.onnx`, SHA-256 `919c3bc8a434b6e733e5fe5dcd247974d0a7b1f2bc5b6995b6ad55f7225e014d`. https://github.com/istupakov/onnx-asr. License: `Licenses/onnx-asr-LICENSE.txt`.
- ONNX Runtime 1.31.0 (NuGet `Microsoft.ML.OnnxRuntime`): Microsoft Corporation, MIT. https://github.com/microsoft/onnxruntime. License and upstream notices: `Licenses/ONNXRuntime-LICENSE.txt`, `Licenses/ONNXRuntime-ThirdPartyNotices.txt`.
- .NET libraries that ONNX Runtime's managed API depends on: .NET Foundation and contributors, MIT. License and upstream notices: `Licenses/DotNet-LICENSE.txt`, `Licenses/DotNet-ThirdPartyNotices.txt`.
- Microsoft Visual C++ Redistributable runtime files (`msvcp140.dll`, `msvcp140_1.dll`, `vcruntime140.dll`, `vcruntime140_1.dll`, bundled next to ONNX Runtime so it loads on machines without the redistributable): Microsoft Corporation, redistributable under the Microsoft Visual Studio license terms.

## Downloaded on first use

The model weights are not bundled. The plugin downloads the int8 GigaAM v3 end-to-end RNN-T export (`v3_e2e_rnnt_encoder.int8.onnx`, `v3_e2e_rnnt_decoder.int8.onnx`, `v3_e2e_rnnt_joint.int8.onnx`, `v3_e2e_rnnt_vocab.txt`) from https://huggingface.co/istupakov/gigaam-v3-onnx (MIT), pinned to revision `322c3b29492673eb7d0b434bfa9dfb8653e34d02` and to the SHA-256 of every file. The export is based on GigaAM by Sber (GigaChat Team), MIT, https://github.com/salute-developers/GigaAM. License: `Licenses/GigaAM-LICENSE.txt`.

The transducer decoding follows onnx-asr's greedy RNN-T search; this is an independent C# implementation of the same steps, matching the macOS plugin. The model files are not modified.
