# Webhook portable plugin

Configurable POST/PUT post-processing webhooks with workflow filters, secret headers and a sanitized recent-delivery log.

Version `1.3.4`; plugin ID `com.typewhisper.webhook`; minimum host `1.1.2`.

## Setup

Add a destination with the plus button, enter its HTTPS URL (HTTP only for loopback), and use **Send test message** before enabling automatic delivery. Save the destination's name, URL, method, workflow filter, enable switch and optional authentication headers together with **Save profile**. The WinUI workflow field offers checkboxes for existing workflows instead of free text, with an explicit **All workflows** choice (including dictation without a workflow). Missing saved workflow names remain visible as unavailable. Clearing the last selected workflow never implicitly broadens delivery to all. New destinations are disabled. Blank headers keep the existing secret; `{}` clears it. Test messages use the draft fields without saving or enabling the destination; saved credentials are never implicitly sent to a changed draft URL.

Destination URLs are stored encrypted like the headers, because chat and automation webhooks often carry their access token in the URL; the plugin settings file keeps the URL empty, so data exports and backups of that file do not contain it. URLs saved by versions before 1.3.4 move to encrypted storage when the plugin starts. If encrypted storage is unavailable, they stay in the settings file and the move is retried on the next start.

Each enabled destination receives dictations as JSON. **Recent deliveries** reports HTTP status without exposing transcript text, response bodies, URLs or headers. A failed request preserves the dictation. Automatic delivery shares one ten-second budget across all destinations; explicit sample tests have their own ten-second timeout and do not follow redirects. Delivery runs before text insertion, so a slow endpoint can delay insertion.

## Platform scope

Delivery occurs during text post-processing, before downstream paste success is known. Payload contains text, language, duration, workflow and timestamp; modelId is unavailable in this context. No automatic retry is performed.

## Build and verification

From the repository root:

```powershell
dotnet msbuild plugins/TypeWhisper.Plugin.Webhook/portable.proj /t:Build /p:Configuration=Release
dotnet test plugins/TypeWhisper.Plugin.Webhook/Tests -c Release
```

The complete package is staged under `bin/Release/portable-host/Plugins/com.typewhisper.webhook` inside the plugin project. Package that directory as the ZIP root.

Tests cover payload, headers and workflow filters; cancellation and the shared delivery budget; disabled destinations; HTTP failures and log redaction; draft test isolation; atomic saves; encrypted URL storage and its migration; header validation, retention and clearing; restart and package lifecycle.

See the [settings screenshots](../../docs/screenshots/webhook/). Validate draft test messages and selected-workflow persistence in an isolated profile before enabling automatic delivery.
