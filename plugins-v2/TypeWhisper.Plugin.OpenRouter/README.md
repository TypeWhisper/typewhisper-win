# OpenRouter for the portable host

Independent .NET 10 implementation of `com.typewhisper.openrouter`, version `1.1.2`, requiring host `1.1.2` or later. The protocol was ported from the Windows provider at `ce38c355` and compared with the macOS OpenRouter provider on September 14, 2026. The package has no WPF dependency and does not read or migrate legacy profiles.

The plugin provides text processing through `/api/v1/chat/completions` and recorded-audio transcription through `/api/v1/audio/transcriptions`. Audio is sent as base64 WAV in JSON. Translation, streaming and dictionary prompts are not advertised. Following the macOS provider, requests to OpenAI, Groq and Together models request verbose JSON with segment timestamps. Returned valid segments are preserved; unavailable timestamps are not synthesized.

Host-rendered settings provide encrypted API-key storage, connection validation, explicit model refreshes, a default text model, provider-default or custom temperature, text model pricing, and an API-key budget check. The budget action reports the key's `limit_remaining`, not the account balance. Opening settings makes no network calls. Failed model refreshes retain the saved catalog. Workflow model overrides take precedence over the plugin's default model.

Text requests preserve Unicode, use the shared bounded output budget, classify HTTP failures, propagate cancellation and reject truncated or empty answers. Typed content arrays return only visible text; reasoning content is never substituted for the answer. Credentials use the host's secret store, and a failed key write retains the previous configuration.

## Verification

```powershell
dotnet test plugins-v2/TypeWhisper.Plugin.OpenRouter/Tests/TypeWhisper.Plugin.OpenRouter.Portable.Tests.csproj -c Release
dotnet test tests/TypeWhisper.PluginSDK.Portable.Tests/TypeWhisper.PluginSDK.Portable.Tests.csproj -c Release
dotnet msbuild plugins-v2/TypeWhisper.Plugin.OpenRouter/portable.proj '-t:Build;CopyPackage' -p:Configuration=Release -p:PluginDestination=<staging-directory>
```

All 61 plugin tests and 259 portable SDK/host tests passed in Release. The shared settings change also passed all 96 OpenAI Compatible tests. The plugin suite covers requests, catalogs, model selection, temperature, secrets, errors, cancellation, typed text responses, timestamps, and isolated installation/restart/uninstall/reinstall with both provider roles. Package checks verify the dependency manifest, absence of bundled host SDK/WPF assemblies and minimum host version. Test API responses and keys are fixtures.

The standard package output is `bin/Release/portable-host/Plugins/com.typewhisper.openrouter/`: plugin DLL, dependency manifest and plugin manifest. Existing CI discovery includes the new project and its tests automatically.

## Development acceptance

The required Windows development helper built and launched this worktree. The package was installed into the development profile, with the preceding package index backed up outside that profile. Computer Use verified the OpenRouter settings page and key entry. The native refresh actions loaded 21 transcription models and 430 text models on September 14, 2026. Connection validation and the key-budget action succeeded.

Authenticated checks through the installed package translated a short German sentence with `openai/gpt-4o-mini` and transcribed synthetic English speech with `openai/whisper-large-v3-turbo`. The full running-app HTTP path returned the exact English sentence with `language=en`, provider segments and valid SRT output. The previously selected NVIDIA model was restored after request-scoped overrides. No microphone-to-paste check is claimed.

That host check initially rejected explicit English because the port lacked language metadata. Version `1.1.1` adds a conservative 57-language set for the six known OpenAI transcription model IDs. Other models retain automatic detection until their language capabilities are established. The regression covers model changes, metadata and restart.

A real development package update from `1.1.0` to `1.1.1` was staged in the immutable package store while the host was running. The old version remained active with a pending receipt; the prescribed helper restarted the host, which promoted the new package without warnings. The encrypted key, selected transcription model, 430 text models and 21 transcription models survived. Live text processing and app-level transcription succeeded again after the update.

ARM64 hardware execution, long recordings, the German native layout, other upstream model families and side-by-side installed-generation acceptance remain pending. No public plugin catalog or release was modified.

Version `1.1.2` uses the same host-rendered editor as OpenAI Compatible, with a single full-width configuration and one fixed **Save settings** button. API-key edits, model choices, refreshed catalogs and temperature are committed together; connection and budget checks can use an entered key without saving it. A failed settings write leaves the active configuration and encrypted key intact. The native sidebar uses the existing OpenRouter brand asset in both themes.

Computer Use verified the logo, unsaved-change indicator, disabled/enabled Save button, conditional custom-temperature field and successful page-level save. The original provider-default temperature mode and selected models were retained after the test. Automated regression checks cover the grouped commit, encrypted-key rollback, invalid input and draft catalog refresh.

## Provider references

- [Speech-to-text endpoint](https://openrouter.ai/docs/guides/overview/multimodal/stt)
- [Current API key and remaining key budget](https://openrouter.ai/docs/api/api-reference/api-keys/get-current-api-key)
- [macOS OpenRouter implementation](https://github.com/TypeWhisper/typewhisper-mac/blob/main/TypeWhisperPluginSDK/Plugins/OpenRouterPlugin/OpenRouterPlugin.swift)
