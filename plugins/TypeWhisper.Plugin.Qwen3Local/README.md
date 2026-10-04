# Qwen3 ASR (Local) — portable V2 plugin

Offline CPU transcription with Qwen3-ASR 0.6B or 1.7B INT8 through sherpa-onnx
1.13.0. This is a separate plugin (`com.typewhisper.qwen3-local`); the legacy
Qwen3 STT endpoint plugin retains its own identity and configuration.

## Models

| Model | Tier | Download | Installed | Working memory | Speed (RTF) |
| --- | --- | ---: | ---: | ---: | ---: |
| Qwen3-ASR 0.6B INT8 (recommended) | Low memory | 879 MB | ~1 GB | ~2 GB | 0.15 |
| Qwen3-ASR 1.7B INT8 | Higher accuracy, slower | 2.4 GB | 2.4 GB | ~3.5 GB | 0.27 |

Both models run on the CPU only; no GPU execution provider is used. Speed is the
real-time factor on a Ryzen 7 7800X3D with four inference threads (lower is
faster) and varies by device. The settings page shows the tier at the start of
each model's size line.

The macOS plugin offers MLX models at 4, 5, 6 and 8 bit and BF16. Windows has no
validated sherpa-onnx artifact for those precisions, so its tiers are the two INT8
models above; the names state the ONNX precision and are not byte-equivalent to
the MLX models. On macOS the quick picks are low memory (0.6B 6-bit), balanced
(1.7B 6-bit) and high quality (1.7B 8-bit). On Windows, 0.6B INT8 covers low
memory and 1.7B INT8 covers higher accuracy.

## Use

Enable **Qwen3 ASR (Local)** in the WinUI host, download a model, then choose
**Use model**. Downloading does not change the active model. The host provides
download progress, cancellation and removal controls. No credentials, Python
installation, server or GPU are required. Internet access is used only to
download models; recognition runs in the process on the CPU.

Before downloading, the plugin removes staging folders left by an interrupted
download and checks that the drive has room for the model and a 256 MB reserve:
the 879 MB archive plus about 1.1 GB of extracted files for 0.6B, or 2.4 GB for
1.7B. If it does not, the download stops immediately and names the required and
available space.

Only one model is loaded at a time; selecting the other model unloads the first
before loading the new one. Models that are not selected can be removed with the
host's removal button. To remove the selected model, use **Unload and remove
selected Qwen model** in the plugin settings. This explicit action drains
processing, unloads the model, deletes its files and clears the saved selection.
Qwen reports ready only while a supported model is selected and its downloaded
files pass the readiness check. After this settings action, reopen the plugin
page before downloading again; the current host needs a fresh model view after
rebuilding its settings controls.

The package contains Windows x64 and ARM64 CPU runtimes; real execution has been
validated on x64 only.

The models support 30 language codes, including German and English. Leaving the
language unset uses automatic recognition; explicit language choices become
per-stream hints. The .NET backend does not return a detected-language field,
so the plugin leaves that result field empty. Translation, live preview,
streaming and dictionary prompts are not advertised.

Long audio is split into windows of at most ten seconds, preferring a low-energy
boundary in the final three seconds. Segments represent these decoding windows,
not word-aligned timestamps. Quiet input is raised to the 0.707 peak the recorder
uses for dictation, so files and API audio are decoded at the same level; the
1.7B model drops whole windows of quiet speech otherwise. A `language X<asr_text>`
marker that the 1.7B model sometimes emits as plain text is removed. Native
decoding is drained before cancellation is reported; cancellation does not return
a partial transcript. Unload, removal, download, load and transcription share an
operation lock.

## Model provenance and installation

Qwen3-ASR 0.6B INT8 is the official sherpa-onnx build:

