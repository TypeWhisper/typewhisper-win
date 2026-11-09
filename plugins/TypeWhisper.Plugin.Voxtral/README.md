# Mistral for the portable host

Portable .NET 10 package `com.typewhisper.voxtral`, version `1.5.0`, requiring host `1.1.2`. The visible provider is **Mistral**. Its existing package and provider IDs remain unchanged so encrypted credentials, enabled state and dictation selections survive the rename.

## Models

The plugin uses one Mistral API key for Voxtral transcription and Mistral chat completions. Until the account's list is loaded, it offers the current lineup: Voxtral Mini Transcribe 3 (default), Voxtral Small Transcribe 3 and Voxtral Mini Transcribe Realtime 3 for transcription; Mistral Large 4, Medium 3.5, Small 4 (default) and Ministral 3 14B, 8B and 3B for text.

**Refresh models** reads `/v1/models` and keeps a cleaned account catalog with its fetch date. The list also reloads in the background when the plugin starts with a list that is a day old, has no fetch date (catalogs saved before 1.5.0) or a date in the future, and right after a key change. The saved list stays in use until the new one arrives. A failed background refresh, or one whose list has no transcription or no text models, keeps the current list, so it never switches a capability off; the button still saves such a list.

- Mistral lists every alias as its own model. Aliases of one model become one entry; known models get their product name and a fixed order. Unknown models keep their catalog name and a dated ID rather than a moving `-latest` alias.
- Voxtral Transcribe 2 and Realtime 2 are hidden while their version 3 successors are listed. Code completion, Labs, audio-understanding chat and speech models are not offered for text processing. Archived models are skipped.
- Saved selections that name an alias, a Voxtral 2 model or a retired chat model (the list matches the macOS plugin) resolve to the listed model. Saved selections stay in the settings while a list lacks them: the default model is used meanwhile, and the saved one applies again once a list offers it. Catalogs saved before 1.4.0 fall back to the current lineup until the next refresh.
- Model listing is not a guarantee of quota or inference permission; a workspace can set a model's limit to zero requests.

## Transcription

Languages and dictionary support come from each model's `audio_transcription_capabilities`. Transcribe 3 and Realtime 3 accept 58 languages. A language outside the selected model's list is dropped instead of being sent, because Mistral rejects it.

- **File transcription** posts to `/v1/audio/transcriptions`. Without a pinned language the request sets `return_language=true`, since Transcribe 3 otherwise returns no language; with a pinned language it omits the flag, which Mistral would reject with HTTP 422.
- **Dictionary terms** are sent as repeated `context_bias` fields (up to 200 terms, 8,000 characters), so names such as "Saoirse Ronan" keep their spaces. The plugin accepts the host's structured dictionary prompt.
- **Voxtral Realtime 3** streams PCM16 mono 16 kHz audio through the documented WebSocket protocol and shows its text deltas as live preview. The final text comes from uploading the complete recording to Voxtral Mini Transcribe 3 (`SupportsStreamingPreviewForPrompt`): on the same German dictations, Realtime 3 misheard more words ("Wir öffnen" for "Bitte öffne", "3.399" for "1.399") and needed 0.85–1.7 seconds after stopping, the upload 0.55–0.7 seconds. This bills both services. Hosts without preview-only streaming transcribe after recording stops.
- **Accounts without a file model** keep the live session as the source of the final text. The language and dictionary then go into the single `session.update`, because Mistral locks session settings when audio starts, and the plugin waits for the authoritative `transcription.done` result after flushing and ending audio. Interruptions, malformed responses and missing completion fail rather than returning partial text.
- **Complete recordings with a realtime model selected** (live text off, file transcription, final text after a preview) upload to Voxtral Mini Transcribe 3 instead of streaming through the socket: a 63-second recording took 1 second instead of 40. Only an account without a file model streams the recording, with the audio duration plus 15 seconds to finish.

Speaker labels (`diarize`) and turn detection are not used: the Windows SDK has no speaker segments, and a plugin cannot end a dictation.

## Text processing

Text processing supports the selected default or an explicit workflow model, provider-default or custom temperature, complete-response validation, and reasoning responses containing text chunks. Thinking chunks are excluded from the returned text.

Models with documented reasoning levels get a **Reasoning effort** setting, shown while that model is selected and also used when a workflow names the model. The default is the fastest level: `low` for Large 4 (it rejects `none`), `none` for Small 4 and Medium 3.5. Ministral rejects the parameter, so it is never sent there.

## Settings and errors

Settings use the host's single **Save settings** button. The key stays in encrypted host storage. Opening settings performs no network request. A failed model fetch or settings write retains the previous catalog. Changing accounts resets the old catalog, and neither the button nor the background refresh can publish models fetched using a replaced key. Deactivation cancels a running background refresh, and a save that was already under way keeps the saved key. Redirects are disabled; HTTP status, retry information and cancellation remain typed. Mistral error bodies are reduced to their message, without the request fields that validation errors echo.

Mac `VoxtralPlugin` is a local MLX engine; `MistralAIPlugin` is the comparable cloud provider.

## Verification

```powershell
dotnet test plugins/TypeWhisper.Plugin.Voxtral/Tests/TypeWhisper.Plugin.Voxtral.Portable.Tests.csproj -c Release
```

Tests cover catalog grouping and naming, hidden predecessors, saved-ID resolution and old catalogs, language pinning and `return_language`, dictionary terms in uploads and live sessions, complete recordings with realtime models, per-model reasoning levels, readable error messages, restart persistence, failed writes, account changes during refresh, WebSocket handshake, ordered PCM chunks, fragmented Unicode, complete finalization, typed failures, bounded messages, cancellation, the real host transcript collector, and concurrent key/model writes without UI-context capture.

References: [models](https://docs.mistral.ai/api/endpoint/models), [chat](https://docs.mistral.ai/api/endpoint/chat), [file transcription](https://docs.mistral.ai/studio/audio/speech-to-text/transcribe-files), [realtime transcription](https://docs.mistral.ai/studio/audio/speech-to-text/transcribe-in-realtime), [migrating from Voxtral 2](https://docs.mistral.ai/studio/audio/migrate-from-voxtral-2), [reasoning](https://docs.mistral.ai/inference/conversations/reasoning).
