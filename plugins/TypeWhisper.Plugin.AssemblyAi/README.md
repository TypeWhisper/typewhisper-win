# AssemblyAI for the portable host

Portable .NET 11 implementation of `com.typewhisper.assemblyai`, version `1.4.0`, requiring host `1.3.1` or later.

## Features

| Capability | Portable implementation |
| --- | --- |
| Models | AssemblyAI-STT-3.7 (default, 32 languages) and Universal-2 (37 languages), as on macOS 1.4.0. AssemblyAI deprecated Universal-3.5 Pro on November 17, 2026; saved `universal-3-5-pro` and `universal-3-pro` selections move to AssemblyAI-STT-3.7 at activation, and the model lists both as `FormerIds`, so workflows and HTTP API requests that name them keep working. Unknown explicit selections are rejected. |
| Transcription mode | Live (default), Sync ($0.30/h), Dictation ($0.62/h) or Async ($0.21/h), AssemblyAI's list prices in November 2026, as on macOS. Only Live streams; the other modes show no live preview from AssemblyAI. Universal-2 has no modes and always uses the async API ($0.15/h); the mode setting is hidden for it. |
| Live model | In the Live mode, Realtime-Agent (`assemblyai-stt-3-7-realtime-agent`, $0.45/h, default) keeps the preview steady; Realtime (`assemblyai-stt-3-7-realtime`, $0.30/h) costs less, but the preview changes more while you speak. Profiles from before 1.4.0 get Realtime-Agent, the price Live had. |
| Recorded audio | AssemblyAI-STT-3.7 in the Live, Sync and Dictation modes: one multipart request to the Sync API (`sync.assemblyai.com/v1/transcribe`, `X-AAI-Model: universal-3-6-pro`) with the selected language as `language_codes` and up to 100 keyterms (8,000 characters), for recordings up to 20 minutes and 256 MB in the 32 languages. The Sync API reports no detected language; its word timestamps become segments, and a word it cannot align joins the segment before it, so subtitles keep all text. Otherwise, and when a Sync request fails for any reason other than an invalid key, WAV upload, job submission and cancellable polling (async API) with `speech_models: ["assemblyai-stt-3-7", "universal-2"]`, so automatic language detection can fall back to Universal-2, and `disfluencies: false`, since AssemblyAI-STT-3.7 keeps filler words by default. A failed Sync or Dictation request is not sent again, since AssemblyAI may already have billed it; when the host retries the async fallback, the same recording goes straight to the async API. |
| Dictation API | In the Dictation mode, dictations (`TranscriptionRequestContext.Purpose == Dictation`, host 1.3.1) go to `dictation.assemblyai.com/v1/transcribe` with the Sync request's audio, language and keyterms, without a model header and without `timestamps`, which the Dictation API rejects. The cleaned-up `llm_response` is used, the verbatim `text` when the rewrite failed. A request may take 30 s plus the dictation's length, at most 90 s (AssemblyAI's recommended timeout), plus 1 s per MB for the upload; then, and on any failure other than an invalid key, the dictation goes to the async API without cleanup. Recordings, files, watch folders, the HTTP API and dictionary training keep the verbatim transcript from the Sync API. |
| Dictionary | AssemblyAI-STT-3.7 sends `keyterms_prompt` (1,000 entries, six words per term); Universal-2 sends `word_boost` with `boost_param=high` (100 entries, 50 characters per term). |
| Live text | Live mode with AssemblyAI-STT-3.7 only. v3 WebSocket with the selected live model, the selected language as `language_codes`, `language_detection=true` so turns report the spoken language, and dictionary keyterms. Universal-2 no longer streams: Universal-Streaming English and Multilingual retire on February 1, 2027, and macOS never streamed it. |
| Speaker diarization | Optional `speaker_labels` for recordings, files, watch folders and the HTTP API; utterance timestamps and `Speaker A: …` labels in both transcript text and segments. Dictations leave speaker labels out, as on macOS. Enabling it still disables live text. |
| Settings | Encrypted key storage/removal, connection validation, model selection, transcription mode, live model and speaker diarization; one host-rendered Save settings action, English/German copy. |

Windows currently exposes text/start/end segments, without separate speaker identity/confidence fields. Speaker labels remain visible in transcript and subtitle text; macOS structured speaker metadata is not claimed as supported. Audio upload uses WAV; macOS's M4A compression helper is not part of this port. Translation is not advertised.

Opening settings performs no requests. Connection validation lists at most one transcript and uploads no audio. A draft key can be tested without saving. All configuration fields and the encrypted-key reference commit together; failed writes retain the preceding active configuration. Credentials and settings remain after uninstall.

Streaming sends 50–1,000 ms PCM16 frames, pads only a short final tail, sends `{"type":"Terminate"}`, and awaits the server's `Termination`, which arrives about half a second after the last turn. As on macOS, the stream finishes earlier once no turn is open and the audio sent after the last confirmed word is quiet (below 15 % of the loudest 50 ms window, and never above four times the noise floor of the quietest tenth of windows, so quieter speech after a loud sound does not pass for silence), so that audio cannot hold another word. A session closed before it finished also tries to send `Terminate`, which ends AssemblyAI's billing. When the connection is already gone, another send is still running after 500 ms, or the send fails within a second, the socket is closed without it, and AssemblyAI bills the session until it closes it itself. Only formatted final turns are committed; repeated messages for the same turn are not appended twice, while repeated speech in a new turn is retained. Broken, malformed or incomplete streams fail so the host can retry the complete recording. A dictionary exceeding streaming limits uses the recorded-audio path for the final text instead of silently dropping terms; hosts that support preview streams still show live text from a stream without the dictionary. Polling has a five-minute wall-clock deadline after job submission. Cancellation stops local requests; it does not promise deletion or cancellation of an already submitted provider job.

## Verification

For repeatable physical microphone-to-paste checks and screenshot evidence, use the [local audio development workflow](../../docs/WINUI-LOCAL-AUDIO-TEST.md). See the [settings screenshots](../../docs/screenshots/assemblyai/README.md).

```powershell
dotnet test plugins/TypeWhisper.Plugin.AssemblyAi/Tests/TypeWhisper.Plugin.AssemblyAi.Portable.Tests.csproj -c Release
dotnet test tests/TypeWhisper.PluginSDK.Portable.Tests/TypeWhisper.PluginSDK.Portable.Tests.csproj -c Release
dotnet msbuild plugins/TypeWhisper.Plugin.AssemblyAi/portable.proj '-t:Build;CopyPackage' -p:Configuration=Release -p:PluginDestination=<staging-directory>
```

Fake HTTP/WebSocket tests cover Sync and Dictation API payloads, routing between the Sync, Dictation and async APIs and the fallback, the transcription modes and live models, the purpose of a request, former model IDs, payloads, errors, cancellation, polling timeout, multilingual routing, dictionary budgets, speaker labels, unlabeled word fallback when utterances are missing or invalid, timestamps, formatted-turn deduplication, final-tail flushing and termination acknowledgement. Headless audio-script lifecycle checks cover success and cleanup failures. The real package-store/runtime test covers ZIP identity/hash, installation, configuration, restart, duplicate-install rejection, uninstall/reinstall and retained credentials/settings. The ZIP contains only the provider DLL, dependency manifest and plugin manifest; no host SDK or test assemblies.

Validate light-theme layout, package updates, ARM64 execution and installed 1.0/1.1 coexistence separately. Synthetic audio tests do not establish recognition quality or multiple-speaker accuracy.

## Protocol references

- [Sync API](https://www.assemblyai.com/docs/api-reference/sync-api/transcribe)
- [Dictation API](https://www.assemblyai.com/docs/dictation)
- [Recorded-audio model selection](https://www.assemblyai.com/docs/pre-recorded-audio/select-the-speech-model)
- [Streaming model selection](https://www.assemblyai.com/docs/streaming/select-the-speech-model)
- [Streaming WebSocket API](https://www.assemblyai.com/docs/streaming/api-spec/streaming-websocket)
- [Streaming message sequence and turn ordering](https://www.assemblyai.com/docs/streaming/message-sequence)

Dictionary terms use the structured host contract 1.1.5 or newer, preserving literal commas inside each entry. Older plugin packages keep their original prompt contract.
