# Soniox portable plugin

Soniox live transcription over WebSocket, plus recorded-audio transcription with upload, Windows audio compression, polling, cleanup and region selection.

Version `1.3.3`; plugin ID `com.typewhisper.soniox`; minimum host `1.1.2`.

## Setup

Enter the API key, select the account region and choose Soniox in the transcription workflow. Enable Live transcription under Appearance for text during dictation. The existing model ID stays `default`; live capture uses `stt-rt-v5`, while recorded-audio requests and the host fallback retain `stt-async-v5`.

## Platform scope

Windows supports regional realtime transcription with final/non-final token handling. Translation and TTS are not included.

## Build and verification

From the repository root:

```powershell
dotnet msbuild plugins/TypeWhisper.Plugin.Soniox/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins/TypeWhisper.Plugin.Soniox/Tests -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.soniox` inside the plugin project. Package that directory as the ZIP root.

Tests cover streaming protocol, host preview/final assembly, fragmented responses, region configuration, interim replacement, cancellation, timeout and typed failures. Batch tests cover upload/poll/delete, metadata/language/region contracts, audio encoding and package lifecycle.

The host provides draft editing and one Save settings button. Unexpected close, incomplete tokens, cancellation or missing completion cannot succeed with a partial transcript.

## Live transcription

The stream sends an empty **text** frame to end audio, matching Soniox's reference clients.

Confirmed subword tokens are concatenated until an endpoint or completed session, so the host does not insert spaces inside words. Interim tokens replace the current preview. Protocol sentinel tokens are excluded. The endpoint uses the selected US/EU/Japan region; the stream never silently changes regions.

References: [WebSocket API](https://soniox.com/docs/api-reference/stt/websocket-api), [real-time transcription](https://soniox.com/docs/stt/rt/real-time-transcription), [models](https://soniox.com/docs/stt/models), [regional endpoints](https://soniox.com/docs/data-residency).

Outstanding batch cleanup tasks drain before deactivation/disposal. Untimed tokens are skipped in subtitle timing. Native Media Foundation encoding tests require Windows with a 16 kHz mono AAC encoder; other tests run on both CI platforms.

The native-codec test skips missing Media Foundation installations, including Windows N without its Media Feature Pack. Regression tests cover absent libraries, missing encoders and unexpected probe failures.

If Media Foundation DLLs or entry points are missing, including wrapped initialization failures, batch transcription falls back to the original WAV. Protocol fixtures cover that upload path.
