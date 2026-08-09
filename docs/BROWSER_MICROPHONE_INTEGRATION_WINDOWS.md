# Browser microphone integration on Windows

Status: research outcome for issue #304. This document records a recommended architecture and a tested browser proof of concept. It is not a release commitment.

## Recommendation

Use one cross-platform WebExtension with a small platform-native bridge. Start with Chrome and Edge on Windows. Add Firefox after the native-host packaging and browser-specific compatibility work are understood.

The production data path should be:

1. An isolated content script observes the focused editable field and renders a microphone control in a closed Shadow DOM.
2. The content script sends only fixed start, stop, and cancel messages to the extension service worker.
3. The service worker forwards those commands to a signed TypeWhisper Native Messaging host.
4. The native host reads the protected local discovery and authentication state, then starts an authenticated TypeWhisper `return_only` dictation session.
5. The final result returns to the initiating tab, frame, and field. The content script inserts it at the captured selection only if the original target is still valid.

Do not make a native overlay window the primary browser UI. Keeping a separate Windows window aligned with a browser field across scrolling, zoom, multiple monitors, cross-process accessibility boundaries, and browser layout changes would be less reliable than a control rendered inside the page.

## Current TypeWhisper API findings

The local HTTP API already exposes `/v1/dictation/start`, `/v1/dictation/stop`, status, and transcription polling. It listens on loopback. Authentication can be enabled, and the discovery token is protected at rest for the current Windows user.

The current API dictation path still uses normal desktop delivery after transcription. Depending on settings, that can paste into the active application, mutate the clipboard, deliver action-plugin output, and publish `TextInsertedEvent`. A browser extension that also inserts the returned text could therefore produce duplicate or misdirected output.

Before production integration, TypeWhisper needs an authenticated per-session `return_only` mode. It should preserve transcription history and API-session persistence while skipping all desktop output side effects.

The existing preflight response does not provide the CORS policy needed by an arbitrary page origin. Adding permissive localhost CORS would not solve the more important token and command-boundary concerns.

## Architecture comparison

| Approach | Result | Reason |
| --- | --- | --- |
| Content script calls loopback HTTP | Reject | Content-script requests follow the page origin and CORS rules. It would also put the API token in the least trusted extension context. |
| Service worker calls loopback HTTP with host permissions | Limited prototype only | It avoids page-origin CORS, but still expands localhost permissions and makes protected discovery and token handling part of the extension. |
| WebExtension plus Native Messaging bridge | Recommended | The native host can read protected local state and expose a small, versioned command protocol while the token never enters the page or content script. |
| Native overlay beside the browser field | Reject as primary UI | Cross-process positioning, focus, scroll, zoom, and accessibility behavior are fragile. |
| Separate full extension per operating system | Avoid initially | Field discovery and insertion are browser concerns and should be shared. Keep only installation and native transport platform-specific. |

