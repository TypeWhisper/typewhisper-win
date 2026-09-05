# Local Parakeet timing probe

Run the already-started legacy API without activating a model or downloading assets:

```powershell
dotnet run --project tools/TypeWhisper.Benchmarks -- api http://127.0.0.1:8978
```

For the current WinUI-equivalent decoder configuration (not the application):

```powershell
dotnet run --project tools/TypeWhisper.Benchmarks -- decoder C:\path\to\parakeet-tdt-0.6b
```

Only synthetic English Windows speech is used. No microphone, clipboard, dictation-control endpoints or history endpoints are accessed. The inspected legacy local-file transcription handler does not add history entries. Tokens, if required, come from `TYPEWHISPER_BENCHMARK_TOKEN` and are never logged. Requests and authentication are restricted to loopback without redirects.

Each clip has one unmeasured warm-up and three measured runs; JSON lines include PCM hashes, transcripts, wall time, API-reported processing time, median and range. Verify hashes before comparing separate invocations. Three runs provide a preliminary baseline, not a reliable tail-latency estimate.

Do not claim an app speedup from API versus direct decoder measurements: the API includes audio conversion/postprocessing, has an already-loaded model and may use different threads, runtime versions or model files. The local probe mirrors the current WinUI CPU/thread configuration but omits capture, live preview, dictionary processing, UI, history and paste. API `processing_time` is recorded verbatim; its measurement boundary must be verified before comparing it with wall time. Run modes sequentially, with other recordings/inference stopped. Existing resident app models can affect memory pressure.
