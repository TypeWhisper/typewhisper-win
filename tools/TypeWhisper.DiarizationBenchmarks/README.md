# Diarization benchmark (Windows)

Runs the `diarization-v1` set of [TypeWhisper/typewhisper-benchmark](https://github.com/TypeWhisper/typewhisper-benchmark)
(`bench/diarization`) through local diarization engines on Windows and writes one hypothesis file per
engine in that repository's format. Scoring happens only there, so macOS, Windows and iOS are scored by
the same code. Background: #399.

This is a measurement tool. It does not change the app and is not part of the solution build.

## Engines

| Engine id | Runtime | Model | Device |
|---|---|---|---|
| `nemotron3-nemo-offline` | NVIDIA NeMo (main), PyTorch CUDA | `nvidia/Nemotron-3-Diarization` `.nemo` | CUDA |
| `nemotron3-nemo-fast32` | same | same | CUDA |
| `nemotron3-onnx-dml-fast32` | ONNX Runtime DirectML, C# | `onnx-community/Nemotron-3-Diarization-ONNX` fp32, Gelu as `com.microsoft` Gelu | DirectML |
| `nemotron3-onnx-cpu-fast32` | ONNX Runtime CPU (the version the app pins), C# | same | CPU |
| `pyannote-community1-cuda` | pyannote.audio 4, PyTorch CUDA | `pyannote/speaker-diarization-community-1` | CUDA |
| `sherpa-onnx-eres2net-cpu` | sherpa-onnx (the version the app pins), C# | pyannote segmentation 3.0 int8 + 3D-Speaker ERes2Net base | CPU |

`nemotron3-onnx-dml-offline` and `nemotron3-onnx-cpu-offline` are extra configurations (`engines` lists them).

Nemotron profiles, in 80 ms encoder frames:

- `offline`: the model card's 30.4 s profile (chunk 340, right context 40, FIFO 40, speaker cache 264, update period 300).
- `fast32`: FluidAudio's `fast32` preset, 2.88 s latency (chunk 32, right context 4, FIFO 40, speaker cache 264, update period 40).

Frame probabilities become turns the way FluidAudio does it for the macOS hypotheses: a speaker is active in a
10 ms frame when its probability is above 0.5, and turns shorter than 0.2 s are dropped. pyannote and sherpa-onnx
run with their default pipeline settings; no engine gets the reference speaker count.

### Notes on the runtimes

- **NeMo**: the PyPI release 3.0.0 cannot load the checkpoint (no RoPE Transformer encoder), so the worker
  installs NeMo from a pinned commit of `NVIDIA-NeMo/Speech`. NeMo computes the encoder attention with compiled
  FlexAttention on CUDA, which needs Triton; PyTorch has no Triton for Windows. The worker routes the encoder
  through NeMo's own export path instead, which computes the same attention with `scaled_dot_product_attention`
  and a dense padding mask. NeMo runs in fp32 with its synchronous streaming update (the model's default).
- **ONNX**: the onnx-community export is one stateless chunk step: chunk features and cached embeddings in,
  10 ms logits, the chunk's pre-encoded embeddings and the learned silence embedding out. The speaker cache and
  FIFO queue (Arrival-Order Speaker Cache with its compression) live outside the graph.
  `Workers/NemotronOnnx/NemotronStreamingDiarizer.cs` ports them from Transformers'
  `Nemotron3DiarizationSpeakerCache`, and `MelFrontend.cs` ports the log-mel front end. `compare` checks the
  port against NeMo frame by frame. DirectML runs on the DXGI adapter whose name contains `NVIDIA`.
- **DirectML**: three changes make it usable; without them a step is slower than on the CPU.
  - The export uses ONNX `Gelu` (opset 20), which the DirectML provider does not implement, so all 31 Gelu
    nodes ran on the CPU with copies in both directions. `scripts/dml_gelu.py` writes `model.dml.onnx`, the
    same graph with `com.microsoft` Gelu (the same exact erf Gelu), next to the original; the weights stay
    in `model.onnx_data`.
  - With free dimensions DirectML runs every operator on its own. The DirectML engine fixes the input shapes:
    the cached frames are padded on the left to speaker cache + FIFO, the chunk on the right to chunk + right
    context, and the padding is masked. On the CPU provider this padding gives the same probabilities as
    unpadded input.
  - With fixed shapes DirectML fuses the graph, and the fused graph returns wrong probabilities (as if there
    were no speaker cache). The worker turns graph fusion off (`ep.dml.disable_graph_fusion`).

  One fast32 step (2.56 s of audio) on an RTX 4060 Ti: about 420 ms as exported, 210 ms with the Gelu change,
  80 ms with fixed shapes, 25 ms with both. ONNX Runtime on the CPU (8 threads, Ryzen 7 7800X3D) needs about
  140 ms.
- **pyannote**: the pipeline gets the waveform from `soundfile`, because its own decoding needs FFmpeg DLLs on
  Windows. Community-1 is gated on Hugging Face.

## Layout

- `Runner/`: the command-line tool. Verifies the set, starts one worker at a time, writes the hypothesis files,
  samples each worker's working set and dedicated GPU memory per file, and compares frame probabilities.
- `Workers/`: C# workers. Separate processes, because the sherpa-onnx package and ONNX Runtime ship different
  `onnxruntime.dll` files. `NemotronOnnx.Cpu` and `NemotronOnnx.DirectML` build the same sources against the
  two ONNX Runtime packages.
- `python/nemo`, `python/pyannote`: uv projects with locked dependencies for the Python workers.
- `scripts/download_set.py`: downloads and verifies the audio.

All workers speak the same JSON-lines protocol on stdin/stdout:
`{"op":"load","config":{…}}`, `{"op":"diarize","path":"…","probs":"…"}` (the optional `probs` path receives the
raw 10 ms probabilities as little-endian float32 `[frames × 8]`), `{"op":"quit"}`.

## Running

Requirements: .NET 10 SDK, Python 3.12 with `uv` (`python -m pip install uv`), an NVIDIA GPU for the CUDA engines.
Everything heavy lives in a data directory outside the repository (about 20 GB with both Python environments).
Keep the path short: Windows' 260-character path limit breaks the Python environments in deep directories.

```powershell
$env:TYPEWHISPER_DIARIZATION_DATA = "F:\diarization-bench"
dotnet run -c Release --project tools/TypeWhisper.DiarizationBenchmarks/Runner -- setup
dotnet run -c Release --project tools/TypeWhisper.DiarizationBenchmarks/Runner -- compare --items synthetic/en-3sp-60s
dotnet run -c Release --project tools/TypeWhisper.DiarizationBenchmarks/Runner -- run
dotnet run -c Release --project tools/TypeWhisper.DiarizationBenchmarks/Runner -- run --engines sherpa-onnx-eres2net-cpu --items synthetic/
```

`setup` downloads the audio (the AMI mirror is slow and refuses range requests, so files load in parallel and a
broken file starts over), the pinned model files (SHA-256 checked), creates the Python environments under
`<data>\venvs` and builds the workers. pyannote Community-1 is gated: accept its conditions on Hugging Face and set
`HF_TOKEN`, or copy a snapshot of the pinned revision to `<data>\models\pyannote-community-1`.

`run` writes `<data>\hypotheses\windows\<engineId>.json` after every file; `--resume` continues an interrupted run.
It refuses to start while the GPU is busy with something else (a game, another model). Copy the hypothesis files to
`bench/diarization/hypotheses/windows/` in the benchmark repository and score them there.

Measurement rules, as in the benchmark README: one engine loaded at a time, one file at a time, nothing else heavy
running. `processingMs` covers reading the WAV, inference and turn extraction. `prepareMs` covers starting the
worker process, importing the runtime and loading the model, without downloads and without a warm-up file.
`peakMemoryMb` is the worker's peak working set during the file; `settings.peakGpuDedicatedMb` is its peak dedicated
GPU memory over the run (Windows GPU process memory counters).
