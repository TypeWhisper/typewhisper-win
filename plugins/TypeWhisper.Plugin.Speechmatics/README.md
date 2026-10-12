# Speechmatics for the portable host

Portable .NET 10 package `com.typewhisper.speechmatics`, version `1.2.6`, requiring host `1.1.5`.

## Behavior

Enhanced and Standard support uploaded recordings and live PCM16 mono 16 kHz transcription. Batch jobs use bounded polling and cancellation. Live WebSocket sessions replace provisional text, accumulate confirmed fragments, and wait for `EndOfTranscript` after sending the exact audio sequence count. Disconnects, malformed responses and unfinished previews fail instead of reporting partial success. Dictionary terms are sent in both modes.

Europe is the default: batch uses `eu1.asr.api.speechmatics.com`, live uses `eu.rt.speechmatics.com`. The explicit US option selects `us1.asr.api.speechmatics.com` and `us.rt.speechmatics.com`. Requests stay in the selected region; there is no automatic regional failover. Redirects are disabled.

Spoken languages are exposed to the host. Automatic language identification is available for batch recordings. Live transcription requires a fixed language: an explicit host language takes precedence; otherwise the saved **Live transcription language** is used (English initially). This fallback is explained in the settings. Unsupported live languages fail before connecting.

The shared host Save button persists the API key and edited fields. Keys use the Windows encrypted secret store through a staged reference; failed saves preserve the active configuration. Settings pages make no network request merely by opening. Credentials and preferences are retained across package upgrades. Light/dark provider logos appear in navigation and provider selection.

## macOS comparison

Compared with `TypeWhisperPluginSDK/Plugins/SpeechmaticsPlugin`: regional processing, Standard/Enhanced selection and custom vocabulary are preserved. Windows uses the host's live-capture session contract and an explicit fallback-language setting; the macOS implementation falls back to batch for automatic language identification. The Windows dictionary budget remains 100 terms / 4,000 characters.

## Verification

```powershell
dotnet test plugins/TypeWhisper.Plugin.Speechmatics/Tests/TypeWhisper.Plugin.Speechmatics.Portable.Tests.csproj -c Release
dotnet msbuild plugins/TypeWhisper.Plugin.Speechmatics/portable.proj '-t:Build;CopyPackage' -p:Configuration=Release -p:PluginDestination=<staging-directory>
```

Tests cover HTTP protocols/errors, cancellation, encrypted-key persistence and failed saves, standalone immutable package lifecycle, regional WebSockets, explicit/fallback languages, vocabulary, fragmented/revised transcripts, punctuation boundaries, binary audio ordering, exact stop sequence counts, completion timeouts and real host accumulation.

Settings saves avoid capturing the UI synchronization context. Batch language comes from recognized words, with transcript configuration as fallback. Batch assembly respects JSON-v2 punctuation attachment and word delimiters, including contractions and scripts without spaces. Explicit language identifiers are normalized before batch and live requests.

US endpoint access and ARM64 execution need separate live acceptance. Settings screenshots are in [docs/screenshots/speechmatics](../../docs/screenshots/speechmatics/).

References: [regions and authentication](https://docs.speechmatics.com/get-started/authentication), [realtime protocol](https://docs.speechmatics.com/api-ref/realtime-transcription-websocket), [supported languages](https://docs.speechmatics.com/speech-to-text/languages).

Protocol references: [current model selection](https://docs.speechmatics.com/speech-to-text/models#operating-points), [automatic language output](https://docs.speechmatics.com/speech-to-text/batch/language-identification#transcription-result).

Dictionary terms use the structured host contract 1.1.5 or newer, preserving literal commas inside each entry. Older plugin packages keep their original prompt contract.
