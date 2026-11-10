"""pyannote speaker-diarization-community-1, as a JSON-lines worker of the diarization benchmark.

    {"op": "load", "config": {"model": "<local pipeline directory>", "device": "cuda"}}
    {"op": "diarize", "path": "<16 kHz wav>"}
    {"op": "quit"}

The pipeline runs with the settings of its config.yaml (VBx clustering, threshold 0.6) and no
speaker-count hint. The WAV is read with soundfile and handed over as a waveform, because
pyannote's own file decoding goes through torchcodec, which needs FFmpeg DLLs on Windows.
Turns come from the regular (overlapping) speaker_diarization output, since overlap is scored.
"""

from __future__ import annotations

import json
import os
import sys
import time
import traceback

PROTOCOL = os.fdopen(os.dup(sys.stdout.fileno()), "w", encoding="utf-8", buffering=1)
sys.stdout = sys.stderr

state: dict = {}


def reply(message: dict) -> None:
    PROTOCOL.write(json.dumps(message) + "\n")
    PROTOCOL.flush()


def load(config: dict) -> dict:
    os.environ.setdefault("HF_HUB_OFFLINE", "1")
    import torch
    import pyannote.audio
    from pyannote.audio import Pipeline

    device = config.get("device", "cuda")
    started = time.perf_counter()
    pipeline = Pipeline.from_pretrained(config["model"])
    pipeline.to(torch.device(device))
    if device == "cuda":
        torch.cuda.synchronize()
    state.update(pipeline=pipeline, device=device, torch=torch)
    params = pipeline.parameters(instantiated=True)
    return {
        "ok": True,
        "modelLoadMs": (time.perf_counter() - started) * 1000,
        "runtime": f"pyannote.audio {pyannote.audio.__version__} + PyTorch {torch.__version__}",
        "device": torch.cuda.get_device_name(0) if device == "cuda" else "cpu",
        "params": json.loads(json.dumps(params, default=str)),
    }


def diarize(path: str) -> dict:
    import soundfile as sf

    torch = state["torch"]
    device = state["device"]
    if device == "cuda":
        torch.cuda.reset_peak_memory_stats()
    started = time.perf_counter()
    audio, rate = sf.read(path, dtype="float32", always_2d=False)
    if rate != 16000 or audio.ndim != 1:
        raise ValueError(f"expected 16 kHz mono, got {rate} Hz with shape {audio.shape}")
    waveform = torch.from_numpy(audio)[None]
    output = state["pipeline"]({"waveform": waveform, "sample_rate": rate})
    annotation = getattr(output, "speaker_diarization", output)
    turns = [
        [round(segment.start, 3), round(segment.end, 3), str(label)]
        for segment, _, label in annotation.itertracks(yield_label=True)
    ]
    turns.sort(key=lambda t: (t[0], t[1], t[2]))
    result = {"turns": turns, "durationMs": (time.perf_counter() - started) * 1000}
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
                reply(diarize(request["path"]))
            elif op == "quit":
                reply({"ok": True})
                return
            else:
                reply({"error": f"unknown op {op!r}"})
        except Exception as error:  # noqa: BLE001
            traceback.print_exc()
            reply({"error": f"{type(error).__name__}: {error}"})


if __name__ == "__main__":
    main()
