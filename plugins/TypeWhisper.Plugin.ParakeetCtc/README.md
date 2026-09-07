# Parakeet CTC vocabulary add-on

Portable TypeWhisper SDK plugin, loaded through `VocabularyPluginLease` as an internal NVIDIA dependency. It has no separate enable switch. Activation prepares the required acoustic model and tokenizer automatically; enabled personal terms and built-in Term Packs become hints. Explicit dictionary corrections still run afterward. The legacy text booster is skipped for recordings started with CTC ready.

## Automatic model setup

Download `sherpa-onnx-nemo-parakeet_tdt_ctc_110m-en-36000-int8.tar.bz2` from the official `k2-fsa/sherpa-onnx` GitHub release `asr-models`. Verify the archive SHA-256:

`17f945007b52ccd8b7200ffc7c5652e9e8e961dfdf479cefcabd06cf5703630b`

The installer fetches the archive from `https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet_tdt_ctc_110m-en-36000-int8.tar.bz2` and the tokenizer from `https://huggingface.co/FluidInference/parakeet-ctc-110m-coreml/resolve/main/tokenizer.json`. The tokenizer SHA-256 is `9f7c517c0bf644b1b690ab037bab4d4c53aecd38e047e7154d011013ab9160db`. Both hashes must match before publication. Every tokenizer vocabulary ID is validated against `tokens.txt`.

Assets are staged beside `PluginAssetDirectory/model`. The archive has a 512 MiB download cap; the tokenizer has a 16 MiB cap. These are safety limits, not estimated download sizes. Only the two expected archive files are extracted, with bounded lengths and rejected traversal/link entries. Cancellation and a ten-minute setup limit remove staging files. A receipt records the verified source hashes and installed file hashes; subsequent activation checks these hashes and skips networking when the assets remain valid. Downloaded assets remain on disk after deactivation and package uninstall. Existing unmanaged files are retained in a sibling backup when verified assets are published.

An explicit `ModelDirectory` remains a read-only asset override: supply `model.int8.onnx`, `tokens.txt` and `tokenizer.json` yourself. Automatic setup never rewrites that directory. Missing or mismatched files prevent activation. The default development profile uses `%LOCALAPPDATA%/TypeWhisper-WinUI-DevUserData/PluginData/com.typewhisper.parakeet-ctc/model`; isolated test profiles use their own data root. Offline or failed setup preserves ordinary transcription; enabling NVIDIA again retries setup.

## Boundaries

- This 110M model is **English-trained**. German speech, names and TypeWhisper brand corrections are not validated.
- Managed NeMo-style features are independently implemented; numeric CoreML/ONNX frontend parity remains unverified. Tokenization now uses the original BPE merge ranks with lowercase/NFKC and boundary variants, following FluidAudio 0.15.5. Unknown characters fail closed rather than using unknown-token evidence.
- Scoring now uses per-token CTC scores, a base context bonus of 4.5 and long-token scaling `1 + log2(tokens/3) * 0.3` above three tokens. Auto similarity is 0.52/0.55/0.60 for at most 10/100/more terms, respectively; per-term overrides remain honored. These match the Mac default configuration, not every FluidAudio candidate/rescue heuristic. English and German real-dictation accuracy still require validation.
- At most 256 hints, 64 acoustically evaluated candidates and three-word spans per request; emission windows are bounded to 30 seconds. Missing/unalignable token timings leave the text unchanged.
- Native inference drains before unload. Cancellation rejects stale results; disabling this optional stage preserves the original dictation.
- Only settings, local asset paths, logging and capability notifications are provided by this restricted host. Secrets, localization and the event bus explicitly fail as unsupported.

## Verification

The portable test suite covers CTC repeated-label/blank transitions, bounded scoring, acoustic preference, cancellation and fail-closed settings persistence in addition to existing loader/lifecycle tests. `tests/TypeWhisper.ParakeetCtc.Probe` verifies real model inference, positive and negative acoustic controls, and optionally activation of the published package. Real microphone/TDT-token alignment, WinUI enable/restart interaction and German accuracy remain acceptance tests. These checks are not performance benchmarks.
