# Smallest AI portable plugin

Pulse batch and realtime transcription, plus Lightning and Lightning Pro text-to-speech using the same API key.

Version `1.2.1`; plugin ID `com.typewhisper.smallest-ai`; minimum host `1.1.4`.

## Setup

Enter the API key in Smallest AI settings and use the shared Save settings button. Select Pulse under Dictation. Live transcription includes the final text after stopping.

Use Test connection or Refresh voices to fetch the current Standard and Pro catalogs. The catalog is cached for subsequent app starts. Choose a voice under Audio → Spoken feedback and click Test voice. Voice labels include the model pool and recommended language. Speech speed uses the shared save button; the selected audio output and Stop speaking are honored.

Connection validation retrieves the voice catalogs without uploading audio. A failed refresh retains the previous catalog. Plugin activation does not make network requests.

## Build and verification

```powershell
dotnet msbuild plugins/TypeWhisper.Plugin.SmallestAi/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins/TypeWhisper.Plugin.SmallestAi/Tests -c Release
```

Package `bin/Release/portable-host/Plugins/com.typewhisper.smallest-ai` as the ZIP root. The plugin uses the framework-independent SDK without WPF; Windows audio playback uses NAudio/WASAPI.

39 tests cover transcription, WebSocket finalization, package lifecycle, voice catalog parsing and persistence, model/voice pairing, language and speed, output-device forwarding, cancellation, invalid audio, and failed catalog refreshes.

Authenticated tests retrieved 483 voices and exercised German synthesis, playback, and stopping. The user confirmed dictation, live transcription, and the in-app Test voice action using Ben from the Lightning Pro German catalog. The development package update preserves the API key and unrelated receipts. In-app acceptance is complete; public package publication is pending.

## API references

- [Voice catalogs](https://docs.smallest.ai/models/api-reference/text-to-speech/get-waves-voices)
- [Speech synthesis](https://docs.smallest.ai/models/api-reference/text-to-speech/synthesize-speech)

Standard and Pro voice identifiers are paired with their respective model pools. Requests use binary WAV output and `Accept: audio/wav`. Maximum speech text length is 8,000 characters per request; returned audio is limited to 12 MiB and two minutes before playback.

The single host language selector advertises the recorded/live intersection. East Asian live transcription uses the provider's US endpoint. Streaming events forward the detected language, and recorded results accept both root-level and metadata duration fields.
