# Gemini for the portable host

Portable .NET 10 package `com.typewhisper.gemini` version `1.3.10`, requiring host `1.1.5`.

## Features

- Recorded-audio transcription through resumable Files uploads and Interactions, with best-effort deletion after success, errors or cancellation and `store: false`.
- Smart/Verbatim mode, ordered language hints, macOS ISO-to-BCP-47 defaults, dictionary terms in both recorded audio and live setup.
- Live WebSocket setup, PCM16 audio, fragmented text/binary JSON frames and transcript events. Manual activity boundaries keep a dictation turn open through pauses; finalization retains both the final transcript and full-audio boundary acknowledgement before the host uses it. Unexpected earlier final segments or boundaries invalidate the stream. Provider errors, malformed frames, premature closure and cancellation fail the stream so the host can fall back to the recorded audio.
- Text processing, selected default model, explicit workflow overrides, provider-default/custom temperature (0–2).
- Native paginated model discovery, separate transcription/text catalogs and host-rendered English/German settings. Refresh explicitly saves the catalog using the stored key; opening settings makes no requests. API keys remain in the host secret store.
- Mac response variants (`output_text`, `steps`, `outputs`) are accepted; incomplete interactions remain errors. Repeated pagination tokens and empty refreshes preserve the saved catalog.
- Existing Gemini logo reused by the WinUI host.

The portable provider retains the Windows Flash Lite default and explicit Smart/Verbatim setting. Unlike macOS, it uploads WAV without an AAC compression dependency and does not implement per-request temperature directives (the current Windows LLM contract has no such argument). Streaming uses [Google's documented manual activity detection](https://ai.google.dev/gemini-api/docs/live-api/live-transcribe#manual-vad-push-to-talk) and advertises the host's streaming-completion contract.

## Protocol boundaries

Persisted live-model IDs must name the selected batch model's exact live-transcription sibling. Duplicate activity-end acknowledgements and premature final segments invalidate the stream. A stalled upload metadata response is a timeout unless the caller canceled.

Uploads use unique caller-provided [Files API resource names](https://ai.google.dev/api/files#File), allowing cleanup after missing or malformed metadata without deleting unrelated resources. The host receives ISO language choices mapped to documented BCP-47 defaults from Google's [supported language table](https://ai.google.dev/gemini-api/docs/transcribe#supported-languages). Non-object chat choices or messages produce a structured empty-response failure.

## Verification

```powershell
dotnet test plugins/TypeWhisper.Plugin.Gemini/Tests/TypeWhisper.Plugin.Gemini.Portable.Tests.csproj -c Release
dotnet test tests/TypeWhisper.PluginSDK.Portable.Tests/TypeWhisper.PluginSDK.Portable.Tests.csproj -c Release
& F:/typewhisper/typewhisper-dev-tools/build-typewhisper-windows-dev.ps1 --run <checkout-path>
```

Tests cover protocol fixtures, settings, persistence failures, cancellation, temporary-file cleanup, response variants, local WebSocket exchanges and package installation/configuration/restart/uninstall/reinstall. Validate microphone and workflow behavior, native settings and ARM64 execution separately.

Provider references: [transcription](https://ai.google.dev/gemini-api/docs/transcribe), [OpenAI compatibility](https://ai.google.dev/gemini-api/docs/openai).

Dictionary terms use the structured host contract 1.1.5 or newer, preserving literal commas inside each entry. Older plugin packages keep their original prompt contract.
