# Zoom Scribe for the portable host

Portable .NET 11 implementation of `com.typewhisper.zoom`, version `1.0.0`, requiring host `1.3.0` or later. It follows the macOS plugin of the same version ([TypeWhisper/typewhisper-mac#1978](https://github.com/TypeWhisper/typewhisper-mac/pull/1978)). Users sign in with an API key from Zoom AI Services; Zoom bills usage in Build Credits.

In TypeWhisper's benchmark ([TypeWhisper/typewhisper-benchmark#29](https://github.com/TypeWhisper/typewhisper-benchmark/pull/29)) Scribe had the lowest error rate of all models in Spanish, French and Italian, and Scribe English Pro was level with the best English model.

## Features

| Capability | Portable implementation |
| --- | --- |
| Models | **Scribe Multilingual** (default, `zoom-scribe-multilingual`): English, German, Spanish, French, Italian, Portuguese, Japanese, Korean, Chinese and Arabic; without a dictation language it detects the language (`language_detection: continuous`). It accepts dictionary terms but applies none of them (a live session reports `phrase_count: 0`), so the plugin reports dictionary terms as unsupported for it and sends none. **Scribe Single Language** sends `zoom-scribe-de`, `-en`, `-es`, `-fr`, `-it`, `-pt`, `-ja`, `-zh` or `-ar` for the dictation language, with dictionary terms; Korean, other languages and dictations without a language go to Scribe Multilingual. **Scribe English Pro** (`zoom-scribe-en-pro`), Zoom's premium English model, with dictionary terms; other languages go to Scribe Multilingual. Unknown explicit selections are rejected. |
| Transcription mode | **Live** (default): dictations stream to the Live mode. **After recording**: every recording goes to the Fast mode when it stops, with no live preview from Zoom. |
| Recorded audio | Recordings, files, watch folders, the HTTP API and dictations without a live session use the Fast mode (`POST https://api.zoom.us/v2/aiservices/scribe/transcribe`) with the WAV inline as a base64 data URL and the request's `config` (`model`, `language` as a locale such as `de-DE`, `vocabulary.phrases`). The Fast mode takes at most 2 MB of request body, so the plugin declares 45 seconds of 16 kHz PCM16 WAV as its upload limit and the host splits longer audio at quiet points; a body over 2 MB fails as too large, and the host splits that piece again. macOS sends three-minute M4A pieces instead; the port has no M4A encoder. `result.text_display` is the text, the segments' `start` and `end` become segments for subtitles (a segment without times joins the one before it), and `result.language` or the segments' language is the detected language. |
| Live text | `wss://api.zoom.us/v2/aiservices/scribe/live` with the subprotocol `live-asr` and the key as a bearer token. The session waits for `session.created`, sends one `session.update` with the request's `config`, `interim_results: true` and `audio.format: pcm16`, and waits for `session.updated` before any audio, so a wrong key or a model the account cannot use fails before the dictation streams. Audio goes out in 100 ms binary PCM16 frames. Each `transcription.delta` (the whole partial text of an item) and `transcription.completed` updates a preview of the whole transcript. At the stop the plugin sends `session.close`; once `session.closed` arrives, the whole text is one final event. The host joins final events with spaces, and Zoom writes Chinese and Japanese without spaces between items, so the plugin joins the items itself, without a space between Chinese or Japanese characters, as on macOS. |
| Dictionary | Up to 100 terms of up to 100 characters as `vocabulary.phrases`, in both modes, so dictations stream with their terms. Structured dictionary prompts keep literal commas inside each term. |
| Settings | Encrypted key storage and removal, connection check, model and transcription mode; English/German copy. |

The connection check sends a Fast request for a model that does not exist and no audio. Zoom answers 400 for a valid key and 401 for an invalid one, so the check transcribes nothing and costs no credits. Opening settings performs no requests. All configuration fields and the encrypted-key reference commit together; failed writes keep the preceding configuration. Credentials and settings remain after uninstall.

Errors keep Zoom's identifier `reason` or `code` (such as `UNSUPPORTED_MODEL`) and leave out Zoom's free-text message, so no request content reaches logs. 402 is reported as a quota failure: the account may be out of Build Credits. A live session that fails, closes, or does not finish within 10 seconds of the stop fails without a final text, and the host transcribes the complete recording instead. A non-fatal live `error` is ignored. A cancelled dictation still sends `session.close` when the connection is open and no other send is running, so Zoom stops transcribing it. Translation is not advertised.

## Verification

Settings screenshots are in [docs/screenshots/zoom](../../docs/screenshots/zoom/): the default (Scribe Multilingual, Live) in the dark theme and Scribe Single Language with After recording in the light theme, from a build of this host with the 1.0.0 package and no key, in the Windows test VM.

```powershell
dotnet test plugins/TypeWhisper.Plugin.Zoom/Tests/TypeWhisper.Plugin.Zoom.Portable.Tests.csproj -c Release
dotnet msbuild plugins/TypeWhisper.Plugin.Zoom/portable.proj '-t:Build;CopyPackage' -p:Configuration=Release -p:PluginDestination=<staging-directory>
```

Fake HTTP and WebSocket tests cover the model catalog, model and locale routing per language, dictionary budgets, the Fast request and response, segment and language fallbacks, error kinds without Zoom's message, the upload limit, the connection check, settings commits and their failures, the live session's settings, frames, previews, single final event and language, silence, rejected settings, timeouts, sessions that end early, cancellation and joining Chinese and Japanese items. The package test covers ZIP contents, installation, configuration, restart, duplicate-install rejection, uninstall and reinstall with retained credentials and settings.

## Protocol references

- [Zoom Scribe](https://developers.zoom.us/docs/ai-services/scribe/)
- [Live mode](https://developers.zoom.us/docs/ai-services/scribe/live-mode/)
