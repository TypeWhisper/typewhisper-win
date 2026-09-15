# Qwen3 ASR (Local) — portable V2 plugin

Offline CPU transcription with Qwen3-ASR 0.6B INT8 through sherpa-onnx 1.13.0.
This is a separate plugin (`com.typewhisper.qwen3-local`); the legacy Qwen3 STT
endpoint plugin retains its own identity and configuration.

## Use

Enable **Qwen3 ASR (Local)** in the WinUI host, download **Qwen3-ASR 0.6B INT8**,
then choose **Use model**. Downloading does not change the active model. The host
provides download progress, cancellation and removal controls. No credentials,
Python installation, server or GPU are required. Internet access is used only
to download the model; recognition runs in the process on the CPU.

To reclaim disk space after selecting the sole model, use **Unload and remove
Qwen model** in the plugin settings. This explicit action drains processing,
unloads the model, deletes its files and clears the saved selection. The generic
host removal button blocks selected models. Qwen reports ready only while a
supported model is selected and its downloaded files pass the readiness check.

The 879 MB download expands to approximately 1 GB. Allow about 2 GB of temporary
disk space during installation and around 2 GB of working memory for inference.
The package contains Windows x64 and ARM64 CPU runtimes; real execution has been
validated on x64 only.

The model supports 30 language codes, including German and English. Leaving the
language unset uses automatic recognition; explicit language choices become
per-stream hints. The .NET backend does not return a detected-language field,
so the plugin leaves that result field empty. Translation, live preview,
streaming and dictionary prompts are not advertised.

Long audio is split into windows of at most ten seconds, preferring a low-energy
boundary in the final three seconds. Segments represent these decoding windows,
not word-aligned timestamps. Native decoding is drained before cancellation is
reported; cancellation does not return a partial transcript. Unload, removal,
download, load and transcription share an operation lock.

## Model provenance and installation

- [Official model documentation](https://k2-fsa.github.io/sherpa/onnx/qwen3-asr/pretrained.html)
- [Pinned sherpa-onnx model archive](https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25.tar.bz2)
- Archive SHA-256: `393f8a14e2f5fb96746aaab342997a40641001fbd5bf9592a080a8329178ee96`

Installation verifies the size and hash, rejects unsafe archive entries, checks
all six required files and publishes a completion receipt after extraction.
Readiness checks verify receipt identity and file sizes. Failed or cancelled
downloads remain unavailable and can be retried. Models live under the host's
plugin asset directory in `Models/qwen3-asr-0.6b-int8`.

The plugin owns all native dependencies. Its packaged sherpa DLL imports a
private `qwenort.dll` to avoid another plugin's ONNX Runtime being selected.
The host does not reference this provider's assembly. `portable.proj` supplies
the standard portable package; the existing plugin smoke and headless workflows
discover the project and its tests.

## Validation

```powershell
dotnet test plugins-v2/TypeWhisper.Plugin.Qwen3Local/Tests/TypeWhisper.Plugin.Qwen3Local.Tests.csproj
```

The default suite requires no model or network and explicitly skips the real
inference test. It covers safe downloads, cancellation/retry, corrupt assets,
WAV validation, long-audio coverage, native-operation draining, and installing,
loading, restarting and removing the actual portable package.

To run real inference, download the pinned archive and extract its `test_wavs`.
Use FFmpeg to convert `de.wav` and `f1_noise.wav` to PCM16 mono 16 kHz into a
separate directory (`-ar 16000 -ac 1 -c:a pcm_s16le`). Then set:

```powershell
$env:QWEN_LOCAL_TEST_ARCHIVE = 'C:/Tests/sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25.tar.bz2'
$env:QWEN_LOCAL_TEST_ASSETS = 'C:/Tests/qwen-assets'
$env:QWEN_LOCAL_TEST_WAVS = 'C:/Tests/audio-16k'
dotnet test plugins-v2/TypeWhisper.Plugin.Qwen3Local/Tests/TypeWhisper.Plugin.Qwen3Local.Tests.csproj --logger 'console;verbosity=detailed'
```

The opt-in test reuses the archive through the production download/extraction
path, loads the staged package through the portable host, transcribes German,
English and 36 seconds of repeated speech, then checks silence, cancellation
and unload/reload. It retains model files for repeat runs and writes measured
results to `qwen-local-validation.json` under the chosen asset directory.

On 2026-09-15, all 15 tests passed on Windows x64 / Ryzen 7 7800X3D, using four
CPU inference threads. One run measured:

| Input | Audio | Recognition |
| --- | ---: | ---: |
| German | 6.72 s | 1.01 s |
| English with noise | 19.02 s | 2.15 s |
| Repeated German | 36.10 s | 5.67 s |

Loading took 2.62 s; peak test-process working set was 1,833 MiB. These samples
are smoke checks, not an accuracy benchmark. The noisy English transcript has
minor recognition errors. CPU performance varies by device.

This package is installed locally for development. Publication to the V2
catalog requires its own release archive and catalog entry.

The prescribed WinUI development build/relaunch also passed. In the real host,
the plugin was enabled, its downloaded model was loaded through **Use model**,
and `/v1/transcribe/local-file` returned the German transcript in 1.07 s. The
model inventory reported `cloud: false`, `downloaded: true`, and `active: true`.
The settings UI showed **Loaded · active for dictation**. This verifies the
file-transcription path and native runtime coexistence with the existing
Parakeet provider. Immediately after first enablement, the host required **Refresh
models** before showing the downloaded model. Model selection is persisted through
the host and restored after reactivation or restart; removing assets clears it.

A separate [physical microphone and paste check](../../docs/screenshots/qwen3-local/README.md)
completed the local speaker → microphone → Qwen → Notepad path. Its strict text
assertion failed: the model recognized "transgression" instead of "transcription".
The actual pasted text matched the host's final result, including an existing
dictionary correction. This is evidence of the working integration and a recognition
limitation, not a passing exact-transcript acceptance test.
