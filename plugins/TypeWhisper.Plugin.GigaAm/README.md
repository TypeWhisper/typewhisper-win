# GigaAM

Portable TypeWhisper plugin for local Russian speech recognition with GigaAM v3 by Sber. It mirrors the macOS GigaAM plugin: same plugin id (`com.typewhisper.gigaam`), provider id (`gigaam`), model id (`gigaam-v3-e2e-rnnt`), model files and decoding.

## Model

- The end-to-end RNN-T variant, which outputs punctuation and capitalization. Russian only: `SupportedLanguages` is `["ru"]`, and audio in other languages is transcribed as Russian.
- The int8 export from [`istupakov/gigaam-v3-onnx`](https://huggingface.co/istupakov/gigaam-v3-onnx) (about 216 MB), downloaded on first use from a pinned revision. Every file is checked against its pinned SHA-256 before it gets its final name.
- The 44 KB feature extractor (`gigaam_v3_preprocessor.onnx`, from onnx-asr 0.12.0) ships with the plugin.

## Runtime

ONNX Runtime 1.31.0 on the CPU, with the Visual C++ runtime files bundled next to it. The preprocessor turns 16 kHz audio into 64 log-mel features, the encoder runs once per chunk and a greedy transducer loop drives the decoder and joint networks (at most three tokens per frame, as in onnx-asr). The host runs the plugin in its transcription worker process, so a native crash cannot end the app.

Recordings longer than 20 seconds are cut at the quietest 200 ms near an even split, as the macOS plugin does. If a cut falls mid-sentence, the next chunk starts lowercase instead of with GigaAM's sentence capital. Tokens are joined like SentencePiece; unlike onnx-asr's detokenizer, the space before dashes and opening quotes is kept (`Европа — …`).

## Verification

`Tests/` covers the vocabulary, chunking, download verification, model lifecycle and the staged package. On the 300 Russian clips of the TypeWhisper benchmark (FLEURS, Russian LibriSpeech, Common Voice), the plugin's words match onnx-asr with the same ONNX feature extractor on every clip; the transcripts differ only in the kept spaces before dashes and quotes. A clip takes about 250 ms on a 16-thread desktop CPU.

The opt-in live test runs the real model:

```powershell
$env:GIGAAM_MODEL_DIR = "C:\path\to\gigaam-v3-onnx"   # the four downloaded files
$env:GIGAAM_AUDIO = "C:\path\to\russian-16k.wav"      # 16 kHz mono 16-bit
dotnet test plugins/TypeWhisper.Plugin.GigaAm/Tests --filter TranscribesRealAudio
```
