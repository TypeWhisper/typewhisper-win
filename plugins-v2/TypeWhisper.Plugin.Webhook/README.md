# Webhook portable plugin

Configurable POST/PUT post-processing webhooks with workflow filters, secret headers and a sanitized recent-delivery log.

Version `1.3.0`; plugin ID `com.typewhisper.webhook`; minimum host `1.1.2`.
Independent branch: `seofood/webhook-portable`, updated to the current WinUI-only host.

## Setup

Add a destination with the plus button, enter its HTTPS URL (HTTP only for loopback), and use **Send test message** before enabling automatic delivery. Save the destination's name, URL, method, workflow filter, enable switch and optional authentication headers together with **Save profile**. New destinations are disabled. Blank headers keep the existing secret; `{}` clears it. Test messages use the draft fields without saving or enabling the destination; saved credentials are never implicitly sent to a changed draft URL.

Each enabled destination receives dictations as JSON. **Recent deliveries** reports HTTP status without exposing transcript text, response bodies, URLs or headers. A failed request preserves the dictation. Requests have a ten-second timeout per destination and do not follow redirects. Delivery runs before text insertion, so a slow endpoint can delay insertion.

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

15 plugin tests pass: payload, headers and workflow filters; cancellation; disabled destinations; HTTP failure and log redaction; draft test isolation; atomic saves when settings or secret storage fails; header validation; header retention/clearing; restart and package lifecycle. A synthetic message was sent through the actual plugin to an explicitly supplied Webhook.site endpoint and independently verified through its request API (HTTP 200, JSON text, language, workflow and timestamp). The running WinUI app's **Send test message** button was also exercised with Computer Use: the UI displayed HTTP 200 and the second request was independently found in Webhook.site. No real dictation or credentials were sent.

The ZIP was installed and loaded in the WinUI development profile, preserving existing installation receipts. No credentials were copied from the legacy profile.

The WinUI development host builds and launches through the shared development script. Current settings screenshots are in [`docs/screenshots/webhook/`](../../docs/screenshots/webhook/). Public catalog publication and production-profile migration are pending.
