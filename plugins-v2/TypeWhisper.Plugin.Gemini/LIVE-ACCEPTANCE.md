# Gemini live provider acceptance

Date: 2026-09-18. Tested the installed portable package `com.typewhisper.gemini` version `1.3.0`, built from implementation commit `9e13aa20` on `seofood/gemini-portable`.

The user explicitly authorized authenticated live tests after entering the key in the development app. The harness loaded the installed package through `PortablePluginPackage`, read credentials through the actual Windows DPAPI secret store and used an isolated copy of plugin settings. No key was logged and the app's selected models were not changed.

| Check | Result |
| --- | --- |
| Credential validation against the native model endpoint | Passed |
| Model refresh through the portable settings action | Passed; 19 text model entries and `gemini-3.5-transcribe` available |
| Text completion with `gemini-flash-lite-latest` | Passed; returned exactly `GEMINI_LIVE_OK` |
| Recorded WAV transcription with `gemini-3.5-transcribe` | Passed; complete expected sentence returned in approximately 4.8 seconds |
| Direct provider WebSocket transcription | Passed; incremental updates and a final event contained the complete expected sentence |

The audio was generated locally with Windows speech synthesis as PCM16 mono at 16 kHz. It contained only this synthetic English sentence: “This is a short test. Tomorrow we will meet at ten in the office.” The recorded and streaming results preserved that meaning, normalizing “ten” to “10”. No microphone recording or personal transcription history was uploaded.

## Scope and remaining acceptance

These checks establish live behavior of the installed provider through its portable SDK interfaces. They do not establish native UI interaction, German audio recognition, physical microphone capture, workflow post-processing or insertion into another app.

The WebSocket check explicitly waited for events after sending end-of-audio. `FinalizeAsync` currently sends the end signal without awaiting confirmed completion, and `SupportsStreamingCompletion` remains false. Consequently, the WinUI host does not consume this provider's stream as the final transcription; it uses the recorded-audio path. The successful direct WebSocket test does not claim host streaming-completion support.

Package-update and ARM64 acceptance remain pending. Push, PR creation and review monitoring require Marco's per-plugin approval. No publication has occurred.

The local machine's untracked evidence is in `artifacts/plugin-batch/gemini-live/`: `results.json`, the harness source, the synthetic WAV and its source sentence. These artifacts contain no credential values.
