# Reson8 portable plugin

Batch and realtime transcription, model refresh and portable server/model/header settings.

Version `1.2.10`; plugin ID `com.typewhisper.reson8`; minimum host `1.1.2`.

## Setup

Enter the API key and choose a model. Keep the default server settings unless using a compatible deployment.

Batch requests expose HTTP status, retry delay and failure kind to the host. Hosts with
request resilience can retry transient failures and split uploads rejected with HTTP 413;
authentication failures and caller cancellation are not retried.

## Platform scope

REST and WebSocket requests use ApiKey authorization.

## Build and verification

From the repository root:

```powershell
dotnet msbuild plugins/TypeWhisper.Plugin.Reson8/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins/TypeWhisper.Plugin.Reson8/Tests -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.reson8` inside the plugin project. Package that directory as the ZIP root.

Tests cover fake HTTP, normalization/persistence, model discovery, real local WebSocket finalization/premature close and package lifecycle. All packages have isolated install, enable, restart, disable, uninstall and reinstall coverage through the real portable package loader and host services.

The current source validates connection settings and model catalogs, preserves model state when refresh or persistence fails, and resets account-specific models only after a successful connection change.

Streaming honors callback cancellation, preserves proxy paths and fails promptly after receive errors. Connection setup has a 15-second deadline; completion allows the submitted PCM duration plus 15 seconds, while honoring any shorter caller cancellation deadline. Unsupported or truncated WAV input is rejected before upload. The logo is included in WinUI build and publish output.
