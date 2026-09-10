# ElevenLabs for Windows 1.1

Independent portable implementation of `com.typewhisper.elevenlabs`, based on the existing Windows provider and the macOS ElevenLabs plugin. The legacy project, settings, secrets and published package remain separate.

## Capabilities

- Scribe v2 recorded-audio transcription with automatic or explicit language, word timestamps, clean-transcript mode, audio events, speaker-count hints and bounded dictionary keyterms.
- Scribe v2 Realtime with PCM16 mono 16 kHz input, partial text, language detection and acknowledged manual commits. After 20 seconds, commits wait for 200 ms of near-silence. Unbroken speech falls back to the complete recording before the provider's automatic commit boundary rather than splitting a word. Finalization waits for the timestamped final event, including when the last audio chunk already emptied the send buffer.
- A failed connection or missing acknowledgement fails the streaming operation, allowing the host to retry the complete recording. Plain and timestamped versions of a final event are not both appended. Repeated confirmed provider segments are appended without text-based deduplication.
- Automatic mode uses recorded-audio transcription when dictionary terms, audio events or speaker-count settings require the batch path. Speaker labels are not exposed by the host; speaker count is sent as a provider hint.
- API-key storage through the host secret store. Configuration checks accept the specific `user_read` restriction for a Speech-to-Text-only key, without treating other permission errors as success.
- Host-rendered selection fields for settings, with English and German labels. This package requires the accompanying host/SDK choice-setting support; do not publish it for older 1.1 Daily hosts without that support.

Dictionary keyterms are limited to 1,000 entries, fewer than 50 characters and at most five words per term. ElevenLabs charges an additional 20% for keyterms; more than 100 terms also impose a minimum billable duration of 20 seconds per request. See the [provider reference](https://elevenlabs.io/docs/api-reference/speech-to-text/convert).

## Build and validation

`dotnet test plugins-v2/TypeWhisper.Plugin.ElevenLabs/Tests/TypeWhisper.Plugin.ElevenLabs.Portable.Tests.csproj -c Release`

The build stages package contents under `bin/Release/portable-host/Plugins/com.typewhisper.elevenlabs`. Package that folder's contents, excluding the SDK and credentials. `portable.proj` supplies the standard build/copy contract. Plugin CI and headless-test discovery include both plugin roots.

The suite covers request fields, audio preservation, ISO language metadata, timings, persistence, restricted keys, classified errors, cancellation, keyterm limits, isolated package load/install/restart/uninstall/reinstall, fragmented live responses, repeated final text and interrupted finalization. Live English and German file transcription and a 3.2-second German WebSocket recording passed. The German transcript matched exactly; live finalization took 268 ms in that run. A repeated-audio stress test exposed a mid-word periodic commit; the pause-aware correction has separate regression coverage. After the correction, a varied 27.43-second English recording completed as two confirmed segments and matched the batch transcript exactly (338 ms finalization). A real batch request with dictionary terms, audio-event tagging and a two-speaker hint also succeeded. Replaying eight identical German audio clips produced shortened provider transcripts even with cleanup disabled; that synthetic repetition case is not claimed as accurate recognition. The native settings page rendered the expected choices. These observations are separate from the fake-transport tests.

Protocol references:

- [Recorded-audio API](https://elevenlabs.io/docs/api-reference/speech-to-text/convert)
- [Realtime commit strategies](https://elevenlabs.io/docs/eleven-api/guides/how-to/speech-to-text/realtime/transcripts-and-commit-strategies)
- [Realtime events](https://elevenlabs.io/docs/eleven-api/guides/how-to/speech-to-text/realtime/event-reference)

No package or catalog is published by building or staging this development port.
