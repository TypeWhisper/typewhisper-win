# Mistral for the portable host

Independent .NET 10 package `com.typewhisper.voxtral`, version `1.3.0`, requiring host `1.1.2`. The visible provider is **Mistral**. Its existing package and provider IDs remain unchanged so encrypted credentials, enabled state and dictation selections survive the rename. Developed on `seofood/voxtral-portable`; no other migration branch is required.

## Behavior and macOS comparison

The plugin uses one Mistral API key for Voxtral audio transcription and Mistral chat completions. **Refresh models** reads `/v1/models`, classifies models by their advertised capabilities and persists the account catalog. Batch transcription models and text completion models have separate selectors. Archived models, embeddings, OCR and speech generation are not offered for these endpoints. Realtime audio models use the WebSocket transport instead of the file endpoint. Model listing is not a guarantee of quota or inference permission for every model.

Text processing supports the selected default or an explicit workflow model, provider-default or custom temperature, complete-response validation, and reasoning responses containing text chunks. Thinking chunks are excluded from the returned text. Batch Voxtral supports automatic language detection and explicit hints for the 13 documented languages. Voxtral Realtime detects language automatically; the provider does not expose a language-hint parameter for live sessions.

Settings use the host's single **Save settings** button. The key stays in encrypted host storage. Opening settings performs no network request. A failed model fetch or settings write retains the previous catalog. Changing accounts resets the old catalog, and a concurrent refresh cannot publish models fetched using a replaced key. Redirects are disabled; HTTP status, retry information and cancellation remain typed.

The Mac sources were compared at `ac00e39e` in `TypeWhisperPluginSDK/Plugins/`. Mac `VoxtralPlugin` is a local MLX engine; `MistralAIPlugin` is the comparable cloud provider. This package is cloud-based, without local downloads, translation or dictionary biasing. Voxtral Realtime sends PCM16 mono 16 kHz audio through the documented WebSocket protocol. It accumulates text deltas for live previews, then waits for the authoritative `transcription.done` result after flushing and ending audio. Interruptions, malformed responses and missing completion fail rather than returning partial text. Realtime models also accept completed host WAV recordings through the same socket, including when live preview is disabled.

## Verification

```powershell
dotnet test plugins-v2/TypeWhisper.Plugin.Voxtral/Tests/TypeWhisper.Plugin.Voxtral.Portable.Tests.csproj -c Release
```

All **60 tests passed**, including capability discovery, filtering/deduplication, restart persistence, failed writes, account changes during refresh, empty account catalogs, shared-key chat requests, optional temperature, reasoning chunks, incomplete replies, independent ZIP lifecycle tests, WebSocket handshake, ordered PCM chunks, fragmented Unicode, complete finalization, typed failures, bounded messages, cancellation, and the real host transcript collector.

On September 19, 2026, real API checks passed for credentials, model discovery (2 batch transcription IDs, 3 realtime IDs and 27 text completion IDs), English synthetic WAV transcription and text correction using `ministral-8b-latest`. `mistral-small-latest` returned HTTP 429 during two attempts; its inference was not accepted as verified. No claim is made that every listed model was individually tested. Marco confirmed normal microphone dictation in the running development app on the same day. A real paced-audio test produced 17 previews before recording stopped (first at 857 ms), then the complete sentence including its final word, without host fallback. The installed package repeated the live test successfully (first preview at 877 ms). Completed WAV input also passed with the realtime model. Marco also confirmed live microphone transcription, including the final sentence after stop. Manual text-workflow acceptance and ARM64 remain pending. The running WinUI app was checked for its Mistral sidebar/selector icon, API model refresh, joint save of realtime and text selections, automatic-language display, and enabled Live transcription switch. Screenshots are in `docs/screenshots/mistral`. No public package or catalog was published.

References: [models](https://docs.mistral.ai/api/endpoint/models), [chat](https://docs.mistral.ai/api/endpoint/chat), [transcription](https://docs.mistral.ai/studio/audio/speech_to_text).

Realtime protocol reference: [official Mistral Python SDK](https://github.com/mistralai/client-python/tree/main/src/mistralai/extra/realtime).
