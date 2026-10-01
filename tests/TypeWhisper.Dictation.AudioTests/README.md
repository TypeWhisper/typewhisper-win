# Immediate dictation audio regression

This isolated Windows test project does not open a microphone, play sound, modify the clipboard, insert text, or persist personal history. It links the same capture service and hybrid state machine used by the WinUI host. Only the audio-device adapter is replaced with a deterministic 30-ms PCM replay source.

Run sample-preservation tests without a speech model:

```powershell
dotnet test tests/TypeWhisper.Dictation.AudioTests/TypeWhisper.Dictation.AudioTests.csproj --filter 'Category!=LocalParakeet'
```

Run the complete local comparison with an existing Parakeet transducer model and an installed English Windows speech synthesis voice:

```powershell
$env:TYPEWHISPER_TEST_PARAKEET_MODEL = 'C:\path\to\parakeet-tdt-0.6b'
dotnet test tests/TypeWhisper.Dictation.AudioTests/TypeWhisper.Dictation.AudioTests.csproj --logger 'console;verbosity=detailed'
```

The model directory must contain encoder.int8.onnx, decoder.int8.onnx, joiner.int8.onnx and tokens.txt. Models are never downloaded. The opt-in model test fails explicitly when its prerequisites are missing.

Speech is synthesized in memory, with leading silence removed to place speech at the key-down boundary. The same PCM is replayed with immediate capture and with a simulated 300-ms start delay. The tests assert complete sample preservation in the immediate path and exactly 4,800 missing samples in the delayed path. Real Parakeet decoding checks the opening word and final phrase; the delayed transcript is diagnostic, since its exact recognition may vary by voice/model.

Observed locally with Microsoft David Desktop:

- Immediate: `Bananas are yellow. This is a test of immediate recording.`
- Delayed: `are yellow. This is a test of immediate recording.`

This covers the state machine, audio buffering/conversion and local recognizer. It does not measure real WASAPI/device startup latency, keyboard-hook dispatch timing, or target-app paste behavior. Those still require a separate device/virtual-input end-to-end test.

## Microphone test diagnostics

Audio settings and the setup microphone step offer **Test microphone**. The test uses the same microphone priority resolver as dictation, shows the actual input name and level, and stops after 15 seconds or when you leave the page, change the microphone priority, or start dictation. It keeps only packet counts and levels in memory; audio is neither saved nor sent to a transcription provider.

The diagnostics distinguish:

- **No audio received:** no packet has arrived for at least one second, including when a previously active stream stalls. The level clears.
- **Windows reports silence:** WASAPI delivered a packet with `AUDCLNT_BUFFERFLAGS_SILENT`. The capture preserves this origin before filling its audio buffer with silence; it does not dereference the packet's data pointer.
- **Microphone sends only silence:** an unflagged packet contains only numeric zero samples, checked in every original channel before downmixing or resampling. Both signs of floating-point zero count as zero, and even tiny nonzero samples count as signal.

The UI shows the input name, level, a plain-language result and a troubleshooting hint. Technical packet counters stay internal: they count original WASAPI packets, including when several packets share one data callback. WaveIn fallback does not expose Windows silence flags and counts its data callbacks instead; it can still report missing or silent input.

`MicrophoneTestDiagnosticsTests` covers packet copying/classification, mixed packet batches, startup and stalled-stream timeouts, stopped snapshots, priority selection, synchronous first packets, fallback metadata and capture errors. It opens no real microphone. Run these checks with:

```powershell
dotnet test tests/TypeWhisper.Dictation.AudioTests/TypeWhisper.Dictation.AudioTests.csproj --filter FullyQualifiedName~MicrophoneTestDiagnosticsTests
```

WASAPI packet handling follows Microsoft's [IAudioCaptureClient::GetBuffer documentation](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer).

# Portable plugin inference

`PortableParakeetTests` exercises the published 1.1 sherpa-onnx plugin through the portable loader and PCM contract. Set `TYPEWHISPER_TEST_PARAKEET_PACKAGE` to its published package directory and `TYPEWHISPER_TEST_PARAKEET_MODEL` to the existing `parakeet-tdt-0.6b` model directory. It generates local English speech, checks transcription/token intervals, then verifies unload behavior. It never downloads models or migrates production data.

The portable inference filter runs the Parakeet TDT, Parakeet Ultra and Canary cases. Download Ultra and Canary through the development app first; all models must be in the same development plugin asset directory. It verifies real inference and catalog language metadata without recording a microphone. A second case synthesizes about a minute of distinct sentences and checks that every section survives; single-pass Canary decoding used to drop the middle of such recordings.
