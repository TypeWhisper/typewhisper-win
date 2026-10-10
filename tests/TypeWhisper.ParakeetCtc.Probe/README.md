# CTC functional audio probe

Run with the separately downloaded official model and its bundled `test_wavs/1.wav` (mono 16-kHz PCM16):

```powershell
dotnet run --project tests/TypeWhisper.ParakeetCtc.Probe -- <model-directory> <test_wavs/1.wav> [published-plugin-directory]
```

The fixture says `I love you.`. The probe checks real acoustic emissions and deliberately supplies the wrong transcript `I live you.`: the hint `love` must be accepted. Conversely, a `live` hint against the correct transcript must be rejected. The similarity threshold is 0.7 specifically for this short control. With the optional third argument, the published package is activated through the real portable loader and must pass the positive control too.

Timings cover the entire fixture, not actual TDT word/token timings. This does not validate real microphone input, German vocabulary, UI interaction or performance. No user audio, history or clipboard is accessed.

## Rescoring benchmark transcripts

`rescore` applies the app's CTC vocabulary rescoring (this plugin through `VocabularyPipeline`) to transcripts that
`tools/TypeWhisper.Benchmarks` wrote with `files --raw --timings`, so engines can be compared with dictionary terms:

```powershell
dotnet run --project tools/TypeWhisper.Benchmarks -- files <model> <wav-directory> --raw --timings --output engine.json
dotnet run --project tests/TypeWhisper.ParakeetCtc.Probe -- rescore <CTC model directory> engine.json <wav-directory> terms.txt
```

`terms.txt` holds one dictionary term per line, without a similarity override. The step runs in its own process
because the benchmark's sherpa-onnx runtime and this plugin's ONNX Runtime both ship `onnxruntime.dll`. It prints the
changed clips, the micro WER before and after and the rescoring time per clip.