- [Official model documentation](https://k2-fsa.github.io/sherpa/onnx/qwen3-asr/pretrained.html)
- [Pinned sherpa-onnx model archive](https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25.tar.bz2)
- Archive SHA-256: `393f8a14e2f5fb96746aaab342997a40641001fbd5bf9592a080a8329178ee96`

sherpa-onnx publishes no 1.7B build. Qwen3-ASR 1.7B INT8 comes from a community
export in the same layout, made with the converter of the official archive
([Wasser1462/Qwen3-ASR-onnx](https://github.com/Wasser1462/Qwen3-ASR-onnx)) from
[Qwen/Qwen3-ASR-1.7B](https://huggingface.co/Qwen/Qwen3-ASR-1.7B):

- [thieunv-asilla/sherpa-onnx-qwen3-asr-1.7B-int8](https://huggingface.co/thieunv-asilla/sherpa-onnx-qwen3-asr-1.7B-int8),
  pinned to revision `69eb686fd94a4a865bb5340a3d6ac0d7f1fec0d5` (Apache-2.0)

| File | Size | SHA-256 |
| --- | ---: | --- |
| `conv_frontend.onnx` | 48,080,441 | `3cb27a9fe94d95c938e476f2012b21aba2ec0bfceef33b0e58acd208946bafdd` |
| `encoder.int8.onnx` | 314,222,162 | `a5deedae034ece715de8ed204378d8c77f889af3a60c2566581135e84cced7cd` |
| `decoder.int8.onnx` | 2,037,458,645 | `c43c853fa6e97d08365cb8a5502b360b595cd43c00dc60e4d8ca7cc18cad460b` |
| `tokenizer/merges.txt` | 1,671,853 | `8831e4f1a044471340f7c0a83d7bd71306a5b867e95fd870f74d0c5308a904d5` |
| `tokenizer/vocab.json` | 2,776,833 | `ca10d7e9fb3ed18575dd1e277a2579c16d108e32f27439684afa0e10b1440910` |
| `tokenizer/tokenizer_config.json` | 12,487 | `4942d005604266809309cabc9f4e9cb89ce855d59b14681fdc0e1cc62ea26c4c` |

A second, independent export of the same model has an identical decoder; the
tokenizer files are byte-identical to the official 0.6B archive.

For 0.6B, installation verifies the archive size and hash, rejects unsafe archive
entries and checks all six required files. For 1.7B, each file is downloaded from
the pinned revision and must match its size and SHA-256. Both publish a
completion receipt only for a verified complete set; readiness checks verify the
receipt identity and file sizes. Failed or cancelled downloads remain unavailable
and can be retried. Models live under the host's plugin asset directory in
`Models/qwen3-asr-0.6b-int8` and `Models/qwen3-asr-1.7b-int8`.

The plugin owns all native dependencies. Its packaged sherpa DLL imports a
private `qwenort.dll` to avoid another plugin's ONNX Runtime being selected.
The host does not reference this provider's assembly. `portable.proj` supplies
the standard portable package; the existing plugin smoke and headless workflows
discover the project and its tests.

## Accuracy and speed

Measured on 2026-10-04 with `tools/TypeWhisper.Benchmarks` on Windows x64 / Ryzen 7
7800X3D, four CPU threads, with the plugin's windowing and peak level.

The first 100 clips each of the pinned public FLEURS test set (German and English;
lowercased and stripped of punctuation, without number normalization):

| | 0.6B INT8 | 1.7B INT8 |
| --- | ---: | ---: |
| WER German | 13.2 % | 9.6 % |
| WER English | 8.0 % | 7.0 % |
| CER German / English | 6.2 % / 5.2 % | 4.4 % / 4.3 % |
| Real-time factor | 0.148 | 0.266 |
| Load time | 3.4 s | 4.5 s |
| Peak working set | 2.1 GB | 3.5 GB |

Before the plugin removed the `language X<asr_text>` marker, it appeared in 38 of
200 1.7B transcripts and raised its WER to 14.4 % (German) and 9.0 % (English). With 1.7B, one English clip
produced no text and another a single unrelated word.

Twelve self-recorded German dictation clips (code, numbers, formatting, proper
nouns, URLs; private corpus) compared against the intended final text: 1.7B matched
6 of 12 exactly including punctuation and digits, 0.6B 3 of 12. 1.7B wrote numbers
as digits, while 0.6B spelled them out; 1.7B once answered a German SQL sentence in
English. The clips were recorded through a remote-desktop microphone, so this is
indicative only.

## Validation

```powershell
dotnet test plugins/TypeWhisper.Plugin.Qwen3Local/Tests/TypeWhisper.Plugin.Qwen3Local.Tests.csproj
```

The default suite requires no model or network and explicitly skips the real
inference tests. It covers safe archive and per-file downloads, checksum and size
failures, cancellation/retry, abandoned staging cleanup, corrupt assets, the
pinned 1.7B file set, independent download, selection, loading and removal of
both models, peak normalization, marker removal, WAV validation, long-audio
coverage, native-operation draining, and installing, loading, restarting and
removing the actual portable package.

To run real inference, download the pinned 0.6B archive and extract its
`test_wavs`. Use FFmpeg to convert `de.wav` and `f1_noise.wav` to PCM16 mono
16 kHz into a separate directory (`-ar 16000 -ac 1 -c:a pcm_s16le`). For 1.7B,
download the six pinned files into one directory, keeping `tokenizer/`. Then set:

```powershell
$env:QWEN_LOCAL_TEST_ARCHIVE = 'C:/Tests/sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25.tar.bz2'
$env:QWEN_LOCAL_TEST_LARGE_FILES = 'C:/Tests/qwen3-asr-1.7b-int8'
$env:QWEN_LOCAL_TEST_ASSETS = 'C:/Tests/qwen-assets'
$env:QWEN_LOCAL_TEST_WAVS = 'C:/Tests/audio-16k'
dotnet test plugins/TypeWhisper.Plugin.Qwen3Local/Tests/TypeWhisper.Plugin.Qwen3Local.Tests.csproj --logger 'console;verbosity=detailed'
```

Each model test runs only when its archive or file variable is set. It installs
the local files through the production download path, including every size and
hash check, loads the staged package through the portable host, transcribes
German, English and 36 seconds of repeated speech, then checks silence,
cancellation and unload/reload. It retains model files for repeat runs and
writes measured results to `qwen-local-validation-<model>.json` under the chosen
asset directory.

On 2026-10-05, both real inference tests passed on Windows x64 / Ryzen 7 7800X3D
with four CPU inference threads:

| Input | Audio | 0.6B | 1.7B |
| --- | ---: | ---: | ---: |
| German | 6.72 s | 1.07 s | 2.08 s |
| English with noise | 19.02 s | 2.29 s | 4.34 s |
| Repeated German | 36.10 s | 5.71 s | 10.85 s |

Loading took 2.75 s and 4.35 s; peak test-process working set was 1,809 MiB and
3,133 MiB. These samples are smoke checks; see the accuracy section for the
benchmark.

This package is installed locally for development. Publication to the V2
catalog requires its own release archive and catalog entry.

On 2026-09-15, the prescribed WinUI development build/relaunch passed with
version 1.0.0 and the 0.6B model. In the real host, the plugin was enabled, its
downloaded model was loaded through **Use model**, and
`/v1/transcribe/local-file` returned the German transcript in 1.07 s. The model
inventory reported `cloud: false`, `downloaded: true`, and `active: true`. The
settings UI showed **Loaded · active for dictation**. This verifies the
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
