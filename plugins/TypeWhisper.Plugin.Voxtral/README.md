# Mistral for the portable host

Portable .NET 10 package `com.typewhisper.voxtral`, version `1.3.3`, requiring host `1.1.2`. The visible provider is **Mistral**. Its existing package and provider IDs remain unchanged so encrypted credentials, enabled state and dictation selections survive the rename.

## Behavior

The plugin uses one Mistral API key for Voxtral audio transcription and Mistral chat completions. **Refresh models** reads `/v1/models`, classifies models by their advertised capabilities and persists the account catalog. Batch transcription models and text completion models have separate selectors. Archived models, embeddings, OCR and speech generation are not offered for these endpoints. Realtime audio models use the WebSocket transport instead of the file endpoint. Model listing is not a guarantee of quota or inference permission for every model.

Text processing supports the selected default or an explicit workflow model, provider-default or custom temperature, complete-response validation, and reasoning responses containing text chunks. Thinking chunks are excluded from the returned text. Batch Voxtral supports automatic language detection and explicit hints for the 13 documented languages. Voxtral Realtime detects language automatically; the provider does not expose a language-hint parameter for live sessions.

Settings use the host's single **Save settings** button. The key stays in encrypted host storage. Opening settings performs no network request. A failed model fetch or settings write retains the previous catalog. Changing accounts resets the old catalog, and a concurrent refresh cannot publish models fetched using a replaced key. Redirects are disabled; HTTP status, retry information and cancellation remain typed.

Mac `VoxtralPlugin` is a local MLX engine; `MistralAIPlugin` is the comparable cloud provider. This package is cloud-based, without local downloads, translation or dictionary biasing. Voxtral Realtime sends PCM16 mono 16 kHz audio through the documented WebSocket protocol. It accumulates text deltas for live previews, then waits for the authoritative `transcription.done` result after flushing and ending audio. Interruptions, malformed responses and missing completion fail rather than returning partial text. Realtime models also accept completed host WAV recordings through the same socket, including when live preview is disabled. Live finalization keeps the host's 15-second tail deadline and full-recording fallback. Completed WAV requests allow the audio duration plus 15 seconds for finalization, since their audio is submitted faster than realtime.

## Verification

```powershell
dotnet test plugins/TypeWhisper.Plugin.Voxtral/Tests/TypeWhisper.Plugin.Voxtral.Portable.Tests.csproj -c Release
```

Tests cover capability discovery, filtering/deduplication, restart persistence, failed writes, account changes during refresh, empty account catalogs, shared-key chat requests, optional temperature, reasoning chunks, incomplete replies, independent ZIP lifecycle tests, WebSocket handshake, ordered PCM chunks, fragmented Unicode, complete finalization, typed failures, bounded messages, cancellation, the real host transcript collector, duration-aware completed-WAV finalization, and concurrent key/model writes without UI-context capture.

Validate text workflows and ARM64 execution separately. Account model discovery does not establish successful inference for every listed model. See [settings screenshots](../../docs/screenshots/mistral/).

References: [models](https://docs.mistral.ai/api/endpoint/models), [chat](https://docs.mistral.ai/api/endpoint/chat), [transcription](https://docs.mistral.ai/studio/audio/speech_to_text).

Realtime protocol reference: [official Mistral Python SDK](https://github.com/mistralai/client-python/tree/main/src/mistralai/extra/realtime).
