# Reson8 portable plugin

Batch and realtime transcription with Reson8's model list, dictionary terms, pattern sets and portable server/header settings.

Version `1.4.0`; plugin ID `com.typewhisper.reson8`; minimum host `1.1.5`.

## Setup

Enter the API key, select Save settings, then Check connection; a verified key also loads the models. Choose a model and keep the default server settings unless using a compatible deployment.

Batch requests expose HTTP status, retry delay and failure kind to the host. Hosts with
request resilience can retry transient failures and split uploads rejected with HTTP 413;
authentication failures and caller cancellation are not retried.

A custom server URL must use HTTPS, or HTTP only on this computer (`localhost`, `127.0.0.1`, `::1`), because the API key goes with every request. HTTP to another computer is rejected when saving; a URL saved by an earlier version stays shown, but requests to it stop with a configuration error before the key is sent.

## Models

The model list shows Reson8's models from `GET /v1/model` next to the custom models from `GET /v1/custom-model` and the pattern sets. The default model keeps the ID `__default__`, so saved selections keep working; it is named after Reson8's default model (Resonant-1). A listed model is sent as `model`, a custom model as `custom_model_id`; custom models and pattern sets run on the default model. **Refresh models** loads both lists; a list that cannot be loaded keeps its previous entries and the selection. Another server URL drops both lists of the previous server, so the new one starts with the built-in model until its lists are loaded.

With an API key, both lists also reload in the background when the plugin starts and they are a day old or have no date, and after the key, server URL or header changes. While the plugin is active, it repeats this check every hour, so lists that age while the app keeps running reload, and a reload that failed, also for one list only, is tried again. A list that cannot be loaded keeps its entries, and a result that arrives after one of these changes or after deactivation is dropped. **Refresh models** and Check connection reset the day. A saved model that the lists no longer offer stays saved: the default model runs meanwhile, and the saved model applies again once a list offers it. A new key, server or header still resets the selection to the default model, because custom models belong to one account and server.

Per model, the supported languages follow its `languages` and live text follows its `endpoints`. `GET /v1/model` does not say which parameters a model rejects, so the plugin keeps the table from Reson8's model documentation: Rumble-2-Flash rejects `diarize`, `include_timestamps`, `include_words`, `phrases`, `custom_model_id`, `patterns` and `filler_mode`. With Rumble-2-Flash the plugin sends none of them, reports no dictionary support and no live text, and the host transcribes the complete recording.

## Requests

- Several preferred languages go as `language=nl,en`, which limits Reson8's per-utterance detection to them; `include_language=true` reports the detected language.
- Both endpoints request `include_timestamps` and `include_words`. The words become token timings and sentence segments with the macOS rules: a pause over one second, a sentence end after three words, or 30 seconds.
- **Filler words** sends `filler_mode` (Clean or Verbatim); Natural sends nothing, Reson8's default.
- **Bias strength** sends `bias_strength`, only together with phrases or a custom model.
- `MaximumAudioUploadBytes` is five minutes of 16 kHz PCM16, so the host splits longer recordings into five-minute requests.
- Errors name Reson8's detail: 400 rejected parameters, 402 the credit limit, 404 a missing model or custom model, 429 the concurrent connection limit. A rejected live connection names its `X-Error-Message`.

Finished recordings and files always use the prerecorded endpoint, at 1 credit per minute; the host shows no progress for them, so realtime (2 credits per minute) would only cost more. Live dictation uses the realtime endpoint.

## Dictionary

Active dictionary terms arrive as a structured list. They go as `phrases` in the query string: up to 250 terms of up to 255 characters while the encoded list stays within 2,500 characters, because Reson8 answers 414 above about 4,000. A term with a comma is left out, since Reson8 splits the list at commas.

**Keep dictionary in a custom model** (off by default) keeps the terms in a custom model named "TypeWhisper Dictionary" in the Reson8 account instead, with up to 50,000 terms. Only changes go to Reson8: the terms added to or removed from the dictionary since the last sync, in batches of 1,000. The terms of a batch that fails or times out count as unconfirmed and go again with the next sync, in whichever direction the dictionary then needs; Reson8 skips phrases that are already present or already gone. A request waits up to five seconds for the sync and sends phrases instead while it continues in the background. When Reson8 no longer knows the model ("Unknown vocabulary"), the plugin creates it again and retries once. A changed key, server or header forgets the model. The model does not appear in the model list, and a selected custom model takes the terms as phrases.

## Pattern sets

**Pattern sets** is a text field: the set's name on one line, one pattern per line below it, sets separated by an empty line. Each set appears as a model named "Name (patterns)" with the ID `patterns:<id>`, so a workflow can select it where such tokens are spoken; the ID stays the same while the name does, and a new set never takes the ID of a kept one. A set has to fit into the query string like the phrases: its encoded patterns stay within 2,500 characters, and a longer set is rejected when saving. Patterns replace dictionary terms. **Check patterns with Reson8** sends half a second of silence with each set and reports Reson8's answer, for example that an API client with a custom model configured takes no patterns.

## Not included

Speaker diarization needs speaker labels on segments, which the Windows SDK does not have yet. The turns endpoint would need the host to end a dictation when the plugin reports a finished thought.

## Build and verification

From the repository root:

```powershell
dotnet msbuild plugins/TypeWhisper.Plugin.Reson8/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins/TypeWhisper.Plugin.Reson8/Tests -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.reson8` inside the plugin project. Package that directory as the ZIP root.

Tests cover fake HTTP, normalization/persistence, model discovery, real local WebSocket finalization/premature close, rejected upgrades and package lifecycle. Feature tests cover the model list, per-model parameters, query building, transcript parsing, the dictionary custom model sync, pattern sets, settings and actions. All packages have isolated install, enable, restart, disable, uninstall and reinstall coverage through the real portable package loader and host services.

The current source validates connection settings and model catalogs, preserves model state when refresh or persistence fails, and resets account-specific models only after a successful connection change.

Streaming honors callback cancellation, preserves proxy paths and fails promptly after receive errors. Audio goes out in frames of at most 8 KiB, because Reson8 closes the connection on frames above 64 KiB. Connection setup has a 15-second deadline; completion allows the submitted PCM duration plus 15 seconds, while honoring any shorter caller cancellation deadline. Unsupported or truncated WAV input is rejected before upload. The logo is included in WinUI build and publish output.
