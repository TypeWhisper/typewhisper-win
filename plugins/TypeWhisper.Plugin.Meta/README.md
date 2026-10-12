# Meta portable plugin

Meta transcription, realtime transcription, LLM completion, model refresh, language/dictionary/diarization and reasoning settings.

Version `1.2.14`; plugin ID `com.typewhisper.meta`; minimum host `1.1.5`.

## Setup

Enter the API key, refresh models if needed and choose the engine/model in a workflow.

## Platform scope

Provider rollout and model availability require an authenticated check.

## Build and verification

From the repository root:

```powershell
dotnet msbuild plugins/TypeWhisper.Plugin.Meta/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins/TypeWhisper.Plugin.Meta/Tests -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.meta` inside the plugin project. Package that directory as the ZIP root.

Tests cover fake HTTP protocol, key storage failure, real local WebSocket finalization/premature close and package lifecycle. Realtime completion emits one terminal final result. All packages have isolated install, enable, restart, disable, uninstall and reinstall coverage through the real portable package loader and host services.

## Settings and streaming

The settings page groups speaker labels under transcription and model/reasoning choices under text processing. Model choices come from the account API catalog; settings use the host’s shared save action. The host displays the Meta brand icon in navigation and provider selectors.

Diarization finalization drains all completed speaker turns and requires a clean WebSocket close after endStream. Loopback tests cover delayed final turns, abnormal closure, and missing turn completion.

Push-to-talk accepts the explicit final transcript after stop even when the server later closes the transport without a close frame. Diarization still drains all completed turns through clean stream closure; incomplete or prematurely closed responses remain errors.

Dictionary terms use the structured host contract 1.1.5 or newer, preserving literal commas inside each entry. Older plugin packages keep their original prompt contract.
