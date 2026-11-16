# NVIDIA Nemotron Diarization portable plugin

Local speaker diarization: tells who spoke when in a recording, for up to 8 speakers. Plugin ID
`com.typewhisper.nemotron-diarization`, version `1.0.0`, minimum host `1.3.0`, manifest category `diarization`,
locality `local`. It implements `IAudioDiarizationProvider` (see
[Speaker diarization](../../docs/PLUGIN-PACKAGES-1.1.md#speaker-diarization-plugin-api-130)); the audio never
leaves the PC.

## Model

[NVIDIA Nemotron 3 Diarization](https://huggingface.co/nvidia/Nemotron-3-Diarization) (Streaming Sortformer, 8
speaker outputs) in the fp32 ONNX export
[onnx-community/Nemotron-3-Diarization-ONNX](https://huggingface.co/onnx-community/Nemotron-3-Diarization-ONNX),
pinned to revision `353b6f8ad2cac3580e982d7fbdf0a010786b0406`. The model is not bundled; the host's model card in
the plugin settings downloads it on request, subject to the model download policy:

| File | Size | SHA-256 |
|---|---|---|
| `onnx/model.onnx` | 303,466 bytes | `12a7c98fc1ee6ec98ea728c7ffd9fcf6b245f3c9c1ea9f7c2a7cc47c023e3f9d` |
| `onnx/model.onnx_data` | 398,184,448 bytes | `c293d9b5930eb9f6172f095ced052c0d1bbdbeb2594a115497583ca209b1dbd6` |

Downloads resume, and every file is checked by size and SHA-256 before it is used. The files live in the plugin's
asset directory under `Models/nemotron-3-diarization-353b6f8ad2ca/`. The model is licensed under the
[OpenMDW License Agreement 1.1](https://openmdw.ai/license/1-1/).

## How it runs

The ONNX graph is one stateless step: chunk features and cached embeddings in, 10 ms speaker logits, the chunk's
pre-encoded embeddings and the learned silence embedding out. The plugin keeps the rest:

- `MelFrontend` ports Transformers' `NemotronAsrStreamingFeatureExtractor`: pre-emphasis 0.97, 25 ms Hann window in a
  512-point FFT, 10 ms hop, 128 Slaney mel bands, log with a 2^-24 guard. It computes each chunk's frames on demand,
  so a long recording is never held as features in memory.
- `NemotronStreamingDiarizer` ports the Arrival-Order Speaker Cache and FIFO queue of Transformers'
  `Nemotron3DiarizationSpeakerCache`, including its compression.
- `SpeakerTurns` turns probabilities into turns as FluidAudio does for the macOS app: a speaker is active above 0.5
  in a 10 ms frame, turns shorter than 0.2 s are dropped, and speakers are labelled `S1`, `S2`, … in the order they
  first speak. Each turn's confidence is its mean probability.

### Settings

- **Context**: `fast32` (default; FluidAudio's preset, 2.56 s chunks with 0.32 s look-ahead) or `offline` (the model
  card's 30.4 s profile). In 80 ms encoder frames: chunk 32 / right context 4 / FIFO 40 / speaker cache 264 / update
  period 40, and 340 / 40 / 40 / 264 / 300.
- **Processing device**: the CPU by default. When DXGI reports a hardware graphics card, each one is offered through
  DirectML and the choice applies from the next run. A card that cannot open the model falls back to the CPU and logs
  why. The device list is hidden on PCs without a graphics card.

### DirectML

The package ships the DirectML build of ONNX Runtime 1.24.4 (its CPU provider serves the CPU setting) and DirectML
1.15.4 for x64 and arm64. Three changes make DirectML faster than the CPU:

- The export uses ONNX `Gelu` (opset 20), which DirectML does not implement, so every Gelu would run on the CPU with
  copies in both directions. `OnnxGeluRewrite` writes `model.dml.onnx` next to the download: the same graph with the
  `com.microsoft` Gelu (the same exact erf Gelu) and the weights still in `model.onnx_data`. It edits the protobuf
  wire format directly and produces the same bytes as the `onnx` Python package for that change (31 nodes); the result
  is checked against SHA-256 `9b59b9793505479e7403dc8575d3abaa75783c02c73e03dfc6c21748d4849987`.
- The input shapes are fixed to the profile: cached frames are padded on the left to speaker cache + FIFO, the chunk
  on the right to chunk + look-ahead, and the padding is masked. With free dimensions DirectML runs every operator on
  its own.
- Graph fusion is off (`ep.dml.disable_graph_fusion`): with fixed shapes the fused graph returns probabilities as if
  there were no speaker cache.

ONNX Runtime loads `DirectML.dll` by name, which would find Windows' older copy in System32, so the plugin loads the
packaged one by path first.

## Validation

```powershell
dotnet msbuild plugins/TypeWhisper.Plugin.Diarization.Nemotron/portable.proj -t:Build -p:Configuration=Release
dotnet test plugins/TypeWhisper.Plugin.Diarization.Nemotron/Tests -c Release
```

The tests cover the mel front end against reference values of the Transformers feature extractor, the speaker cache
and FIFO on a stand-in graph (update periods, compression, equal results with fixed and free shapes, progress and
cancellation), turn extraction, the Gelu rewrite on synthetic graphs, downloads with size and SHA-256 checks,
settings, and installing the package into the plugin runtime. Three tests need the pinned model files and are skipped
without them; set `TYPEWHISPER_NEMOTRON_MODEL_DIR` to a folder with `model.onnx` and `model.onnx_data` to run them,
and optionally `TYPEWHISPER_DIARIZATION_AUDIO` to a 16 kHz mono 16-bit WAV file. They check the rewrite of the real
graph and diarize the file on the CPU and, when a graphics card is present, with DirectML.
