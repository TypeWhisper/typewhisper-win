# Gemini for the portable host

Independent .NET 10 package `com.typewhisper.gemini` version `1.3.0`, requiring host `1.1.2`. Source and protocol fixtures were ported from Windows `4db8f6ac` and compared with macOS `ac00e39e` (`TypeWhisperPluginSDK/Plugins/GeminiPlugin/GeminiPlugin.swift`). Legacy sources, profiles and catalogs are unchanged.

## Features and macOS comparison

- Recorded-audio transcription through resumable Files uploads and Interactions, with best-effort deletion after success, errors or cancellation and `store: false`.
- Smart/Verbatim mode, ordered language hints, macOS ISO-to-BCP-47 defaults, dictionary terms in both recorded audio and live setup.
- Live WebSocket setup, PCM16 audio, fragmented text/binary JSON frames, transcript events and end-of-audio signaling.
- Text processing, selected default model, explicit workflow overrides, provider-default/custom temperature (0–2).
- Native paginated model discovery, separate transcription/text catalogs and host-rendered English/German settings. Refresh explicitly saves the catalog using the stored key; opening settings makes no requests. API keys remain in the host secret store.
- Mac response variants (`output_text`, `steps`, `outputs`) are accepted; incomplete interactions remain errors. Repeated pagination tokens and empty refreshes preserve the saved catalog.
- Existing Gemini logo reused by the WinUI host.

The portable provider retains the Windows Flash Lite default and explicit Smart/Verbatim setting. Unlike macOS, it uploads WAV without an AAC compression dependency and does not implement per-request temperature directives (the current Windows LLM contract has no such argument). No legacy settings are imported. Real-time transcription retains the existing event/finalization contract; provider-side timing requires live acceptance.

## Verification

```powershell
dotnet test plugins-v2/TypeWhisper.Plugin.Gemini/Tests/TypeWhisper.Plugin.Gemini.Portable.Tests.csproj -c Release
dotnet test tests/TypeWhisper.PluginSDK.Portable.Tests/TypeWhisper.PluginSDK.Portable.Tests.csproj -c Release
F:/typewhisper/typewhisper-dev-tools/build-typewhisper-windows-dev.ps1 --run --winui F:/typewhisper/typewhisper-win
```

80 plugin tests and 259 shared portable SDK/host tests passed. Coverage includes protocol fixtures, new settings, persistence failures, cancellation, temporary-file cleanup, Mac response variants, a real local WebSocket exchange and ZIP installation/configuration/restart/uninstall/reinstall through the immutable package store. The package contains its DLL, dependency manifest and plugin manifest, without WPF dependencies. WinUI build and development launch succeeded with existing unrelated warnings.

Authenticated API calls, physical microphone/workflow acceptance, native settings inspection, package-update acceptance and ARM64 execution remain pending. Marco will enter the API key and perform live acceptance later. No public release or catalog was changed.

Provider references: [transcription](https://ai.google.dev/gemini-api/docs/transcribe), [OpenAI compatibility](https://ai.google.dev/gemini-api/docs/openai).
