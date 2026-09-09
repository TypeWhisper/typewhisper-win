# Windows Premium and cloud compatibility

Reference: TypeWhisper macOS commit `5f04ae3d1910a252748ba53f26a6e13d5cf18851`.

## Premium overview

The Windows landing page follows `PremiumSettingsView.swift`, `PremiumFeatureOverview.swift`, and `.github/screenshots/premium*.png`: access summary, locked promotion, three tinted feature cards, and access/settings details opened on demand. Details use the existing Settings page instead of separate macOS windows. Meeting automation remains unavailable. Cloud folder sync is now connected to the Windows profile. License activation and correction learning retain their existing implementations.

## Apple account authentication

The Mac already uses a browser-based flow against `https://app.typewhisper.com` in `TypeWhisper/Services/Sync/CloudFolderSyncSettingsView.swift`:

1. Generate cryptographically random nonce and PKCE verifier. POST `nonceHash` (SHA-256 hex) and `codeChallenge` (SHA-256 base64url) to `/v1/auth/apple/web/start`.
2. Validate the returned `authorizationURL` (HTTPS, `appleid.apple.com`), `state`, and `expiresAt` before opening the system browser.
3. Accept only `typewhisper://premium-auth/callback`, match the pending state, reject errors, and require a nonempty code.
4. POST `state`, `code`, and `codeVerifier` to `/v1/auth/apple/web/exchange`.
5. Store the returned access token securely and verify the signed entitlement before granting access. The Mac verifies a pinned entitlement public key; an unverified JSON response must not grant Premium.

Related endpoints: `/v1/entitlements/current`, `/v1/entitlements/polar/device/attach`, `/v1/entitlements/polar/device/current`, and `/v1/account` (account deletion). Do not confuse local sign-out with account deletion.

Windows integration still needs protocol activation routing to the running dev/production app, cancellation and expiry handling, DPAPI token storage, signed entitlement verification, and account/licensing UI integration. Do not ship a sign-in button until this round trip is connected and tested. No Apple developer private key belongs in the Windows binary.

Apple supports this web flow: https://developer.apple.com/help/account/capabilities/configure-sign-in-with-apple-for-the-web

## iCloud and folder synchronization

The Mac uses an iCloud Drive ubiquity-container file mirror (`TypeWhisperICloudBridge/PremiumICloudBridgeService.swift`), not CloudKit database records. It mirrors the container Documents folder through a macOS XPC service. That native bridge cannot be copied into Windows.

The cross-platform route is the existing `CloudFolderSyncEngine`: select a locally available cloud folder and exchange the `typewhisper-sync` package. iCloud for Windows exposes iCloud Drive through File Explorer. Whether the app-specific Mac container appears there still needs a real-device check; a shared, user-selected folder supported by both apps is the fallback. An Apple account sign-in does not itself authorize or install iCloud Drive.

Apple reference: https://support.apple.com/en-us/118443

The Windows Core already implements dictionary/snippet operations, provider detection, conflict handling, and local store adapters. The WinUI Sync & backup view and Premium sync card now share controls for folder selection, enabling/pausing sync, and manual sync. Automatic sync checks every 15 seconds, including remote-only changes. A commercial license enables folder sync without requiring Apple account sign-in. The app persists per-folder progress, serializes runs, rechecks entitlement before publication, and cancels/drains on shutdown or profile restore. Local catalogs are captured before cloud I/O and compared again before commit; concurrent changes abort publication and retry on the next tick. No profile lock is held during cloud I/O.

## Compatibility validation started

Three unchanged Mac fixtures are checked into `tests/TypeWhisper.Core.Tests/Fixtures/PremiumSync`. Integration tests feed them through the actual Windows folder-sync engine in temporary directories. They cover correction import, term import, repeated-import idempotence, and legacy snippets without tags. The legacy fixture exposed a null tags collection; Windows now normalizes missing tags to an empty list, matching the Mac.

Additional tests now cover two persisted profiles, edits, deletions, restart/idempotence, concurrent local edits, cancellation, entitlement loss, malformed/missing catalogs, legacy identifiers, and acoustic `ctcMinSimilarity` preservation. Tombstones are retained to prevent a fresh device from resurrecting older upserts; distributed compaction is not implemented. This does not establish history synchronization, cloud placeholder hydration, or a live Apple-server Mac-to-Windows round trip. No user cloud folder or account was modified by these tests.
