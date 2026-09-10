# WinUI HTTP API implementation plan

Implementation checkpoint: 2026-09-08. The initial WinUI file-transcription HTTP server is connected. See [usage and tests](WINUI-HTTP-API.md).

## Existing contracts

The old Windows implementation lives in src/TypeWhisper.Windows/Services/HttpApiService.cs, HttpApiRequestParser.cs and ApiServerController.cs. The Mac reference is TypeWhisper/Services/HTTPServer/APIHandlers.swift. Existing Windows tests are HttpApiServiceTests, HttpApiRequestParserTests and ApiServerControllerTests.

Both expose status/models, uploaded and local-file transcription, history read/delete, rules/profiles read/toggle, dictation start/stop/status/result, recorder start/stop/status/session, dictionary terms/corrections and settings export/import. Mac also exposes model load/unload/delete. Windows automation routes are gated by TYPEWHISPER_AUTOMATION.

The old Windows server defaults to port 8978 and binds localhost/127.0.0.1. Server enablement and optional authentication default off. Status is public; backup and automation require authentication. Bearer and X-TypeWhisper-API-Token are accepted. Windows protects persisted settings tokens with DPAPI but also supplies a token in local api-discovery.json. Mac explicitly restricts discovery to owner read/write permissions. Do not copy optional authentication or body-before-auth processing into the new host.

## Delivery scope and remaining extensions

1. Connected: UI-independent host/router with mandatory token authentication except for minimal status, loopback-only binding, bounded uploads/concurrency, explicit enable/port controls, discovery lifecycle and safe shutdown.
2. Connected: GET /v1/status and GET /v1/models from real LocalDictationSession/provider state.
3. Connected: POST /v1/transcribe and POST /v1/transcribe/local-file using a shared request-aware transcription operation. Reject unsupported options explicitly; do not silently ignore model/language/prompt overrides.
4. Remaining: Dictation session IDs and start/stop/result access, followed by recorder controller extraction and storage mutations.
5. Remaining: Settings import must use the reviewed backup/restore drain transaction; it must not write stores behind active UI services.

Use WinUIProfile paths for settings/token/discovery, DispatcherQueue for UI-owned state, and the existing admission and shutdown boundaries. LocalDictationSession.Files.cs already owns exclusive decoding, vocabulary processing and cancellation. It currently snapshots global settings, so per-request overrides need an explicit context. Do not use ModelSession.cs: it is a sample catalog. RecorderView still owns session orchestration and needs extraction before a second API client can control it safely. Do not instantiate old WPF services against the new host's stores.

## Verification

Reuse existing parser/contract cases where applicable. Add tests for invalid-token requests never reaching the backend, unknown options, body limits, port conflicts, discovery cleanup, concurrent requests, recorder/dictation contention, stable result IDs, shutdown/restore cancellation and rejection of late History commits. Keep browser CORS closed unless a specific authorized client requires it. Record native and real-network acceptance separately from headless contract tests.
