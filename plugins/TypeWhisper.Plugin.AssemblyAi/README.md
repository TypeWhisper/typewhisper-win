# AssemblyAI for the portable host

Portable .NET 10 implementation of `com.typewhisper.assemblyai`, version `1.1.3`, requiring host `1.1.5` or later.

## Features

| Capability | Portable implementation |
| --- | --- |
| Models | Universal-3.5 Pro (default) and Universal-2; the old `universal-3-pro` identifier resolves to 3.5 Pro within the new profile. Unknown explicit selections are rejected. |
| Languages | 18 Pro languages and conservative 37-language Universal-2 list are exposed in host model metadata. |
| Recorded audio | WAV upload, job submission and cancellable polling; selected model, explicit language or automatic language detection, duration and valid word timestamps. |
| Dictionary | Pro sends `keyterms_prompt` (1,000 entries, six words per term); Universal-2 sends `word_boost` with `boost_param=high` (100 entries, 50 characters per term). |
| Live text | v3 WebSocket, model-specific configuration and dictionary keyterms. Auto uses multilingual streaming for Universal-2. Languages outside Universal-2's six streaming languages require recorded-audio transcription. |
| Speaker diarization | Optional `speaker_labels`; utterance timestamps and `Speaker A: …` labels in both transcript text and segments. As on macOS, enabling it disables live text. |
| Settings | Encrypted key storage/removal, connection validation, model selection and speaker diarization; one host-rendered Save settings action, English/German copy. |

Windows currently exposes text/start/end segments, without separate speaker identity/confidence fields. Speaker labels remain visible in transcript and subtitle text; macOS structured speaker metadata is not claimed as supported. Audio upload uses WAV; macOS's M4A compression helper is not part of this port. Translation is not advertised.

Opening settings performs no requests. Connection validation lists at most one transcript and uploads no audio. A draft key can be tested without saving. All configuration fields and the encrypted-key reference commit together; failed writes retain the preceding active configuration. Credentials and settings remain after uninstall.

Streaming sends 50–1,000 ms PCM16 frames, pads only a short final tail, sends `{"type":"Terminate"}`, and awaits the server's `Termination`. Only formatted final turns are committed; repeated messages for the same turn are not appended twice, while repeated speech in a new turn is retained. Broken, malformed or incomplete streams fail so the host can retry the complete recording. A dictionary exceeding streaming limits uses the recorded-audio path instead of silently dropping terms. Polling has a five-minute wall-clock deadline after job submission. Cancellation stops local requests; it does not promise deletion or cancellation of an already submitted provider job.

## Verification

For repeatable physical microphone-to-paste checks and screenshot evidence, use the [local audio development workflow](../../docs/WINUI-LOCAL-AUDIO-TEST.md). See the [settings screenshots](../../docs/screenshots/assemblyai/README.md).

```powershell
dotnet test plugins/TypeWhisper.Plugin.AssemblyAi/Tests/TypeWhisper.Plugin.AssemblyAi.Portable.Tests.csproj -c Release
dotnet test tests/TypeWhisper.PluginSDK.Portable.Tests/TypeWhisper.PluginSDK.Portable.Tests.csproj -c Release
dotnet msbuild plugins/TypeWhisper.Plugin.AssemblyAi/portable.proj '-t:Build;CopyPackage' -p:Configuration=Release -p:PluginDestination=<staging-directory>
```

Fake HTTP/WebSocket tests cover payloads, errors, cancellation, polling timeout, multilingual routing, dictionary budgets, speaker labels, unlabeled word fallback when utterances are missing or invalid, timestamps, formatted-turn deduplication, final-tail flushing and termination acknowledgement. Headless audio-script lifecycle checks cover success and cleanup failures. The real package-store/runtime test covers ZIP identity/hash, installation, configuration, restart, duplicate-install rejection, uninstall/reinstall and retained credentials/settings. The ZIP contains only the provider DLL, dependency manifest and plugin manifest; no host SDK or test assemblies.

Validate light-theme layout, package updates, ARM64 execution and installed 1.0/1.1 coexistence separately. Synthetic audio tests do not establish recognition quality or multiple-speaker accuracy.

## Protocol references

- [Recorded-audio model selection](https://www.assemblyai.com/docs/pre-recorded-audio/select-the-speech-model)
- [Streaming model selection](https://www.assemblyai.com/docs/streaming/select-the-speech-model)
- [Streaming WebSocket API](https://www.assemblyai.com/docs/streaming/api-spec/streaming-websocket)
- [Streaming message sequence and turn ordering](https://www.assemblyai.com/docs/streaming/message-sequence)

Dictionary terms use the structured host contract 1.1.5 or newer, preserving literal commas inside each entry. Older plugin packages keep their original prompt contract.
