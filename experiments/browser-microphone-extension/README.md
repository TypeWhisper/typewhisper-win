# Browser microphone extension proof of concept

This directory is a research artifact for TypeWhisper Windows issue #304. It is not a production extension and is not included in TypeWhisper release builds.

## What the spike proves

- A focus-triggered microphone control can be rendered without changing page layout.
- A closed Shadow DOM isolates the control from normal page styles and scripts.
- Pointer handling can keep the original field focused when the control is clicked.
- A normal `input`, `textarea`, or `contenteditable` selection can be captured before dictation and replaced after processing.
- The session can stay bound to the tab, frame, field, and selection that started it.
- The privileged background context can expose only fixed start, stop, and cancel messages.

The included `demo.html` uses a mock bridge and covers a normal input, a textarea, and a contenteditable field. Run the field adapter tests with:

```powershell
node --test experiments/browser-microphone-extension/tests/field-target.test.cjs
```

## Deliberate security boundary

The extension requests `nativeMessaging` and does not request loopback HTTP access. A production bridge should be installed by TypeWhisper, allowlist the exact Chrome and Edge extension IDs, read the protected local API discovery state itself, and expose only fixed protocol commands.

The API token must never enter a content script. Content scripts share the page DOM and must be treated as less trusted than the service worker and native bridge. The service worker must not accept an arbitrary URL or API path from a content script.

The current repository does not include the native host. Without it, an unpacked build correctly reports that the TypeWhisper browser bridge is unavailable.

## Required TypeWhisper application change

The current `/v1/dictation/start` flow follows normal desktop output behavior. It can paste or copy the completed text before an extension receives the result. A browser integration therefore needs an authenticated, per-session `return_only` delivery mode. That mode must complete history and API-session persistence but skip desktop paste, clipboard mutation, action-plugin delivery, and `TextInsertedEvent` publication.

Do not point this proof of concept directly at the current dictation endpoint. Doing so can cause duplicate insertion and exposes a weaker browser-to-loopback security boundary.

## Known compatibility limits

- Direct DOM insertion is suitable for standard input, textarea, and basic contenteditable fields.
- Framework-controlled editors can reject or overwrite direct DOM changes. Production compatibility needs editor-specific tests and may require `beforeinput`, browser editing commands, or documented exclusions.
- A cloned contenteditable range can become invalid if the page replaces its editor DOM while dictation runs. The proof of concept fails closed instead of inserting elsewhere.
- Cross-origin iframes require matching host access and their own content-script frame.
- Browser internal pages, extension stores, PDF viewers, and other protected pages do not permit normal content-script injection.
- A page can remove the injected host element or observe the final value and input event. Shadow DOM isolates implementation details, not the page's own field data.

## Production follow-up boundaries

1. Add and test the authenticated `return_only` API delivery mode.
2. Implement a small signed native messaging bridge with length-bounded messages and a fixed command allowlist.
3. Register separate allowlisted host manifests for Chrome and Edge extension IDs during installation.
4. Add explicit user approval in TypeWhisper before enabling browser integration.
5. Decide between all-sites access, optional site permissions, and click-to-enable behavior.
6. Add session ownership, navigation cancellation, tab-close cancellation, and stale-result rejection.
7. Validate Gmail, Google Docs, Microsoft 365, Slack, Notion, common React editors, and iframe-hosted editors separately.
8. Package and review Chrome and Edge builds independently. Document Firefox native-host manifest differences before porting.
