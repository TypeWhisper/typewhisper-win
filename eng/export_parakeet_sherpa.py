#!/usr/bin/env python3
r"""Export a Parakeet TDT 0.6B v3 NeMo checkpoint to the sherpa-onnx transducer layout.

Produces encoder.int8.onnx, decoder.int8.onnx, joiner.int8.onnx and tokens.txt, the
files the NVIDIA Parakeet plugin downloads. Used for Parakeet Ultra (#576, #781):

    pip install torch==2.8.0 --index-url https://download.pytorch.org/whl/cpu
    pip install "nemo_toolkit[asr]==3.0.0" onnx==1.18.0 onnxruntime==1.22.1
    hf download Olicorne/parakeet-tdt-0.6b-v3-ultra-onnx parakeet-tdt-0.6b-v3-ultra.nemo \
        --revision b27f93c2238aa35e138996c005a62f1e8430f801 --local-dir .
    python eng/export_parakeet_sherpa.py parakeet-tdt-0.6b-v3-ultra.nemo \
        "https://huggingface.co/moondream/parakeet-ultra (retrained nvidia/parakeet-tdt-0.6b-v3)" out

The .nemo checkpoint with Moondream's weights comes from
Olicorne/parakeet-tdt-0.6b-v3-ultra-onnx (SHA-256 91b81b3c09a335baa53208c6c70c97aed1c1165df71a51057f26232de33135da).
sherpa-onnx recognizes a TDT model by "tdt" in the encoder's url metadata, so the URL
argument must contain it.

Adapted from k2-fsa/sherpa-onnx scripts/nemo/parakeet-tdt-0.6b-v3/export_onnx.py
(Copyright 2025 Xiaomi Corp., authors: Fangjun Kuang, Apache-2.0), so the result has
the same layout as csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8. The
quantization differs in one place: k2-fsa's build quantizes the whole encoder, while
this script leaves the encoder's convolutions in float and quantizes only its MatMuls
(#781). The decoder and joiner are quantized as in k2-fsa's build.

With these versions on Linux the output matches the hashes pinned in SherpaOnnxPlugin.cs.
Quantizing the whole encoder of the same export instead reproduces the files of the
earlier release model-parakeet-ultra-int8-v1 byte for byte.
"""
import os
import sys
from pathlib import Path
from typing import Dict

import nemo.collections.asr as nemo_asr
import onnx
import torch
from onnxruntime.quantization import QuantType, quantize_dynamic


def add_meta_data(filename: str, meta_data: Dict[str, str]):
    model = onnx.load(filename)
    while len(model.metadata_props):
        model.metadata_props.pop()
    for key, value in meta_data.items():
        meta = model.metadata_props.add()
        meta.key = key
        meta.value = str(value)
    if filename == "encoder.onnx":
        onnx.save(model, filename, save_as_external_data=True, all_tensors_to_one_file=True, location="encoder.weights")
    else:
        onnx.save(model, filename)


@torch.no_grad()
def main():
    nemo_path, url, out_dir = sys.argv[1], sys.argv[2], sys.argv[3]
    if "tdt" not in url:
        raise SystemExit("The URL must contain 'tdt'; sherpa-onnx uses it to select the TDT decoder.")
    asr_model = nemo_asr.models.ASRModel.restore_from(restore_path=nemo_path, map_location="cpu")
    asr_model.eval()
    Path(out_dir).mkdir(parents=True, exist_ok=True)
    os.chdir(out_dir)

    with open("./tokens.txt", "w", encoding="utf-8", newline="\n") as f:
        for i, s in enumerate(asr_model.joint.vocabulary):
            f.write(f"{s} {i}\n")
        f.write(f"<blk> {i+1}\n")

    asr_model.encoder.export("encoder.onnx")
    asr_model.decoder.export("decoder.onnx")
    asr_model.joint.export("joiner.onnx")

    normalize_type = asr_model.cfg.preprocessor.normalize
    if normalize_type == "NA":
        normalize_type = ""

    meta_data = {
        "vocab_size": asr_model.decoder.vocab_size,
        "normalize_type": normalize_type,
        "pred_rnn_layers": asr_model.decoder.pred_rnn_layers,
        "pred_hidden": asr_model.decoder.pred_hidden,
        "subsampling_factor": 8,
        "model_type": "EncDecRNNTBPEModel",
        "version": "2",
        "model_author": "NeMo",
        "url": url,
        "comment": "Only the transducer branch is exported",
        "feat_dim": 128,
    }

    for m in ["encoder", "decoder", "joiner"]:
        encoder = m == "encoder"
        quantize_dynamic(
            model_input=f"./{m}.onnx",
            model_output=f"./{m}.int8.onnx",
            weight_type=QuantType.QUInt8 if encoder else QuantType.QInt8,
            # Quantizing the encoder's convolutions too cost accuracy (#780, #781).
            op_types_to_quantize=["MatMul"] if encoder else None,
        )

    add_meta_data("encoder.int8.onnx", meta_data)
    add_meta_data("encoder.onnx", meta_data)
    print("meta_data", meta_data)


if __name__ == "__main__":
    main()
