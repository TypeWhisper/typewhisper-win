# Cloudflare Workers AI for the portable host

Independent .NET 10 package `com.typewhisper.cloudflare-asr`, version `1.1.10`, requiring host `1.1.2`. Provides original Whisper and language-aware Whisper Large V3 Turbo, with manual API tokens or browser sign-in.

## Behavior and macOS comparison

Preserves direct Cloudflare Workers AI Whisper requests and adds portable account/token configuration, token validation, response failure checks and validated account IDs.

The corresponding Mac sources were inspected at `ac00e39e` in `TypeWhisperPluginSDK/Plugins/`. Mac `CloudflareASRPlugin` is a configurable OpenAI-compatible ASR proxy, not the direct Workers AI backend used by Windows. That proxy is already configurable with the portable OpenAI-compatible provider; this package retains the distinct Workers account/token workflow. No translation or streaming is claimed.

Setup: **Connect with Cloudflare**, or enter a **Cloudflare API token and 32-character account ID**. Settings are rendered by the host in English/German. API keys use the host secret store, with a staged encrypted-key reference and one configuration commit. Failed writes keep the active configuration. Removing a key retains nonsecret preferences. No legacy credentials or settings are imported. Redirects are disabled and provider HTTP failures retain status/retry metadata. Opening the settings page sends no network request.

## Verification

```powershell
dotnet test plugins-v2/TypeWhisper.Plugin.CloudflareAsr/Tests/TypeWhisper.Plugin.CloudflareAsr.Portable.Tests.csproj -c Release
dotnet msbuild plugins-v2/TypeWhisper.Plugin.CloudflareAsr/portable.proj '-t:Build;CopyPackage' -p:Configuration=Release -p:PluginDestination=<staging-directory>
```

All **90 provider tests passed**. The provider test suite covers protocol requests/responses, HTTP errors, malformed JSON, cancellation, key persistence/failure/removal, host settings rendering and independent ZIP installation/configuration/restart/uninstall/reinstall through the immutable portable store. The resulting package contains only the provider DLL, dependency manifest and plugin manifest, with no WPF dependencies. The unchanged portable SDK/host baseline passed all 259 tests in the Gemini checkout.

On 2026-09-18, the initial 1.1.0 ZIP was installed in the Windows development profile and loaded with the real portable host services and Windows secret-store implementation. Settings were read successfully and the plugin was enabled. Existing unrelated package receipts were preserved. The WinUI development build and launch succeeded. No authenticated provider requests were sent. Native visual inspection was unavailable because the computer-use service could not connect.

The initial migration was accepted without authenticated calls on 2026-09-18. This historical limitation was closed on 2026-09-21: authenticated account validation and German transcription passed with manual and OAuth credentials, and Marco accepted Turbo dictation and browser sign-in in the app. ARM64 execution remains untested.

The host includes the Cloudflare logo. Shared draft editing and the single Save settings button are provided by host PR #479; provider-selection icons are provided by host PR #482. The portable package does not require either host UI change to load. Publish only to the separate V2 catalog with an immutable package URL; the legacy release workflow builds the WPF package and must not be used for this port.

