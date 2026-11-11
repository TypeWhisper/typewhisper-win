"""Nemotron 3 Diarization through NVIDIA NeMo, as a JSON-lines worker of the diarization benchmark.

One request per line on stdin, one response per line on stdout (see ../../README.md):

    {"op": "load", "config": {"model": "<.nemo>", "profile": "offline" | "fast32", "device": "cuda"}}
    {"op": "diarize", "path": "<16 kHz wav>", "probs": "<optional .f32 dump path>"}
    {"op": "quit"}

Frame probabilities become turns the way FluidAudio does it for Nemotron: a speaker is active in a
10 ms frame when its probability is above 0.5, and turns shorter than 0.2 s are dropped.
"""

from __future__ import annotations

import json
import os
import sys
import time
import traceback

# Everything NeMo, Lightning or tqdm print goes to stderr; stdout carries only the protocol.
PROTOCOL = os.fdopen(os.dup(sys.stdout.fileno()), "w", encoding="utf-8", buffering=1)
sys.stdout = sys.stderr

PROFILES = {
    # NVIDIA's model card: "Very high latency (offline)", 30.4 s input buffer.
    "offline": {"spkcache_len": 264, "fifo_len": 40, "chunk_len": 340, "chunk_right_context": 40, "spkcache_update_period": 300},
    # FluidAudio's fast32 preset (Nemotron3Config.fast32): 2.88 s input buffer.
    "fast32": {"spkcache_len": 264, "fifo_len": 40, "chunk_len": 32, "chunk_right_context": 4, "spkcache_update_period": 40},
}
THRESHOLD = 0.5
MIN_TURN_SECONDS = 0.2
FRAME_SECONDS = 0.01

state: dict = {}


def reply(message: dict) -> None:
    PROTOCOL.write(json.dumps(message) + "\n")
    PROTOCOL.flush()


def turns_from_probs(probs, frame_seconds: float = FRAME_SECONDS) -> list:
    """Same rule as FluidAudio's Nemotron3Diarizer.segments."""
    import numpy as np

    active = probs > THRESHOLD
    turns = []
    frames, speakers = active.shape
    for spk in range(speakers):
        column = np.concatenate([[False], active[:, spk], [False]])
        edges = np.flatnonzero(column[1:] != column[:-1])
        for start, end in zip(edges[0::2], edges[1::2]):
            if (end - start) * frame_seconds >= MIN_TURN_SECONDS:
                turns.append([round(start * frame_seconds, 3), round(end * frame_seconds, 3), f"S{spk}"])
    turns.sort(key=lambda t: (t[0], t[1], t[2]))
    return turns


def load(config: dict) -> dict:
    import torch
    import nemo
    from nemo.collections.asr.models import SortformerEncLabelModel
    from nemo.collections.asr.modules import transformer_encoder

    # NeMo runs the encoder's attention through compiled FlexAttention on CUDA, which needs Triton;
    # PyTorch ships no Triton for Windows. NeMo's own export path computes the same attention with
    # scaled_dot_product_attention and a dense padding mask, so the encoder takes that path.
    original = transformer_encoder.TransformerEncoder.forward_internal

    def forward_internal(self, audio_signal, length, bypass_pre_encode=False, for_export=False):
        return original(self, audio_signal, length, bypass_pre_encode=bypass_pre_encode, for_export=True)

    transformer_encoder.TransformerEncoder.forward_internal = forward_internal

    device = config.get("device", "cuda")
    profile = PROFILES[config["profile"]]
    started = time.perf_counter()
    model = SortformerEncLabelModel.restore_from(restore_path=config["model"], map_location=device, strict=False)
    model.eval()
    modules = model.sortformer_modules
    for key, value in profile.items():
        setattr(modules, key, value)
    modules.chunk_left_context = 0
    modules._check_streaming_parameters()
    if device == "cuda":
        torch.cuda.synchronize()
    state.update(model=model, device=device, torch=torch)
    return {
        "ok": True,
        "modelLoadMs": (time.perf_counter() - started) * 1000,
        # A venv's python.exe is a launcher; the runner samples this interpreter's memory instead.
        "pid": os.getpid(),
        "runtime": f"NeMo {nemo.__version__} + PyTorch {torch.__version__}",
        "device": torch.cuda.get_device_name(0) if device == "cuda" else "cpu",
        "asyncStreaming": bool(model._cfg.get("async_streaming", False)),
        "precision": str(next(model.parameters()).dtype).replace("torch.", ""),
        "profile": {**profile, "chunk_left_context": 0},
    }


def diarize(path: str, probs_path: str | None) -> dict:
    import numpy as np
    import soundfile as sf

    torch = state["torch"]
    model = state["model"]
    device = state["device"]
    if device == "cuda":
        torch.cuda.reset_peak_memory_stats()
    started = time.perf_counter()
    audio, rate = sf.read(path, dtype="float32", always_2d=False)
    if rate != 16000 or audio.ndim != 1:
        raise ValueError(f"expected 16 kHz mono, got {rate} Hz with shape {audio.shape}")
    signal = torch.from_numpy(audio)[None].to(device)
    length = torch.tensor([audio.shape[0]], device=device)
    with torch.inference_mode():
        preds = model.forward(audio_signal=signal, audio_signal_length=length)
    probs = preds[0].float().cpu().numpy()
    turns = turns_from_probs(probs)
    duration_ms = (time.perf_counter() - started) * 1000
    if probs_path:
        np.ascontiguousarray(probs, dtype="<f4").tofile(probs_path)
    result = {"turns": turns, "durationMs": duration_ms, "frames": int(probs.shape[0])}
    if device == "cuda":
        result["gpuAllocatedPeakMb"] = torch.cuda.max_memory_allocated() / 2**20
        result["gpuReservedPeakMb"] = torch.cuda.max_memory_reserved() / 2**20
    return result


def main() -> None:
    for line in sys.stdin:
        if not line.strip():
            continue
        try:
            request = json.loads(line)
            op = request.get("op")
            if op == "load":
                reply(load(request["config"]))
            elif op == "diarize":
                reply(diarize(request["path"], request.get("probs")))
            elif op == "quit":
                reply({"ok": True})
                return
            else:
                reply({"error": f"unknown op {op!r}"})
        except Exception as error:  # noqa: BLE001 - a failed file is reported, the worker keeps running
            traceback.print_exc()
            reply({"error": f"{type(error).__name__}: {error}"})


if __name__ == "__main__":
    main()
