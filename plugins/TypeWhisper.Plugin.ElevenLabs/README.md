# ElevenLabs for Windows 1.3

Portable transcription plugin `com.typewhisper.elevenlabs`.

## Capabilities

- Scribe v3 (default) and Scribe v2 recorded-audio transcription with automatic or explicit language, clean-transcript mode, audio events, speaker-count hints and bounded dictionary keyterms.
- Scribe v2 Medical for clinical audio, with Scribe v2's request shape, price and keyterms. It is batch-only, so selecting it turns off live text. Scribe v3 costs the same as Scribe v2 and is the default for new setups; a saved Scribe v2 selection stays. Saved selections of retired models such as Scribe v1 move to Scribe v3.
- Timed segments built from Scribe's word timestamps: a segment ends at a sentence end once it has at least three words, at a pause longer than one second, or after 30 seconds. Tagged audio events stay in the segment text. SRT/VTT export and the local HTTP API use these segments.
- Detected languages are reported with two-letter codes where one exists; the batch API answers in ISO 639-3 (`deu` → `de`, `cmn` → `zh`).
- The language list follows ElevenLabs' documented codes. `jw`, `haw` and `vo` are rejected by the API and not offered; Javanese is `jv`.
- Scribe v3 Live for Scribe v3 and Scribe v2 Realtime for Scribe v2, both on the same realtime endpoint with PCM16 mono 16 kHz input, partial text, language detection and acknowledged manual commits. After 20 seconds, commits wait for 200 ms of near-silence. Unbroken speech falls back to the complete recording before the provider's automatic commit boundary rather than splitting a word. Finalization waits for the timestamped final event, including when the last audio chunk already emptied the send buffer.
- Mixed languages (off by default, shown only for Scribe v3) sends `multilang_mode=true` to Scribe v3 Live for dictations that switch languages within a sentence. Scribe v2 Realtime rejects the parameter, so it never reaches that model. Final text from the complete recording, such as with dictionary terms, does not use it, because the batch API has no such switch. `commit_strategy=turn_prediction` is not used: the plugin commits the session itself when the dictation ends.
- A failed connection, missing acknowledgement or realtime error fails the streaming operation, allowing the host to retry the complete recording. All documented realtime error types count, including those without "error" in their name such as `invalid_request` and `quota_exceeded`, so the retry starts at once. Plain and timestamped versions of a final event are not both appended. Repeated confirmed provider segments are appended without text-based deduplication.
- Automatic mode uses recorded-audio transcription when Scribe v2 Medical, audio events or speaker-count settings require the batch path. With dictionary terms it reports `SupportsStreamingPreviewForPrompt`: the live session runs without keyterms and only shows live text, because realtime keyterms often wrap the whole transcript in quotation marks; the host then transcribes the complete recording with the keyterms for the final text. Hosts without this member keep the post-recording path for such dictations. Speaker labels are not exposed by the host; speaker count is sent as a provider hint.
- API-key storage through the host secret store. When `user_read` is disabled, configuration checks report that speech access could not be verified; the saved key can still be tested with a dictation. A missing profile permission does not prove Speech-to-Text access.
- ElevenLabs answers HTTP 401 for a wrong key, a used-up credit quota of the key and a missing permission. The plugin names these cases with fixed messages and never passes the provider's text on.
- Host-rendered settings with English and German labels. This package requires portable host contract `1.1.5` for structured dictionary terms. Older hosts reject it before loading; existing plugin packages remain compatible. The contract revision is separate from the application release version.

Dictionary keyterms are limited to 1,000 entries, fewer than 50 characters and at most five words per term. ElevenLabs charges an additional 20% for keyterms; more than 100 terms also impose a minimum billable duration of 20 seconds per request. See the [provider reference](https://elevenlabs.io/docs/api-reference/speech-to-text/convert).

## Build and validation

`dotnet test plugins/TypeWhisper.Plugin.ElevenLabs/Tests/TypeWhisper.Plugin.ElevenLabs.Portable.Tests.csproj -c Release`

The build stages package contents under `bin/Release/portable-host/Plugins/com.typewhisper.elevenlabs`. Package that folder's contents, excluding the SDK and credentials. `portable.proj` supplies the standard build/copy contract. Plugin CI and headless-test discovery include this project.

The suite covers request fields, audio preservation, ISO language metadata, sentence and pause segments, the Medical model, persistence, restricted keys, classified errors, cancellation, keyterm limits, isolated package load/install/restart/uninstall/reinstall, fragmented live responses, repeated final text and interrupted finalization. Validate recognition quality and native microphone delivery separately; repeated audio can produce shortened provider transcripts.

Protocol references:

- [Recorded-audio API](https://elevenlabs.io/docs/api-reference/speech-to-text/convert)
- [Realtime commit strategies](https://elevenlabs.io/docs/eleven-api/guides/how-to/speech-to-text/realtime/transcripts-and-commit-strategies)
- [Realtime events](https://elevenlabs.io/docs/eleven-api/guides/how-to/speech-to-text/realtime/event-reference)

Building or staging a package does not publish it.

Dictionary terms use the structured host contract 1.1.5 or newer, preserving literal commas inside each entry. Older plugin packages keep their original prompt contract.
