# Soniox portable plugin

Soniox live transcription over WebSocket, recorded-audio transcription with upload, Windows audio compression, polling, cleanup and region selection, translation to English, and text-to-speech.

Version `1.4.0`; plugin ID `com.typewhisper.soniox`; minimum host `1.1.5`.

## Setup

Enter the API key, select the account region and choose Soniox in the transcription workflow. Enable Live transcription under Appearance for text during dictation. The host model ID stays `default`, so existing workflow selections keep working; the Soniox model is a plugin setting.

## Models

**Model** defaults to *Automatic*: live text uses the newest real-time model and recordings the newest async model from Soniox's model list (`GET /v1/models`). A version such as `v5` pins both modes to that generation (`stt-rt-v5`, `stt-async-v5`). Aliases are not offered, and a preview model never becomes automatic.

The list is cached per region. It loads when the key passes **Check connection**, through **Refresh models**, and in the background when Soniox is used without a list for the region or with one older than 24 hours; a failed background load waits ten minutes before the next try. Until a list exists, Automatic uses `stt-rt-v6` and `stt-async-v6`. Soniox retires v5 on 2026-12-15 and routes it to v6.

v6 writes prices closer to how they are spoken ("578.000 Euro" instead of "578.000 €") and adds hyphens to codes only when they are spoken.

## Context and dictionary

Active dictionary terms arrive as a structured list and go to Soniox as `context.terms`; **Transcription context** (up to 6,000 characters) goes as `context.text`, for live and recorded audio alike. Soniox accepts about 10,000 characters of context, so the dictionary budget is what the context text leaves.

## Translation

Translate to English uses Soniox's one-way translation for recorded audio. Soniox keeps the spoken words in the transcript's `text` and returns the English text as translation tokens; the plugin returns those tokens and times each translated chunk by the spoken tokens before it. With language identification Soniox marks speech it leaves untranslated, such as English passages in mixed speech, and those passages stay in the result. Speech that is entirely English has no translation tokens and keeps its transcript. The host transcribes translation tasks from the complete recording, never over the live stream.

## Long recordings

`MaximumAudioUploadBytes` is 270 minutes of 16 kHz PCM16. The host splits longer recordings into parts of that length, because Soniox rejects files over 300 minutes.

## Speech

Soniox text-to-speech (`tts-rt-v2` until the model list names a newer one) plays through the output device and voice the host selects. **Speech model** and **Default speech voice** live in the Speech section; voices come from `GET /v1/tts-models`. Soniox requires a language, so a request without one uses the app language and then English. Generated audio is capped at two minutes per request.

## Platform scope

Windows supports regional realtime transcription with final/non-final token handling. Speaker labels are not included.

## Build and verification

From the repository root:

```powershell
dotnet msbuild plugins/TypeWhisper.Plugin.Soniox/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins/TypeWhisper.Plugin.Soniox/Tests -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.soniox` inside the plugin project. Package that directory as the ZIP root.

Tests cover streaming protocol, host preview/final assembly, fragmented responses, region configuration, interim replacement, cancellation, timeout and typed failures. Batch tests cover upload/poll/delete, metadata/language/region contracts, audio encoding and package lifecycle. Feature tests cover the model list, automatic and pinned models, settings, context and dictionary terms, translation, background refresh and speech.

The host provides draft editing and one Save settings button. Unexpected close, incomplete tokens, cancellation or missing completion cannot succeed with a partial transcript.

## Live transcription

The stream sends an empty **text** frame to end audio, matching Soniox's reference clients.

Confirmed subword tokens are concatenated until an endpoint or completed session, so the host does not insert spaces inside words. Interim tokens replace the current preview. Protocol sentinel tokens are excluded. The endpoint uses the selected US/EU/Japan region; the stream never silently changes regions.

References: [WebSocket API](https://soniox.com/docs/api-reference/stt/websocket-api), [real-time transcription](https://soniox.com/docs/stt/rt/real-time-transcription), [models](https://soniox.com/docs/stt/models), [context](https://soniox.com/docs/stt/concepts/context), [text-to-speech](https://soniox.com/docs/tts/rest-api/generate-speech), [regional endpoints](https://soniox.com/docs/data-residency).

Outstanding batch cleanup and model list tasks drain before deactivation/disposal. Untimed tokens are skipped in subtitle timing. Native Media Foundation encoding tests require Windows with a 16 kHz mono AAC encoder; other tests run on both CI platforms.

The native-codec test skips missing Media Foundation installations, including Windows N without its Media Feature Pack. Regression tests cover absent libraries, missing encoders and unexpected probe failures.

If Media Foundation DLLs or entry points are missing, including wrapped initialization failures, batch transcription falls back to the original WAV. Protocol fixtures cover that upload path.
