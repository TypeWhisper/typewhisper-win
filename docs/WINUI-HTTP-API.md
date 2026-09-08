# Windows 1.1 HTTP API

Enable the server under **Settings > Advanced > HTTP API**, choose a port (default 8978), and select **Apply**. The server is off by default. Settings persist across restarts. A port collision leaves it stopped with a visible error. Use **Copy address** and **Copy API token** for clients.

This is the connected file-transcription API, not complete legacy HTTP endpoint parity. Dictation/recorder control, history/dictionary mutation, model switching/download, backup import and automation routes are not exposed yet. Unknown endpoints return 404.

## Discovery and authentication

The active profile publishes both Mac-compatible files:

- api-port: decimal listening port.
- api-discovery.json: version (1), port and token, plus host, base_url, pid, api_version and requires_authentication.

The normal development profile is %LOCALAPPDATA%/TypeWhisper-WinUI-DevUserData. Named Debug test profiles are under %TEMP%/TypeWhisper-WinUI-TestProfiles/<name>. Discovery files are replaced atomically and removed on disable, clean shutdown and invalid configuration at startup. After a forced termination, clients should confirm liveness at /v1/status; the next startup replaces/removes stale discovery.

The discovery token is plaintext for local client compatibility, protected by a Windows ACL allowing only the current user. Its persistent backing secret is protected by DPAPI. The token survives restart. Never put it in a URL or log it.

Send Authorization: Bearer <token> or X-TypeWhisper-API-Token: <token>. Authentication is mandatory except for minimal server liveness at GET /v1/status. Only loopback connections are accepted. Browser Origin headers are rejected; no CORS access is enabled.

## Endpoints

| Method and path | Result |
| --- | --- |
| GET /v1/status | Minimal status: ok and API version; no authentication |
| GET /v1/models | Actual models, active engine/model and ready/busy/no-model state |
| GET /v1/capabilities | Supported endpoints, formats and limits |
| POST /v1/transcribe | Raw WAV/octet-stream or multipart file upload |
| POST /v1/transcribe/local-file | JSON with an absolute local Windows path |

Transcription options: language, task (transcribe or translate), response_format (json, text, srt, vtt), model and engine. Omitted language/task use the Dictation selection. Engine/model parameters assert the current selection; a mismatch returns 409 and never switches the UI's model. Unsupported language/task returns 422. Model language-hint capabilities still apply. Raw uploads accept query parameters. Multipart accepts fields; local-file requests accept JSON fields. Duplicate or unknown options return 400, including currently unsupported prompt/download/target-language overrides.

Example local-file request:

    {"path":"C:\Audio\sample.wav","task":"transcribe","response_format":"json"}

JSON contains text, engine, model, duration, warnings and segments with text/start/end. Text responses are UTF-8. Subtitle output requires real provider segment timestamps; missing/invalid timestamps return 422. Subtitle segments retain provider text, while the main transcript uses the configured vocabulary/text pipeline.

The API does not paste into another application, read clipboard placeholders, save History/audio or increment snippet usage. Configured transcription/text-processing plugins can still make their normal provider calls.

## Limits and lifecycle

Uploads are bounded to 32 MiB, including multipart framing. Decoding retains the application's 60-minute audio limit. Network/UNC/device paths and reparse-point paths are rejected for local-file requests. Temporary uploads are removed after success, cancellation and failure.

Up to four HTTP requests are admitted; excess traffic returns 429. The shared transcription gate allows one decode at a time and returns 409 when dictation, recording, training, file processing or another API request owns the engine. No-ready-model returns 503. Undecodable audio returns 422. Transport timeouts cancel processing; native work is still drained before the engine is released. Shutdown/profile restore closes admission, cancels requests and awaits active work.

## Tests

Headless host and parser tests run in the existing Presentation suite:

    dotnet test tests/TypeWhisper.Presentation.Tests --filter "FullyQualifiedName~LocalApi|FullyQualifiedName~LocalHttpApi"

They exercise real loopback HTTP, authentication-before-body, body limits, multipart, UTF-8, duplicate/unknown options, port conflicts, concurrent admission, request timeout and cancellation/drain. No cloud credentials or microphone are required.

For native acceptance, prepare http-api-20260908 as an isolated Debug profile with a ready local model and synthetic speech at artifacts/ui-fixtures/file-speech-en.wav. The 2026-09-08 run reused the Canary-only packages/model assets from the isolated watched-folder fixture without personal data or cloud keys:

    ./tests/native/test-winui-http-api-lifecycle.ps1 -SourcePath (Get-Location).Path

The prescribed-build script verifies startup/disable, Mac discovery and owner-only ACL, stale discovery cleanup after corrupt settings, token persistence, and real raw/multipart/local-file recognition through the running app. It restores the normal development launch in finally.

Standalone native HTTP checks:

    python tests/native/test_winui_http_api.py --profile "$env:TEMP/TypeWhisper-WinUI-TestProfiles/http-api-20260908" --audio artifacts/ui-fixtures/file-speech-en.wav

The script refuses an active cloud model and checks successful text, segment shape, wrong credentials/options, concurrent 200/409 responses, unchanged History/source bytes and an empty upload directory. Authenticated cloud-provider acceptance and unimplemented control endpoints are outside this run.
