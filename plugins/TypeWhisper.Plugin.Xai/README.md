# xAI / Grok portable plugin

xAI batch/realtime transcription, Responses LLM completion and TTS with voice/output-device selection.

Version `1.3.3`; plugin ID `com.typewhisper.xai`; minimum host `1.1.5`.

## Setup

Enter the API key, refresh available models/voices as needed and select xAI in transcription, LLM or speech settings.

## Platform scope

Windows speech playback uses WASAPI. Streaming completion waits for `transcript.done` and reports one final result.

## Build and verification

From the repository root:

```powershell
dotnet msbuild plugins/TypeWhisper.Plugin.Xai/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins/TypeWhisper.Plugin.Xai/Tests/TypeWhisper.Plugin.Xai.Portable.Tests.csproj -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.xai` inside the plugin project. Package that directory as the ZIP root.

Tests cover HTTP requests, voice/model contracts, local WebSocket finalization and premature close, and package lifecycle. Live audio, text and playback acceptance require an account with available credits and remain deferred.

## Current settings and protocol

The host provides one Save settings action for the API key and edited preferences. Text models and voices use catalog selections; refreshing a catalog preserves a still-available selection. The optional custom voice ID overrides the selected default voice. xAI branding appears in navigation and provider selections with light/dark variants.

Batch and WebSocket requests explicitly select Grok Voice Transcribe 2.0 or 1.0. The former `grok-stt` selection maps to 2.0. Both transports pass individual dictionary terms (including commas) with a 100-term/50-character budget. Streaming waits for `transcript.created` before releasing captured audio, stitches utterance chunks without duplicates, and waits for `transcript.done` on stop. Premature closure fails explicitly. The language parameter controls xAI text formatting; it does not force recognition to that language.

Protocol references: [Speech to Text](https://docs.x.ai/developers/model-capabilities/audio/speech-to-text) and [Text to Speech](https://docs.x.ai/developers/model-capabilities/audio/text-to-speech).

Settings screenshots: [general](../../docs/screenshots/xai/settings.png) and [speech](../../docs/screenshots/xai/speech-settings.png).
