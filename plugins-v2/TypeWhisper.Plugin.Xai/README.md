# xAI / Grok portable plugin

xAI batch/realtime transcription, Responses LLM completion and TTS with voice/output-device selection.

Version `1.3.2`; plugin ID `com.typewhisper.xai`; minimum host `1.1.5`.
Independent branch: `seofood/xai-portable`, integrated with the current WinUI-only main.

## Setup

Enter the API key, refresh available models/voices as needed and select xAI in transcription, LLM or speech settings.

This package uses host-rendered portable settings and an independent WinUI data directory. Legacy settings, credentials and model files are not imported automatically.

## Source and platform scope

Protocol/runtime sources and applicable fixtures were snapshotted from `plugins/TypeWhisper.Plugin.Xai` at `4db8f6ac`, then adapted under `plugins-v2`. The package does not reference the legacy provider DLL or compile WPF settings views. Legacy sources, catalogs and published packages remain unchanged.

The macOS repository was compared at `ac00e39ea63e4789de8427d034d2085b3d898159`; it was not modified. Compared with the macOS provider, the Windows package retains the existing cloud protocols and replaces legacy playback with WASAPI. Streaming completion waits for transcript.done and reports one final result.

## Build and verification

From this branch's repository root:

```powershell
dotnet msbuild plugins-v2/TypeWhisper.Plugin.Xai/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins-v2/TypeWhisper.Plugin.Xai/Tests/TypeWhisper.Plugin.Xai.Portable.Tests.csproj -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.xai` inside the plugin project. Package that directory as the ZIP root.

30 plugin tests cover the current API contracts. Fake HTTP, voice/model contracts, real local WebSocket finalization/premature close and package lifecycle. An authenticated catalog request returned HTTP 403 because the configured team has no credits or licenses. At the user's request, paid/live audio, text and playback acceptance is deferred until credits are available. All packages have isolated install, enable, restart, disable, uninstall and reinstall coverage through the real portable package loader and host services.

The ZIP was installed and loaded in the WinUI development profile, preserving existing installation receipts. No credentials were copied from the legacy profile.

The shared portable SDK/host suite passed 259 tests on the Live Transcript host branch. Automated fixture tests do not replace authenticated provider, native model/device, microphone or visual UI acceptance. Public catalog publication and production-profile migration are pending.

## Current settings and protocol

The host provides one Save settings action for the API key and edited preferences. Text models and voices use catalog selections; refreshing a catalog preserves a still-available selection. The optional custom voice ID overrides the selected default voice. xAI branding appears in navigation and provider selections with light/dark variants.

Batch and WebSocket requests explicitly select Grok Voice Transcribe 2.0 or 1.0. The former `grok-stt` selection maps to 2.0. Both transports pass individual dictionary terms (including commas) with a 100-term/50-character budget. Streaming waits for `transcript.created` before releasing captured audio, stitches utterance chunks without duplicates, and waits for `transcript.done` on stop. Premature closure fails explicitly. The language parameter controls xAI text formatting; it does not force recognition to that language.

Protocol references: [Speech to Text](https://docs.x.ai/developers/model-capabilities/audio/speech-to-text) and [Text to Speech](https://docs.x.ai/developers/model-capabilities/audio/text-to-speech), checked September 22, 2026.

Native WinUI build and launch passed. The settings page, xAI icon and shared Save settings flow were checked in the running development app; normalization changes persisted only after Save and were restored to their original value. Current screenshots: [settings](../../docs/screenshots/xai/settings.png) and [speech settings](../../docs/screenshots/xai/speech-settings.png).
