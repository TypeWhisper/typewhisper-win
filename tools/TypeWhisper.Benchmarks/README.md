# Local Parakeet timing probe

Run the already-started legacy API without activating a model or downloading assets:

```powershell
dotnet run --project tools/TypeWhisper.Benchmarks -- api http://127.0.0.1:8978
```

For the current WinUI-equivalent decoder configuration (not the application):

```powershell
dotnet run --project tools/TypeWhisper.Benchmarks -- decoder C:\path\to\parakeet-tdt-0.6b
```

To score a transducer model directory (`encoder`, `decoder`, `joiner` as `.int8.onnx` or `.onnx`, plus `tokens.txt`) for accuracy and speed:

```powershell
dotnet run -c Release --project tools/TypeWhisper.Benchmarks -- wer C:\path\to\model C:\path\to\corpus-cache --output results.json
```

`wer` downloads the first clips per language of the pinned public FLEURS test corpus (`FluidInference/fleurs`, the corpus `eng/benchmark_cohere_quantizations.py` uses) into the cache directory and decodes them with the plugin's CPU configuration. Each clip is first raised to the 0.707 peak the app's recorder normalizes to, because FLEURS contains near-silent recordings that otherwise decode to nothing. It prints WER, CER, real-time factor and median latency per language, then the macro and micro averages, load time and peak working set. `--languages de_de,en_us` and `--samples 100` select the subset, `--noise-snr 10` adds seeded white noise at that signal-to-noise ratio, and `--concat-seconds 300` joins clips into single passes of that length. Text is lowercased and stripped of punctuation before scoring, without number or spelling normalization, so absolute values are higher than leaderboard figures. Compare models only on the same subset and options.

`wer` also accepts a Qwen3-ASR directory (`conv_frontend.onnx`, `encoder.int8.onnx`, `decoder.int8.onnx`, `tokenizer/`) and then decodes like the Qwen3 ASR (Local) plugin, in windows of at most ten seconds.

To transcribe your own recordings, put 16 kHz mono 16-bit PCM WAV files in one directory:

```powershell
dotnet run -c Release --project tools/TypeWhisper.Benchmarks -- files C:\path\to\model C:\path\to\recordings --threads 4 --output results.json
```

A UTF-8 `name.txt` next to `name.wav` holds what was said (verbatim). Such clips get word and character edits after the same normalization as `wer`. An optional `name.formatted.txt` holds the intended final text, for example digits and code identifiers; the `exact` flag compares the transcript with it, or with `name.txt` when it is missing, including punctuation and casing. The summary adds micro WER/CER and the number of exact matches. Clips without a `.txt` are only transcribed and timed. Like `wer`, `files` raises each clip to the recorder's 0.707 peak; `--raw` decodes the files at their original level instead. Keep personal recordings outside the repository.

The `api` and `decoder` modes use only synthetic English Windows speech. No microphone, clipboard, dictation-control endpoints or history endpoints are accessed. The inspected legacy local-file transcription handler does not add history entries. Tokens, if required, come from `TYPEWHISPER_BENCHMARK_TOKEN` and are never logged. Requests and authentication are restricted to loopback without redirects.

Each clip has one unmeasured warm-up and three measured runs; JSON lines include PCM hashes, transcripts, wall time, API-reported processing time, median and range. Verify hashes before comparing separate invocations. Three runs provide a preliminary baseline, not a reliable tail-latency estimate.

Do not claim an app speedup from API versus direct decoder measurements: the API includes audio conversion/postprocessing, has an already-loaded model and may use different threads, runtime versions or model files. The local probe mirrors the current WinUI CPU/thread configuration but omits capture, live preview, dictionary processing, UI, history and paste. API `processing_time` is recorded verbatim; its measurement boundary must be verified before comparing it with wall time. Run modes sequentially, with other recordings/inference stopped. Existing resident app models can affect memory pressure.
