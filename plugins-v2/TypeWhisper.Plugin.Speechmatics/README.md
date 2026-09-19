# Speechmatics for the portable host

Independent .NET 10 package `com.typewhisper.speechmatics`, version `1.2.1`, requiring host `1.1.2`. The separate `seofood/speechmatics-portable` branch includes the host logo assets. No other migration branch is required.

## Behavior

Enhanced and Standard support uploaded recordings and live PCM16 mono 16 kHz transcription. Batch jobs use bounded polling and cancellation. Live WebSocket sessions replace provisional text, accumulate confirmed fragments, and wait for `EndOfTranscript` after sending the exact audio sequence count. Disconnects, malformed responses and unfinished previews fail instead of reporting partial success. Dictionary terms are sent in both modes.

Europe is the default: batch uses `eu1.asr.api.speechmatics.com`, live uses `eu.rt.speechmatics.com`. The explicit US option selects `us1.asr.api.speechmatics.com` and `us.rt.speechmatics.com`. Requests stay in the selected region; there is no automatic regional failover. Redirects are disabled.

Spoken languages are exposed to the host. Automatic language identification is available for batch recordings. Live transcription requires a fixed language: an explicit host language takes precedence; otherwise the saved **Live transcription language** is used (English initially). This fallback is explained in the settings. Unsupported live languages fail before connecting.

The shared host Save button persists the API key and edited fields. Keys use the Windows encrypted secret store through a staged reference; failed saves preserve the active configuration. Settings pages make no network request merely by opening. Credentials and preferences are retained across package upgrades. Light/dark provider logos appear in navigation and provider selection.

## macOS comparison

Compared with `TypeWhisperPluginSDK/Plugins/SpeechmaticsPlugin`: regional processing, Standard/Enhanced selection and custom vocabulary are preserved. Windows uses the host's live-capture session contract and an explicit fallback-language setting; the macOS implementation falls back to batch for automatic language identification. The Windows dictionary budget remains 100 terms / 4,000 characters. macOS sources are not modified.

## Verification

```powershell
dotnet test plugins-v2/TypeWhisper.Plugin.Speechmatics/Tests/TypeWhisper.Plugin.Speechmatics.Portable.Tests.csproj -c Release
dotnet msbuild plugins-v2/TypeWhisper.Plugin.Speechmatics/portable.proj '-t:Build;CopyPackage' -p:Configuration=Release -p:PluginDestination=<staging-directory>
```

All **40 tests passed**, covering HTTP protocols/errors, cancellation, encrypted-key persistence and failed saves, standalone immutable package lifecycle, regional WebSockets, explicit/fallback languages, vocabulary, fragmented/revised transcripts, punctuation boundaries, binary audio ordering, exact stop sequence counts, completion timeouts and real host accumulation.

Authenticated tests on September 19, 2026 passed against Europe: connection validation, Enhanced batch transcription and paced live audio through the actual portable host. Live previews appeared during capture (first preview about 0.7 seconds), and the complete final sentence arrived after stopping without batch fallback. The 1.2.0 immutable ZIP was installed in the development profile with credentials and unrelated package receipts preserved. The development WinUI build and launch succeeded.

The 1.2.1 review fix prevents settings saves from capturing the UI synchronization context under contention. A regression test reproduces the previous behavior and verifies the correction. Installed/UI acceptance remains on 1.2.0.

Native UI verification passed: provider logos, Europe, Enhanced, German, live text enabled and the shared Save button. The installed immutable package also passed paced live transcription through the real host. Screenshots are in `docs/screenshots/speechmatics/`. Marco confirmed successful live microphone transcription in the development app on September 19, 2026, with Europe and German selected. US endpoint access and ARM64 execution are not live-tested. No public package/catalog release has been performed.

References: [regions and authentication](https://docs.speechmatics.com/get-started/authentication), [realtime protocol](https://docs.speechmatics.com/api-ref/realtime-transcription-websocket), [supported languages](https://docs.speechmatics.com/speech-to-text/languages).
