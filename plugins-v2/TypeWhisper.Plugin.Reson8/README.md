# Reson8 portable plugin

Batch and realtime transcription, model refresh and portable server/model/header settings.

Version `1.2.6`; plugin ID `com.typewhisper.reson8`; minimum host `1.1.2`.
Independent branch: `seofood/reson8-portable`, based on `4db8f6ac`.

## Setup

Enter the API key and choose a model. Keep the default server settings unless using a compatible deployment.

This package uses host-rendered portable settings and an independent WinUI data directory. Legacy settings, credentials and model files are not imported automatically.

## Source and platform scope

Protocol/runtime sources and applicable fixtures were snapshotted from `plugins/TypeWhisper.Plugin.Reson8` at `4db8f6ac`, then adapted under `plugins-v2`. The package does not reference the legacy provider DLL or compile WPF settings views. Legacy sources, catalogs and published packages remain unchanged.

The macOS repository was compared at `ac00e39ea63e4789de8427d034d2085b3d898159`; it was not modified. Compared with macOS Reson8; Windows REST/WebSocket conventions, including ApiKey authorization, are retained.

## Build and verification

From this branch's repository root:

```powershell
dotnet msbuild plugins-v2/TypeWhisper.Plugin.Reson8/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins-v2/TypeWhisper.Plugin.Reson8/Tests -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.reson8` inside the plugin project. Package that directory as the ZIP root.

51 plugin tests pass. Fake HTTP, normalization/persistence, model discovery, real local WebSocket finalization/premature close and package lifecycle. All packages have isolated install, enable, restart, disable, uninstall and reinstall coverage through the real portable package loader and host services.

The ZIP was installed and loaded in the WinUI development profile, preserving existing installation receipts. No credentials were copied from the legacy profile.

The installed 1.2.2 package passed authenticated connection validation, prerecorded transcription and paced live PCM through the actual host StreamingDictation implementation. Live updates arrived during capture and the complete last sentence arrived after finalization, without batch fallback. Marco confirmed successful live microphone transcription in the development app with German selected. Public catalog publication and production-profile migration are pending.

Source 1.2.3 preserves custom models after failed refreshes, resets account models on credential changes, honors progress cancellation, preserves realtime proxy paths and includes the logo in build/publish output. The development microphone test and screenshots use installed 1.2.2.

Source 1.2.4 also resets endpoint-specific models when changing servers, while preserving them for equivalent normalized URLs.

Source 1.2.5 validates connection settings before saving, persists a safe model selection before changing credentials and propagates receive callback failures immediately.

Source 1.2.6 restores model settings when credential persistence fails and rejects unsupported or truncated WAV input before uploading.