Chrome documents that content scripts remain subject to the page origin for cross-origin requests, while an extension context can use declared host permissions. It also recommends validating messages from less trusted content-script contexts. See the official [cross-origin request guidance](https://developer.chrome.com/docs/extensions/develop/concepts/network-requests), [messaging guidance](https://developer.chrome.com/docs/extensions/develop/concepts/messaging), and [content-script isolation model](https://developer.chrome.com/docs/extensions/develop/concepts/content-scripts).

The Native Messaging design follows the documented browser boundary: Chrome or Edge launches an allowlisted native host and exchanges length-prefixed JSON messages over standard input and output. See the [Microsoft Edge Native Messaging documentation](https://learn.microsoft.com/en-us/microsoft-edge/extensions-chromium/developer-guide/native-messaging) and [MDN Native Messaging documentation](https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/Native_messaging).

## Security requirements

- Never expose the TypeWhisper API token to a page or content script.
- Install and remove the native host registration with TypeWhisper. Allowlist exact production extension IDs for each browser.
- Accept a small versioned protocol with fixed command names. Reject arbitrary URLs, API paths, executable names, and filesystem paths.
- Bound message lengths, validate every field, and reject unknown protocol versions.
- Bind each session to the initiating browser, extension, tab, frame, and opaque request ID.
- Permit only one active browser dictation session until concurrency semantics are deliberately defined.
- Cancel on navigation, tab close, extension disconnect, app shutdown, or timeout. Reject late results.
- Require explicit user approval before enabling browser integration. Make site access visible and revocable.
- Avoid placing transcript text in extension logs or persistent extension storage.

## Field and interaction behavior

The control appears only for supported focused fields. Pointer handling must not steal focus from the field. Starting dictation captures the field identity and current selection. Stopping dictation moves through processing and either inserts into that original target or fails closed.

Supported baseline targets are text-like `input` elements, `textarea`, and basic `contenteditable` regions. Password fields are always rejected. The control needs accessible labels, visible keyboard focus, clear ready, recording, processing, and error states, and reduced-motion support.

Insertion should dispatch the events expected by web applications and restore the caret after the inserted text. A result must never fall back to whichever field happens to be focused later.

## Browser compatibility

| Browser | First implementation | Native-host note |
| --- | --- | --- |
| Chrome on Windows | Yes | Registry-installed host manifest with the Chrome extension origin allowlisted. |
| Edge on Windows | Yes | Separate host registration and exact Edge extension origin allowlist. |
| Firefox on Windows | Later | Shared WebExtension logic is plausible, but host manifest keys and registration locations differ. Test separately. |

Browser internal pages, extension stores, built-in PDF viewers, and other protected pages do not allow ordinary content-script injection. Cross-origin frames need explicit access and a content script in the target frame.

## Proof of concept

The isolated spike lives in [`experiments/browser-microphone-extension`](../experiments/browser-microphone-extension/README.md). It is deliberately not part of a release build and does not include a native executable.

Automated Node tests cover:

- replacement of an input selection and dispatch of an `input` event;
- rejection of password and disconnected targets;
- replacement of a contenteditable range with caret restoration.

A real-browser smoke test also verified both paths:

- A selected range in a normal input was replaced after a mock start and stop cycle. Focus stayed on the original input and the caret moved to the end of the inserted text.
- A selection nested in a `strong` element inside a contenteditable region was replaced. The surrounding rich-text structure, editable focus, and final caret were preserved.

The spike uses a mock bridge for the demo. Loading the unpacked extension without a TypeWhisper native host should report that the bridge is unavailable.

## Known editor limits

Framework-controlled editors can overwrite direct DOM mutations. Complex products such as Google Docs, Microsoft 365, Gmail, Notion, Slack, and editors built on ProseMirror, Slate, Lexical, or CodeMirror need explicit compatibility tests. Some may need `beforeinput`, editor-specific adapters, or a documented unsupported state.

A cloned contenteditable range is invalid if a page replaces the editor DOM while dictation is running. The safe behavior is to reject the stale result, not to insert it at a new focus location.

Shadow DOM protects the control's implementation from ordinary page styles. It cannot prevent the page from observing its own field value or removing the injected host element.

## Required implementation slices

1. Add authenticated `return_only` dictation sessions to TypeWhisper and test that paste, clipboard, action-plugin delivery, and `TextInsertedEvent` do not occur.
2. Define a versioned Native Messaging protocol and implement a minimal signed Windows host.
3. Add installer-managed Chrome and Edge registrations with exact extension allowlists and uninstall cleanup.
4. Turn the spike into a packaged extension with an explicit enablement and site-permission flow.
5. Add navigation, tab-close, disconnect, cancellation, stale-result, and timeout handling.
6. Run the editor compatibility matrix and classify supported, adapter-backed, and unsupported targets.
7. Package and review Chrome and Edge independently, then evaluate the Firefox host-manifest variant.

## Test plan

### TypeWhisper application

- Start, stop, cancel, and poll an authenticated `return_only` session.
- Verify history and API session persistence remain intact.
- Verify no clipboard read or write, paste input, action-plugin delivery, or `TextInsertedEvent` occurs.
- Reject missing, invalid, expired, cross-client, and replayed session identifiers.
- Verify normal desktop dictation remains unchanged.

### Native bridge

- Accept only the allowlisted extension IDs and protocol commands.
- Reject oversized, truncated, malformed, unknown-version, and arbitrary-path messages.
- Verify startup when TypeWhisper is running and a clear unavailable result when it is not.
- Verify disconnect, timeout, app exit, browser exit, and uninstall cleanup.
- Verify that tokens and transcript text do not appear in logs or extension storage.

### Extension

- Cover empty fields, selections, caret insertion, multiline text, IME composition, undo, and form-framework event handling.
- Cover input, textarea, simple contenteditable, nested formatting, iframes, shadow-root editors, and editor DOM replacement.
- Reject password, disabled, read-only, disconnected, protected-page, and stale-session targets.
- Preserve the initiating field through focus changes and never insert into a later field.
- Verify keyboard navigation, screen-reader labels, zoom, high contrast, reduced motion, and multiple display scales.
- Test explicit permission enablement, permission removal, private browsing policy, and extension update behavior.

### Compatibility matrix

- Chrome and Edge current stable versions on Windows 10 and Windows 11.
- Gmail, Google Docs, Microsoft 365, Slack, Notion, and representative React and iframe editors.
- Normal microphone completion, cancellation, app restart, browser restart, tab navigation, and network-backed transcription failure.

## Re-entry criteria

Production implementation should begin only after the `return_only` API contract, extension permission model, and signed native-host packaging approach are accepted. The proof of concept removes uncertainty around the browser field UX, but it does not remove the security and editor-compatibility work.
