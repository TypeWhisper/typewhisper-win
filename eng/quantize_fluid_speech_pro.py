#!/usr/bin/env python3
"""Quantize FluidVoice's Fluid Speech Pro ONNX export into the files the NVIDIA Parakeet plugin downloads.

FluidVoice publishes an int8 build, but it keeps its weights in separate .onnx.data files, which sherpa-onnx cannot
find because it loads graphs from memory, and it uses MatMulNBits, which ran at a quarter of Parakeet's speed on a
Windows x64 CPU. This script starts from FluidVoice's float files and quantizes them dynamically, like
csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8 (QUInt8 encoder, QInt8 decoder and joiner), except that the
encoder's convolutions stay in float: quantizing them too cost about four points of word error rate. The result is
one self-contained file per graph, slightly faster than Parakeet v3 int8 on x64. The model metadata sherpa-onnx reads
(TDT, 128 features) is already in FluidVoice's encoder.

    pip install onnx==1.18.0 onnxruntime==1.22.1
    hf download FluidVoice-Labs/fluid-speech-pro-onnx --revision fa588e9f87f1b9ad0ca2e2d8f7eaf84dc3803581 \
        --include "encoder-model.onnx*" "decoder.onnx*" "joiner.onnx*" vocab.txt --local-dir fluid-speech-pro-onnx
    python eng/quantize_fluid_speech_pro.py fluid-speech-pro-onnx out

Writes encoder.int8.onnx, decoder.int8.onnx, joiner.int8.onnx and tokens.txt (FluidVoice's vocab.txt, which is
byte-identical to Parakeet v3's tokens.txt). With these versions the output matches the hashes pinned in
SherpaOnnxPlugin.cs, on macOS and Linux alike.
"""
import shutil
import sys
from pathlib import Path

from onnxruntime.quantization import QuantType, quantize_dynamic


def main():
    source, out = Path(sys.argv[1]), Path(sys.argv[2])
    out.mkdir(parents=True, exist_ok=True)
    for name, target in [("encoder-model", "encoder"), ("decoder", "decoder"), ("joiner", "joiner")]:
        encoder = target == "encoder"
        quantize_dynamic(
            model_input=str(source / f"{name}.onnx"),
            model_output=str(out / f"{target}.int8.onnx"),
            weight_type=QuantType.QUInt8 if encoder else QuantType.QInt8,
            # Only the encoder has convolutions; the decoder keeps its quantized LSTM.
            op_types_to_quantize=["MatMul"] if encoder else None,
        )
    shutil.copy(source / "vocab.txt", out / "tokens.txt")


if __name__ == "__main__":
    main()
