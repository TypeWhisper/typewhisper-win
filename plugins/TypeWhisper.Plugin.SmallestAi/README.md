# Smallest AI portable plugin

Pulse batch and realtime transcription, plus Lightning and Lightning Pro text-to-speech using the same API key.

Version `1.3.0`; plugin ID `com.typewhisper.smallest-ai`; minimum host `1.1.4`.

## Setup

Enter the API key in Smallest AI settings and use the shared Save settings button. Select Pulse under Dictation. Live transcription includes the final text after stopping.

Use Test connection or Refresh voices to fetch the current Standard and Pro catalogs. The catalog is cached for subsequent app starts. Choose a voice under Audio → Spoken feedback and click Test voice. Voice labels include the model pool and recommended language. Speech speed uses the shared save button; the selected audio output and Stop speaking are honored.

Connection validation retrieves the voice catalogs without uploading audio. A failed refresh retains the previous catalog. With an API key, activation reloads the catalogs in the background once the cached copy is a day old or has no date, and a key change reloads a stale copy with the new key. The catalogs are Smallest AI's public Lightning voices, the same for every account, so a key change keeps them. A failed, empty or incomplete reload keeps the cached catalog; Test connection and Refresh voices reset the day.

A saved voice that the catalog no longer lists stays saved and applies again once the catalog offers it. The plugin's default voice is Magnus; without Magnus, the first Standard voice. A background reload that returns one pool without voices keeps the cached catalog, so a voice saved in that pool keeps working.

Pulse batch requests expose HTTP status, retry delay and failure kind to the host.
Hosts with request resilience can retry transient failures and split uploads rejected
with HTTP 413; authentication failures and caller cancellation are not retried.

## Build and verification

```powershell
dotnet msbuild plugins/TypeWhisper.Plugin.SmallestAi/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins/TypeWhisper.Plugin.SmallestAi/Tests -c Release
```

Package `bin/Release/portable-host/Plugins/com.typewhisper.smallest-ai` as the ZIP root. The plugin uses the framework-independent SDK; Windows audio playback uses NAudio/WASAPI.

Tests cover transcription and host retry/chunk integration, WebSocket finalization, package lifecycle, voice catalog parsing and persistence, model/voice pairing, language and speed, output-device forwarding, cancellation, invalid audio, and failed catalog refreshes.

## API references

- [Voice catalogs](https://docs.smallest.ai/models/api-reference/text-to-speech/get-waves-voices)
- [Speech synthesis](https://docs.smallest.ai/models/api-reference/text-to-speech/synthesize-speech)

Standard and Pro voice identifiers are paired with their respective model pools. Requests use binary WAV output and `Accept: audio/wav`. Maximum speech text length is 8,000 characters per request; returned audio is limited to 12 MiB and two minutes before playback.

The single host language selector advertises the recorded/live intersection. East Asian live transcription uses the provider's US endpoint. Streaming events forward the detected language, and recorded results accept both root-level and metadata duration fields.