Reference: [provider documentation](https://developers.cloudflare.com/workers-ai/models/whisper/).

Connection validation uses the account-scoped [Workers AI model search endpoint](https://developers.cloudflare.com/api/resources/ai/subresources/models/methods/list/) with no audio payload. The token must have Workers AI Read or Write access to the configured account. A general active-token check is not sufficient.

Whisper uses automatic language detection. Explicit language requests are rejected before audio is uploaded; the host exposes no fixed-language choices for the original Whisper model. Turbo supports explicit language selection. Protocol fixtures assert the complete model URL, POST method, bearer authentication, content type and unchanged WAV bytes.

The plugin uses a conservative 100,000,000-byte WAV upload limit, including headers, based on Cloudflare's [documented Free/Pro request-body ceiling](https://developers.cloudflare.com/support/troubleshooting/http-status-codes/4xx-client-error/error-413/). The host rejects oversized recordings before WAV allocation; direct plugin calls reject oversized byte arrays before HTTP. This baseline does not promise that all smaller inputs will be accepted by the model.

## Whisper Large V3 Turbo and language selection

Version 1.1.8 adds `@cf/openai/whisper-large-v3-turbo` alongside the existing Whisper model. Select Turbo under Dictation to choose a spoken language such as German. Requests explicitly use `task: transcribe`; dictionary terms are passed as a bounded initial prompt. Existing Whisper selections keep their automatic-language behavior. Live streaming and audio translation are not exposed.

Turbo streams base64 audio into JSON using a bounded encoding buffer and advertises a 74 MB WAV limit to allow for encoding overhead below the 100 MB request ceiling. Language and duration are read from `transcription_info`.

On 2026-09-21, authenticated account validation and a short German recording succeeded with both Cloudflare models. Marco confirmed that the existing model returns dictation in the app but reported inaccurate recognition in spontaneous German speech. A successful fixed sample does not establish the quality of free dictation; Marco subsequently tested Whisper Large V3 Turbo with German selected in the running app and confirmed substantially better recognition.

References: [Whisper](https://developers.cloudflare.com/workers-ai/models/whisper/), [Whisper Large V3 Turbo](https://developers.cloudflare.com/workers-ai/models/whisper-large-v3-turbo/).

## Browser sign-in (1.1.10)

Connect with Cloudflare uses Authorization Code with S256 PKCE and a loopback callback at `http://127.0.0.1:47831/callback/`. The public client ID is `f3792faca0b8263152f5581e9ff28e2c`; no client secret is embedded. The registered client must allow `authorization_code` and `refresh_token`, token authentication `none`, and scopes `ai.read`, `ai.write`, `account-settings.read`, and `offline_access` (the latter is enabled by the refresh grant).

The current client is private and can only be authorized by members of its Cloudflare account. Public distribution of browser sign-in requires the publisher to complete Cloudflare's client URL/domain verification and deliberately promote the client. API-token configuration continues to work independently.

The callback listener binds only to IPv4 loopback, checks the Host header, callback path and one-time state, and closes on completion or cancellation. The browser receives a static response without authorization codes or tokens. Token errors do not include provider response bodies. A three-minute sign-in timeout applies.

Access and refresh tokens are stored together in the host's encrypted secret store, with one atomic configuration reference update. Existing credentials remain active until sign-in and account discovery succeed. One account is selected automatically; multiple accounts require an explicit selection unless the existing selected account remains authorized. Refreshes are serialized and preserve a rotated refresh token. Caller cancellation is honored before refresh starts; once started, refresh has a separate 30-second timeout and saves returned tokens before propagating caller cancellation. Disconnect removes the local sign-in; users can revoke the Cloudflare grant under Connected Applications.

Validation: 90 automated tests cover the callback, cancellation, PKCE, rejected state and duplicate parameters, credential persistence failures, concurrent refresh and account selection. On 2026-09-21, Marco confirmed browser consent and the automatic connection in version 1.1.10. A subsequent test explicitly verified OAuth mode, authenticated account/model access, and German Turbo transcription with the saved OAuth credentials. Automatic refresh is covered by fixtures, not a forced live token rotation. No live token values are included in fixtures or logs.

References: [OAuth client registration](https://developers.cloudflare.com/fundamentals/oauth/create-an-oauth-client/), [OAuth endpoints](https://developers.cloudflare.com/fundamentals/oauth/integrate-with-cloudflare/).

Review validation: missing accounts retain an actionable sign-in message; expired credentials are distinguished from transient token-endpoint failures. Streaming upload fixtures verify exact base64 across chunk boundaries and cancellation. The reviewed upload implementation also passed authenticated OAuth account validation and German Turbo transcription against the live service.

