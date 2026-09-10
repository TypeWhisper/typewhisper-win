# OpenAI / ChatGPT for Windows 1.1

Independent portable implementation of `com.typewhisper.openai`. The protocol implementation and tests originate from `plugins/TypeWhisper.Plugin.OpenAi` and the OpenAI helpers in `src/TypeWhisper.PluginSDK` at Windows commit `f0899bd09e12b0ad9869c3358f8c2b67e11b26e6`. Settings, transcription context and reasoning compatibility also follow the macOS `TypeWhisperPluginSDK/Plugins/OpenAIPlugin/OpenAIPlugin.swift` implementation (blob `689e3545448fbce070cdb0bf6f131a320d7c3aba`). The legacy plugin and its profile remain separate.

## Capabilities and settings

- API-key transcription, including compressed uploads with a narrow WAV fallback, language hints, dictionary terms and translation with Whisper 1.
- Realtime transcription waits for the committed item's final result. Disconnections, provider errors and incomplete completion fail the operation rather than accepting a partial result.
- API or ChatGPT text processing, account-specific model discovery, compatible reasoning effort and temperature controls. Browser login uses PKCE; importing a local Codex login requires an explicit action. Credentials use the host's protected storage.
- Cloud speech synthesis with 13 voices, instructions and request-specific voice/output-device selection. The WinUI spoken-feedback consumer holds the plugin lease through playback and cancellation. Selecting a cloud voice is explicit.
- Connection method and credentials appear first, followed by the transcription model selectbox and context, speech settings, then text model selection and generation settings. Transcription model selection activates the chosen dictation model. Local providers retain their download and removal controls.

Dictation and speech require an OpenAI API key even when ChatGPT login is selected for text processing. This package requires host contract `1.1.2`; that contract revision is separate from the application's release version. The macOS buffered preview for file-based GPT Transcribe is not implemented here; live preview uses the realtime models.

## Validation status

- Release headless validation passed: 2,385 tests across core, plugin host, CLI, presentation and discovered portable plugin suites, including 66 OpenAI tests.
- Follow-up provider validation passed 73 tests after live acceptance fixes: translation requests omit the source-language field, recognized media-probe failures retain the WAV retry, GPT-6 Astra uses Responses without unsupported temperature parameters, and non-chat live/instruct models are excluded from text discovery.
- An additional 120 legacy OpenAI/TTS regression tests passed in Debug. The Release legacy test build was blocked before execution by the existing WhisperCpp ARM64 MSVC-runtime packaging requirement on this machine.
- The portable tests cover fake HTTP/WebSocket requests, completion and cancellation, protected-session persistence, classified errors, TTS playback selection, settings ordering and German labels. Isolated package tests cover install, configuration, execution, restart and uninstall/reinstall; a same-version replacement is correctly rejected.
- The development package and WinUI build were installed locally. Native inspection confirmed the connection controls precede the transcription selectbox, and text models use a selectbox as well.
- Real provider acceptance is recorded in [the live-test notes](../../docs/releases/1.1-openai-validation.md). An OpenAI package version-to-version update and side-by-side installed-generation acceptance remain open. No package or catalog publication is implied by this development staging.

Run the provider tests with `dotnet test plugins-v2/TypeWhisper.Plugin.OpenAi/Tests/TypeWhisper.Plugin.OpenAi.Portable.Tests.csproj -c Release`. `portable.proj` supplies the standard package build/copy contract; package the staged plugin directory, excluding host SDK assemblies and credentials.
