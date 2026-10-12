# OpenAI / ChatGPT for Windows 1.1

Portable transcription, text-processing and speech plugin `com.typewhisper.openai`.

## Capabilities and settings

- API-key transcription, including compressed uploads with a narrow WAV fallback, language hints, dictionary terms and translation with Whisper 1.
- Realtime transcription waits for the committed item's final result. Disconnections, provider errors and incomplete completion fail the operation rather than accepting a partial result.
- API or ChatGPT text processing, account-specific model discovery, compatible reasoning effort and temperature controls. Browser login uses PKCE; importing a local Codex login requires an explicit action. Credentials use the host's protected storage.
- Cloud speech synthesis with 13 voices, instructions and request-specific voice/output-device selection. The WinUI spoken-feedback consumer holds the plugin lease through playback and cancellation. Selecting a cloud voice is explicit.
- Connection method and credentials appear first, followed by the transcription model selectbox and context, speech settings, then text model selection and generation settings. Transcription model selection activates the chosen dictation model. Local providers retain their download and removal controls.

Dictation and speech require an OpenAI API key even when ChatGPT login is selected for text processing. This package requires host contract `1.1.5`; that contract revision is separate from the application's release version. The macOS buffered preview for file-based GPT Transcribe is not implemented here; live preview uses the realtime models.

## Validation

Portable tests cover HTTP/WebSocket requests, completion and cancellation, protected-session persistence, classified errors, speech output selection, settings ordering and German labels. Package tests cover install, configuration, execution, restart and uninstall/reinstall; same-version replacements are rejected. Validate actual package updates and installed 1.0/1.1 coexistence separately.

Translation requests omit the source-language field. Recognized media-probe failures retain the WAV retry. GPT-6 Astra uses Responses without unsupported temperature parameters; non-chat live/instruct models are excluded from text discovery.

Run the provider tests with `dotnet test plugins/TypeWhisper.Plugin.OpenAi/Tests/TypeWhisper.Plugin.OpenAi.Portable.Tests.csproj -c Release`. `portable.proj` supplies the standard package build/copy contract; package the staged plugin directory, excluding host SDK assemblies and credentials.

Dictionary terms use the structured host contract 1.1.5 or newer, preserving literal commas inside each entry. Older plugin packages keep their original prompt contract.
