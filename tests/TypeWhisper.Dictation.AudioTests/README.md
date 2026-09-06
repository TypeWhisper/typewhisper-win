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
# Portable plugin inference

`PortableParakeetTests` exercises the published 1.1 sherpa-onnx plugin through the portable loader and PCM contract. Set `TYPEWHISPER_TEST_PARAKEET_PACKAGE` to its published package directory and `TYPEWHISPER_TEST_PARAKEET_MODEL` to the existing `parakeet-tdt-0.6b` model directory. It generates local English speech, checks transcription/token intervals, then verifies unload behavior. It never downloads models or migrates production data.

The portable inference filter now runs both Parakeet and Canary cases. Download Canary through the development app first; both models must be in the same development plugin asset directory. It verifies real inference and catalog language metadata without recording a microphone.
