# ElevenLabs for Windows 1.1

Portable transcription plugin `com.typewhisper.elevenlabs`.

## Capabilities

- Scribe v2 recorded-audio transcription with automatic or explicit language, word timestamps, clean-transcript mode, audio events, speaker-count hints and bounded dictionary keyterms.
- Scribe v2 Realtime with PCM16 mono 16 kHz input, partial text, language detection and acknowledged manual commits. After 20 seconds, commits wait for 200 ms of near-silence. Unbroken speech falls back to the complete recording before the provider's automatic commit boundary rather than splitting a word. Finalization waits for the timestamped final event, including when the last audio chunk already emptied the send buffer.
- A failed connection or missing acknowledgement fails the streaming operation, allowing the host to retry the complete recording. Plain and timestamped versions of a final event are not both appended. Repeated confirmed provider segments are appended without text-based deduplication.
- Automatic mode uses recorded-audio transcription when dictionary terms, audio events or speaker-count settings require the batch path. Speaker labels are not exposed by the host; speaker count is sent as a provider hint.
- API-key storage through the host secret store. When `user_read` is disabled, configuration checks report that speech access could not be verified; the saved key can still be tested with a dictation. A missing profile permission does not prove Speech-to-Text access.
- Host-rendered settings with English and German labels. This package requires portable host contract `1.1.5` for structured dictionary terms. Older hosts reject it before loading; existing plugin packages remain compatible. The contract revision is separate from the application release version.

Dictionary keyterms are limited to 1,000 entries, fewer than 50 characters and at most five words per term. ElevenLabs charges an additional 20% for keyterms; more than 100 terms also impose a minimum billable duration of 20 seconds per request. See the [provider reference](https://elevenlabs.io/docs/api-reference/speech-to-text/convert).

## Build and validation

`dotnet test plugins/TypeWhisper.Plugin.ElevenLabs/Tests/TypeWhisper.Plugin.ElevenLabs.Portable.Tests.csproj -c Release`

The build stages package contents under `bin/Release/portable-host/Plugins/com.typewhisper.elevenlabs`. Package that folder's contents, excluding the SDK and credentials. `portable.proj` supplies the standard build/copy contract. Plugin CI and headless-test discovery include this project.

The suite covers request fields, audio preservation, ISO language metadata, timings, persistence, restricted keys, classified errors, cancellation, keyterm limits, isolated package load/install/restart/uninstall/reinstall, fragmented live responses, repeated final text and interrupted finalization. Validate recognition quality and native microphone delivery separately; repeated audio can produce shortened provider transcripts.

Protocol references:

- [Recorded-audio API](https://elevenlabs.io/docs/api-reference/speech-to-text/convert)
- [Realtime commit strategies](https://elevenlabs.io/docs/eleven-api/guides/how-to/speech-to-text/realtime/transcripts-and-commit-strategies)
- [Realtime events](https://elevenlabs.io/docs/eleven-api/guides/how-to/speech-to-text/realtime/event-reference)

Building or staging a package does not publish it.

Dictionary terms use the structured host contract 1.1.5 or newer, preserving literal commas inside each entry. Older plugin packages keep their original prompt contract.
