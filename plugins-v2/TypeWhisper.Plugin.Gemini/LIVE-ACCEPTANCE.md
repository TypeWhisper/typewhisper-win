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

## Streaming completion update (1.3.1)

The development package was upgraded from 1.3.0 to 1.3.1 through the immutable package store; its pending update was applied on restart. Unrelated package receipts and the saved key were preserved.

Version 1.3.1 uses manual activity detection, with one activity start and end per recording. Finalization waits for the final input-transcription event and fails on interrupted or malformed streams. It advertises `SupportsStreamingCompletion`.

Both the staged package and the installed upgraded package passed a live test through the host's actual `StreamingDictation` pipeline. The synthetic sentence produced 11 preview updates and the complete final text, without a recorded-audio fallback or an artificial delay after finalization. Six additional loopback cases cover delayed final confirmation, empty final text, premature closure, provider errors, malformed JSON and cancellation. All 86 plugin tests pass.

Protocol reference: [Google Live Transcription manual VAD](https://ai.google.dev/gemini-api/docs/live-api/live-transcribe#manual-vad-push-to-talk).

## Scope and remaining acceptance

These checks establish live behavior of the installed provider through its portable SDK interfaces. The user subsequently confirmed live transcription in the development app. The independent custom-editor insertion fix was also manually confirmed. Full workflow and ARM64 acceptance remain outside this provider smoke test.

The original 1.3.0 direct WebSocket check explicitly waited for events after sending end-of-audio and did not establish host streaming support. That limitation is addressed and separately tested in 1.3.1 as described above.

ARM64 acceptance remains pending. The user approved the tested provider for PR review; public package publication remains separate.

The local machine's untracked evidence is in `artifacts/plugin-batch/gemini-live/`: `results.json`, the harness source, the synthetic WAV and its source sentence. These artifacts contain no credential values.

## Review hardening (1.3.2)

95 tests pass after correcting unverified detected-language metadata, preserving successfully fetched empty transcription catalogs, tolerating malformed persisted/provider model IDs, and bounding and classifying connection-check failures. An additional loopback case verifies that an early finalized transcript segment does not prematurely complete the recording.

## Review hardening (1.3.4)

105 tests cover capability-filtered model discovery, text-specific HTTP 413 failures, independent transcription/text availability, upload cleanup after cancellation, and streaming completion in either acknowledgement/transcript order. A premature segment final or earlier audio boundary now invalidates the stream instead of being reused as the last recording segment. The host falls back to the complete recording for these unexpected events.

A live protocol inspection with two synthetic utterances separated by eight seconds of silence produced one final transcription only after the manual activity end, followed by the full submitted audio offset. This confirms the observed single-activity behavior for the tested model; it is not a general ordering guarantee for all Live API event types.
