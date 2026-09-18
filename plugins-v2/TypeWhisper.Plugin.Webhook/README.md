# Webhook portable plugin

Configurable POST/PUT post-processing webhooks with workflow filters, secret headers and a sanitized recent-delivery log.

Version `1.2.0`; plugin ID `com.typewhisper.webhook`; minimum host `1.1.2`.
Independent branch: `seofood/webhook-portable`, based on `4db8f6ac`.

## Setup

Add an endpoint, configure its HTTPS URL (HTTP only for loopback), optional replacement headers JSON and workflow filter, then enable it. New endpoints are disabled. Blank headers keep the existing secret; {} clears it.

This package uses host-rendered portable settings and an independent WinUI data directory. Legacy settings, credentials and model files are not imported automatically.

## Source and platform scope

Protocol/runtime sources and applicable fixtures were snapshotted from `plugins/TypeWhisper.Plugin.Webhook` at `4db8f6ac`, then adapted under `plugins-v2`. The package does not reference the legacy provider DLL or compile WPF settings views. Legacy sources, catalogs and published packages remain unchanged.

The macOS repository was compared at `ac00e39ea63e4789de8427d034d2085b3d898159`; it was not modified. Uses the portable post-processing pipeline instead of the old event bus. Delivery occurs during text post-processing, before downstream paste success is known. Payload contains text, language, duration, workflow and timestamp; modelId is unavailable in this context. No automatic retry is performed.

## Build and verification

From this branch's repository root:

```powershell
dotnet msbuild plugins-v2/TypeWhisper.Plugin.Webhook/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins-v2/TypeWhisper.Plugin.Webhook/Tests -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.webhook` inside the plugin project. Package that directory as the ZIP root.

7 plugin tests pass. Fake HTTP payload/headers/filter handling, cancellation, error pass-through, secret settings and package lifecycle. No real endpoint was contacted. All packages have isolated install, enable, restart, disable, uninstall and reinstall coverage through the real portable package loader and host services.

The ZIP was installed and loaded in the WinUI development profile, preserving existing installation receipts. No credentials were copied from the legacy profile.

The shared portable SDK/host suite passed 259 tests on the Live Transcript host branch. Automated fixture tests do not replace authenticated provider, native model/device, microphone or visual UI acceptance. Public catalog publication and production-profile migration are pending.
