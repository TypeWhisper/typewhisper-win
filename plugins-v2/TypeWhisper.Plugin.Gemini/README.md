# Gemini for the portable host

Independent .NET 10 package `com.typewhisper.gemini` version `1.3.1`, requiring host `1.1.2`. Source and protocol fixtures were ported from Windows `4db8f6ac` with cross-platform behavior checked. Legacy sources, profiles and catalogs are unchanged.

## Features and macOS comparison

- Recorded-audio transcription through resumable Files uploads and Interactions, with best-effort deletion after success, errors or cancellation and `store: false`.
- Smart/Verbatim mode, ordered language hints, macOS ISO-to-BCP-47 defaults, dictionary terms in both recorded audio and live setup.
- Live WebSocket setup, PCM16 audio, fragmented text/binary JSON frames and transcript events. Manual activity boundaries keep a dictation turn open through pauses; finalization awaits the confirmed transcript before the host uses it. Provider errors, malformed frames, premature closure and cancellation fail the stream so the host can fall back to the recorded audio.
- Text processing, selected default model, explicit workflow overrides, provider-default/custom temperature (0–2).
- Native paginated model discovery, separate transcription/text catalogs and host-rendered English/German settings. Refresh explicitly saves the catalog using the stored key; opening settings makes no requests. API keys remain in the host secret store.
- Mac response variants (`output_text`, `steps`, `outputs`) are accepted; incomplete interactions remain errors. Repeated pagination tokens and empty refreshes preserve the saved catalog.
- Existing Gemini logo reused by the WinUI host.

The portable provider retains the Windows Flash Lite default and explicit Smart/Verbatim setting. Unlike macOS, it uploads WAV without an AAC compression dependency and does not implement per-request temperature directives (the current Windows LLM contract has no such argument). No legacy settings are imported. Streaming uses [Google's documented manual activity detection](https://ai.google.dev/gemini-api/docs/live-api/live-transcribe#manual-vad-push-to-talk) and advertises the host's streaming-completion contract.

## Verification

```powershell
dotnet test plugins-v2/TypeWhisper.Plugin.Gemini/Tests/TypeWhisper.Plugin.Gemini.Portable.Tests.csproj -c Release
dotnet test tests/TypeWhisper.PluginSDK.Portable.Tests/TypeWhisper.PluginSDK.Portable.Tests.csproj -c Release
& "$DevTools/build-typewhisper-windows-dev.ps1" --run --winui $Checkout
```

86 plugin tests passed, including six streaming completion/error/cancellation cases. The shared portable SDK/host suite also passed (264 tests with the companion common-save change). Coverage includes protocol fixtures, new settings, persistence failures, cancellation, temporary-file cleanup, Mac response variants, real local WebSocket exchanges and ZIP installation/configuration/restart/uninstall/reinstall through the immutable package store. The package contains its DLL, dependency manifest and plugin manifest, without WPF dependencies. WinUI build and development launch succeeded with existing unrelated warnings.

On 2026-09-18, the ZIP was installed in the Windows development profile and loaded with the real portable host services and Windows secret-store implementation. Settings were read successfully and the plugin was enabled. Existing unrelated package receipts were preserved. The WinUI development build and launch succeeded. No authenticated provider requests were sent. Native visual inspection was unavailable because the computer-use service could not connect.

Authenticated model discovery, text completion, recorded-audio transcription and WebSocket transcription passed on 2026-09-18 using synthetic English audio and a user-configured key. Version 1.3.1 additionally passed live transcription through the actual `StreamingDictation` host pipeline without a recorded-audio fallback, both staged and after the development-profile upgrade from 1.3.0. See [live acceptance](LIVE-ACCEPTANCE.md) for evidence and scope. Physical microphone/workflow acceptance, native settings inspection, German audio and ARM64 execution remain pending. No public release or catalog was changed.

Provider references: [transcription](https://ai.google.dev/gemini-api/docs/transcribe), [OpenAI compatibility](https://ai.google.dev/gemini-api/docs/openai).
