# AssemblyAI for the portable host

Independent .NET 10 implementation of `com.typewhisper.assemblyai`, version `1.1.1`, requiring host `1.1.5` or later. The package has no WPF dependency and does not import legacy settings or credentials. Legacy source and packages are unchanged.

## Features and macOS comparison

The port was compared with the [macOS AssemblyAI plugin](https://github.com/TypeWhisper/typewhisper-mac/tree/15ddecc2cd1af24fdb9906ed04400c5a75cc09b1/TypeWhisperPluginSDK/Plugins/AssemblyAIPlugin) on September 14, 2026. Its model catalog, dictionary payloads, speaker labeling and settings informed this independent implementation.

| Capability | Portable implementation |
| --- | --- |
| Models | Universal-3.5 Pro (default) and Universal-2; the old `universal-3-pro` identifier resolves to 3.5 Pro within the new profile. Unknown explicit selections are rejected. |
| Languages | The macOS catalog's 18 Pro languages and conservative 37-language Universal-2 list are exposed in host model metadata. |
| Recorded audio | WAV upload, job submission and cancellable polling; selected model, explicit language or automatic language detection, duration and valid word timestamps. |
| Dictionary | Pro sends `keyterms_prompt` (1,000 entries, six words per term); Universal-2 sends `word_boost` with `boost_param=high` (100 entries, 50 characters per term). |
| Live text | v3 WebSocket, model-specific configuration and dictionary keyterms. Auto uses multilingual streaming for Universal-2. Languages outside Universal-2's six streaming languages require recorded-audio transcription. |
| Speaker diarization | Optional `speaker_labels`; utterance timestamps and `Speaker A: …` labels in both transcript text and segments. As on macOS, enabling it disables live text. |
| Settings | Encrypted key storage/removal, connection validation, model selection and speaker diarization; one host-rendered Save settings action, English/German copy. |

Windows currently exposes text/start/end segments, without separate speaker identity/confidence fields. Speaker labels remain visible in transcript and subtitle text; macOS structured speaker metadata is not claimed as supported. Audio upload uses WAV; macOS's M4A compression helper is not part of this port. Translation is not advertised.

Opening settings performs no requests. Connection validation lists at most one transcript and uploads no audio. A draft key can be tested without saving. All configuration fields and the encrypted-key reference commit together; failed writes retain the preceding active configuration. Credentials and settings remain after uninstall.

Streaming sends 50–1,000 ms PCM16 frames, pads only a short final tail, sends `{"type":"Terminate"}`, and awaits the server's `Termination`. Only formatted final turns are committed; repeated messages for the same turn are not appended twice, while repeated speech in a new turn is retained. Broken, malformed or incomplete streams fail so the host can retry the complete recording. A dictionary exceeding streaming limits uses the recorded-audio path instead of silently dropping terms. Polling has a five-minute wall-clock deadline after job submission. Cancellation stops local requests; it does not promise deletion or cancellation of an already submitted provider job.

## Verification

For repeatable physical microphone-to-paste checks and screenshot evidence, use the [local audio development workflow](../../docs/WINUI-LOCAL-AUDIO-TEST.md). The [reviewed acceptance screenshot](../../docs/screenshots/assemblyai/README.md) is committed with this port.

```powershell
dotnet test plugins-v2/TypeWhisper.Plugin.AssemblyAi/Tests/TypeWhisper.Plugin.AssemblyAi.Portable.Tests.csproj -c Release
dotnet test tests/TypeWhisper.PluginSDK.Portable.Tests/TypeWhisper.PluginSDK.Portable.Tests.csproj -c Release
dotnet msbuild plugins-v2/TypeWhisper.Plugin.AssemblyAi/portable.proj '-t:Build;CopyPackage' -p:Configuration=Release -p:PluginDestination=<staging-directory>
```

All 59 plugin tests and 259 portable SDK/host tests passed. Fake HTTP/WebSocket tests cover payloads, errors, cancellation, polling timeout, multilingual routing, dictionary budgets, speaker labels, unlabeled word fallback when utterances are missing or invalid, timestamps, formatted-turn deduplication, final-tail flushing and termination acknowledgement. Four headless audio-script lifecycle checks cover success and cleanup failures. The real package-store/runtime test covers ZIP identity/hash, installation, configuration, restart, duplicate-install rejection, uninstall/reinstall and retained credentials/settings. The ZIP contains only the provider DLL, dependency manifest and plugin manifest; no host SDK, WPF or test assemblies.

The required Windows development helper built and launched this checkout. The package was installed and enabled through the real immutable store in the development profile, preserving all preceding package receipts. After Marco entered the API key, the running app's `/v1/models` endpoint reported both AssemblyAI models as ready.

Authenticated acceptance passed on September 14, 2026 with a locally synthesized English recording: key validation, REST transcription with dictionary terms for both models (14 word segments each), real-time PCM WebSocket transcription with dictionary terms for both models, confirmed termination without duplicated sentences, and Pro speaker diarization (one labeled utterance). The running app's `/v1/transcribe/local-file` path also returned the exact test sentence, English language and 14 valid segments with Universal-3.5 Pro. Its request-scoped override restored the preceding NVIDIA selection. Direct provider tests used in-memory settings overrides and read the existing encrypted key through the normal secret store; no credentials or user settings were rewritten. These checks do not establish recognition quality on arbitrary recordings or multiple-speaker accuracy.

Microphone-to-paste acceptance also passed on September 14, 2026 in the local Windows console session: synthesized speech played through Creative Pebble Pro speakers was captured with the physical microphone path (Windows default: HyperX QuadCast 2), transcribed by Universal-3.5 Pro, and automatically inserted into a blank Notepad document. The 8.76-second recording returned the exact raw sentence; the existing enabled dictionary correction `test` → `test2` was applied to the inserted text. Paste diagnostics confirmed delivery, and the resulting document was verified in a screenshot. Recording start/stop used the running app's dictation API; this does not establish physical-hotkey coverage. The preceding NVIDIA model selection and audio-ducking preference were restored. Local evidence is under `artifacts/assemblyai/live-results/` (`e2e-result.json`, `e2e-notepad.jpg`).

The native host source includes the official AssemblyAI logomark for light and dark themes. After rebuilding this checkout and verifying matching published/local host DLL hashes, the dark settings page and logomark were inspected and captured in the committed evidence. Light-theme layout, an update to a subsequent plugin version, ARM64 execution and installed legacy/v2 side-by-side acceptance remain pending. The shared development host output was replaced by another checkout before the microphone test; the installed AssemblyAI package was unchanged. No public catalog or release was changed.

## Protocol references

- [Recorded-audio model selection](https://www.assemblyai.com/docs/pre-recorded-audio/select-the-speech-model)
- [Streaming model selection](https://www.assemblyai.com/docs/streaming/select-the-speech-model)
- [Streaming WebSocket API](https://www.assemblyai.com/docs/streaming/api-spec/streaming-websocket)
- [Streaming message sequence and turn ordering](https://www.assemblyai.com/docs/streaming/message-sequence)

Dictionary terms use the structured host contract 1.1.5 or newer, preserving literal commas inside each entry. Older plugin packages keep their original prompt contract.
