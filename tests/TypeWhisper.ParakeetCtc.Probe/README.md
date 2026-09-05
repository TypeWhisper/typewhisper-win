# CTC functional audio probe

Run with the separately downloaded official model and its bundled `test_wavs/1.wav` (mono 16-kHz PCM16):

```powershell
dotnet run --project tests/TypeWhisper.ParakeetCtc.Probe -- <model-directory> <test_wavs/1.wav> [published-plugin-directory]
```

The fixture says `I love you.`. The probe checks real acoustic emissions and deliberately supplies the wrong transcript `I live you.`: the hint `love` must be accepted. Conversely, a `live` hint against the correct transcript must be rejected. The similarity threshold is 0.7 specifically for this short control. With the optional third argument, the published package is activated through the real portable loader and must pass the positive control too.

Timings cover the entire fixture, not actual TDT word/token timings. This does not validate real microphone input, German vocabulary, UI interaction or performance. No user audio, history or clipboard is accessed.
