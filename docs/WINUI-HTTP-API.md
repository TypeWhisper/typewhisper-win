# Windows 1.1 HTTP API

Enable the server under **Settings > Advanced > HTTP API**, choose a port (default 8978), and select **Apply**. The server is off by default. Settings persist across restarts. A port collision leaves it stopped with a visible error. Select **Open documentation** to read the built-in reference at `/docs` in your browser while the server is running. Use **Copy address** and **Copy API token** for clients.

The Windows host exposes all 29 method/path pairs registered by the Mac API, plus `/docs` and `/v1/capabilities`. The existing Raycast extension works without an update when **Require API token** is off (the application default). Use port 8978, or set Raycast's API Port Override; its existing discovery implementation reads only Mac paths.

Advanced also offers the Raycast extension link: an installed `raycast:` handler opens `raycast://extensions/SeoFood/typewhisper`; otherwise it opens the extension's Store page.

## Discovery and authentication

The active profile publishes both Mac-compatible files:

- api-port: decimal listening port.
- api-discovery.json: version (1), port and token, plus host, base_url, pid, api_version and requires_authentication.

The normal development profile is %LOCALAPPDATA%/TypeWhisper-WinUI-DevUserData. Named Debug test profiles are under %TEMP%/TypeWhisper-WinUI-TestProfiles/<name>. Discovery files are replaced atomically and removed on disable, clean shutdown and invalid configuration at startup. After a forced termination, clients should confirm liveness at /v1/status; the next startup replaces/removes stale discovery.

The discovery token is plaintext for local client compatibility, protected by a Windows ACL allowing only the current user. Its persistent backing secret is protected by DPAPI. The token survives restart. Never put it in a URL or log it.

Send Authorization: Bearer <token> or X-TypeWhisper-API-Token: <token>. When Require API token is enabled, authentication is mandatory except for GET /v1/status and the static documentation at GET /docs (also /docs/). The discovery requires_authentication field reports the active mode. Documentation contains no credentials and makes no API calls. Only loopback connections are accepted. Browser Origin headers are rejected; no CORS access is enabled.

## Endpoints

| Method and path | Result |
| --- | --- |
| GET /v1/status | Ready/no-model state, active engine/model, API version and streaming/translation capabilities; no authentication |
| GET /v1/models | Actual models, active engine/model and ready/busy/no-model state |
| GET /v1/capabilities | Supported endpoints, formats and limits |
| POST /v1/transcribe | Raw WAV/octet-stream or multipart file upload |
| POST /v1/transcribe/local-file | JSON with an absolute local Windows path |
| POST /v1/models/load | JSON engine/model; load a downloaded model through its plugin |
| POST /v1/models/unload | JSON engine; unload without disabling the plugin |
| DELETE /v1/models | Query engine/model; remove downloaded assets if the plugin permits it |
| GET /v1/history | Query q, limit (default 50/max 200), offset; entries/total/limit/offset |
| DELETE /v1/history | Query id; delete the entry and its associated audio |
| GET /v1/rules, GET /v1/profiles | Workflow rule lists; both aliases return rules and profiles |
| PUT /v1/rules/toggle, PUT /v1/profiles/toggle | Query id; persist enablement and refresh UI/shortcuts |
| POST /v1/dictation/start | Optional JSON workflow_id; returns recording session id |
| POST /v1/dictation/stop | Returns stopped plus id; processing continues asynchronously |
| GET /v1/dictation/status | Current state and is_recording |
| GET /v1/dictation/transcription | Query id; recording/processing/completed/failed with transcript/error |
| POST /v1/recorder/start | Optional query mic/system_audio booleans |
| POST /v1/recorder/stop | Returns finalizing and session id |
| GET /v1/recorder/status | recording boolean |
| GET /v1/recorder/session | Query id; session state and saved output_file/error |
| GET /v1/dictionary/terms | terms, term_entries and count |
| PUT /v1/dictionary/terms | JSON terms or term_entries array, optional replace |
| DELETE /v1/dictionary/terms | JSON term |
| GET /v1/dictionary/corrections | corrections and count |
| PUT /v1/dictionary/corrections | JSON original/replacement, optional caseSensitive |
| DELETE /v1/dictionary/corrections | JSON original |
| GET /v1/settings/export | Windows portable profile backup JSON |
| POST /v1/settings/import | Validate and merge backup; changed import returns 202, then drains and restarts the app |

All errors use `error.code` and `error.message`. Unknown routes return 404 and incorrect methods return 405. Local OPTIONS returns 204; browser Origin restrictions still apply. The complete route inventory is `LocalApiRouteCatalog`.

Endpoint parity does not make platform-specific assets or all Mac transcription options interchangeable. Engine identifiers come from installed Windows plugins. Explicit model operations return 409 when unsupported or busy; selected model deletion remains protected. Windows backup archives cover dictionary, snippets, workflows and History, excluding credentials/device settings; invalid or unsupported archives return 400. Changed imports are asynchronous (202/restoring/restart_required) because live profile writers must stop before applying the merge. Unchanged imports return 200. Concurrent imports are rejected. Recorder sessions save WAV files; they do not automatically transcribe them.


Transcription options: language, task (transcribe or translate), response_format (json, text, srt, vtt), model and engine. Omitted language/task use the Dictation selection. Engine/model parameters assert the current selection; a mismatch returns 409 and never switches the UI's model. Unsupported language/task returns 422. Model language-hint capabilities still apply. Raw uploads accept query parameters. Multipart accepts fields; local-file requests accept JSON fields. Duplicate or unknown options return 400, including currently unsupported prompt/download/target-language overrides.

Example local-file request:

    {"path":"C:\Audio\sample.wav","task":"transcribe","response_format":"json"}

JSON contains text, engine, model, duration, warnings and segments with text/start/end. Text responses are UTF-8. Subtitle output requires real provider segment timestamps; missing/invalid timestamps return 422. Subtitle segments retain provider text, while the main transcript uses the configured vocabulary/text pipeline.

File-transcription endpoints do not paste into another application, read clipboard placeholders, save History/audio or increment snippet usage. Dictation and recorder control follow the normal application capture/output settings. Configured transcription/text-processing plugins can still make their normal provider calls.

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

## Raycast and Mac-route acceptance

`tests/native/test_winui_http_api_parity.py` prepares and exercises the isolated `http-api-parity-20260908` profile. It checks all 29 method/path pairs, incorrect methods, legacy token-free calls, History paging/deletion, Dictionary CRUD, profile toggles, local model unload/load, real recorder capture/save and dictation session polling. Model removal uses a missing model; installed model assets are not deleted.

The 2026-09-08 native run passed. A separate changed-backup round trip returned 202, rejected a concurrent import with 409, restarted the app, and restored the fixture term. File-transcription and authenticated lifecycle acceptance remain covered by `test-winui-http-api-lifecycle.ps1`, with RequireAuthentication explicitly enabled in that fixture. Full spoken dictation accuracy and cloud-provider inference are not asserted by the parity script.
