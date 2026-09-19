# Mistral for the portable host

Independent .NET 10 package `com.typewhisper.voxtral`, version `1.2.0`, requiring host `1.1.2`. The visible provider is **Mistral**. Its existing package and provider IDs remain unchanged so encrypted credentials, enabled state and dictation selections survive the rename. Developed on `seofood/voxtral-portable`; no other migration branch is required.

## Behavior and macOS comparison

The plugin uses one Mistral API key for Voxtral audio transcription and Mistral chat completions. **Refresh models** reads `/v1/models`, classifies models by their advertised capabilities and persists the account catalog. Batch transcription models and text completion models have separate selectors. Archived models, embeddings, OCR, speech generation and realtime-only models are not offered for these endpoints. Model listing is not a guarantee of quota or inference permission for every model.

Text processing supports the selected default or an explicit workflow model, provider-default or custom temperature, complete-response validation, and reasoning responses containing text chunks. Thinking chunks are excluded from the returned text. Voxtral supports automatic language detection and explicit hints for the 13 documented languages.

Settings use the host's single **Save settings** button. The key stays in encrypted host storage. Opening settings performs no network request. A failed model fetch or settings write retains the previous catalog. Changing accounts resets the old catalog, and a concurrent refresh cannot publish models fetched using a replaced key. Redirects are disabled; HTTP status, retry information and cancellation remain typed.

The Mac sources were compared at `ac00e39e` in `TypeWhisperPluginSDK/Plugins/`. Mac `VoxtralPlugin` is a local MLX engine; `MistralAIPlugin` is the comparable cloud provider. This package is cloud-based, without local downloads, translation, dictionary biasing or live transcription. Mistral offers a separate realtime API, but this version implements uploaded-audio transcription and text processing.

## Verification

```powershell
dotnet test plugins-v2/TypeWhisper.Plugin.Voxtral/Tests/TypeWhisper.Plugin.Voxtral.Portable.Tests.csproj -c Release
```

All **32 tests passed**, including capability discovery, filtering/deduplication, restart persistence, failed writes, account changes during refresh, empty account catalogs, shared-key chat requests, optional temperature, reasoning chunks, incomplete replies and independent ZIP lifecycle tests.

On September 19, 2026, real API checks passed for credentials, model discovery (2 batch transcription IDs and 27 text completion IDs), English synthetic WAV transcription and text correction using `ministral-8b-latest`. `mistral-small-latest` returned HTTP 429 during two attempts; its inference was not accepted as verified. No claim is made that every listed model was individually tested. Marco confirmed normal microphone dictation in the running development app on the same day. Manual text-workflow acceptance, ARM64 and realtime implementation remain pending. No public package or catalog was published.

References: [models](https://docs.mistral.ai/api/endpoint/models), [chat](https://docs.mistral.ai/api/endpoint/chat), [transcription](https://docs.mistral.ai/studio/audio/speech_to_text).
