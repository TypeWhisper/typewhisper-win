# Third-Party Notices

This plugin ports the log-mel front end of Transformers' `NemotronAsrStreamingFeatureExtractor` and the streaming
speaker cache of `Nemotron3DiarizationSpeakerCache` (https://github.com/huggingface/transformers, Apache License 2.0)
to C#.

The portable package includes full license texts in `Licenses/`:

- ONNX Runtime 1.24.4 with DirectML (`Microsoft.ML.OnnxRuntime.DirectML`), MIT License, with its upstream
  third-party notices (`ONNXRuntime-LICENSE.txt`, `ONNXRuntime-ThirdPartyNotices.txt`).
- DirectML 1.15.4 (`Microsoft.AI.DirectML`), Microsoft Software License Terms, with its third-party notices
  (`DirectML-LICENSE.txt`, `DirectML-ThirdPartyNotices.txt`).
- The OpenMDW License Agreement 1.1 of the model (`OpenMDW-1.1.txt`).

The model is not bundled with this plugin. When the user downloads it, the ONNX export
[onnx-community/Nemotron-3-Diarization-ONNX](https://huggingface.co/onnx-community/Nemotron-3-Diarization-ONNX) of
[NVIDIA Nemotron 3 Diarization](https://huggingface.co/nvidia/Nemotron-3-Diarization) is licensed separately under the
[OpenMDW License Agreement, version 1.1](https://openmdw.ai/license/1-1/). NVIDIA and Nemotron are trademarks of
NVIDIA Corporation.
