# OpenRouter for the portable host

Portable .NET 10 implementation of `com.typewhisper.openrouter`, version `1.1.3`, requiring host `1.1.2` or later.

The plugin provides text processing through `/api/v1/chat/completions` and recorded-audio transcription through `/api/v1/audio/transcriptions`. Audio is sent as base64 WAV in JSON. Translation, streaming and dictionary prompts are not advertised. Following the macOS provider, requests to OpenAI, Groq and Together models request verbose JSON with segment timestamps. Returned valid segments are preserved; unavailable timestamps are not synthesized.

Host-rendered settings provide encrypted API-key storage, connection validation, explicit model refreshes, a default text model, provider-default or custom temperature, text model pricing, and an API-key budget check. The budget action reports the key's `limit_remaining`, not the account balance. Opening settings makes no network calls. Failed model refreshes retain the saved catalog. Workflow model overrides take precedence over the plugin's default model.

Text requests preserve Unicode, use the shared bounded output budget, classify HTTP failures, propagate cancellation and reject truncated or empty answers. Typed content arrays return only visible text; reasoning content is never substituted for the answer. Credentials use the host's secret store, and a failed key write retains the previous configuration.

## Verification

```powershell
dotnet test plugins/TypeWhisper.Plugin.OpenRouter/Tests/TypeWhisper.Plugin.OpenRouter.Portable.Tests.csproj -c Release
dotnet test tests/TypeWhisper.PluginSDK.Portable.Tests/TypeWhisper.PluginSDK.Portable.Tests.csproj -c Release
dotnet msbuild plugins/TypeWhisper.Plugin.OpenRouter/portable.proj '-t:Build;CopyPackage' -p:Configuration=Release -p:PluginDestination=<staging-directory>
```

The plugin suite covers requests, catalogs, model selection, temperature, secrets, errors, cancellation, typed text responses, timestamps, and isolated installation/restart/uninstall/reinstall with both provider roles. Package checks verify the dependency manifest, absence of bundled host SDK or UI-framework assemblies and minimum host version. Test API responses and keys are fixtures.

The standard package output is `bin/Release/portable-host/Plugins/com.typewhisper.openrouter/`: plugin DLL, dependency manifest and plugin manifest. Existing CI discovery includes this project and its tests automatically.

## Language and settings behavior

Known OpenAI transcription model IDs expose a conservative 57-language set. Other models retain automatic detection until their language capabilities are established.

The host renders one full-width configuration form and a fixed **Save settings** button. API-key edits, model choices, refreshed catalogs and temperature are committed together. Connection and budget checks can use an entered key without saving it. Failed writes leave the active configuration and encrypted key intact. Regression checks cover grouped commits, key rollback, invalid input, draft catalog refresh and persisted selection repair.

Validate ARM64 execution, long recordings, German native layouts, upstream model families and installed 1.0/1.1 coexistence separately.

## Provider references

- [Speech-to-text endpoint](https://openrouter.ai/docs/guides/overview/multimodal/stt)
- [Current API key and remaining key budget](https://openrouter.ai/docs/api/api-reference/api-keys/get-current-api-key)
- [macOS OpenRouter implementation](https://github.com/TypeWhisper/typewhisper-mac/blob/main/TypeWhisperPluginSDK/Plugins/OpenRouterPlugin/OpenRouterPlugin.swift)
