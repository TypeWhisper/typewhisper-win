# What's new window

Native screenshots captured on Windows on 2026-10-18 from the changes introducing
`WhatsNewWindow` and manual reopening under **Account & about**. The Debug build
was launched with the Windows development helper from this checkout, using the
normal development profile, English interface and dark theme. The window shows
content version 1; the build reports `0.0.1-dev`.

- [Feature highlights](dialog-dark.png): borderless window, custom close button,
  three highlights and skippable footer.
- [About entry](about-dark.png): persistent access below the app identity.

Native checks covered settings search, repeated manual opening, closing via
Escape and the custom X, and dragging the upper area. A busy manual request
showed the inline explanation without opening the news window.

Separate named profiles exercised automatic presentation, persistence across
restart and immediate dismissal on a dictation shortcut. That shortcut check
did not use a configured model or microphone; end-to-end capture and insertion
target preservation still require a controlled microphone test.

The Release application headless suite passed 4,357 tests, with five optional
audio tests skipped. The final borderless UI was rebuilt and checked natively
after that suite. See the [testing guide](../../../TESTING_GUIDE.md#whats-new-after-an-update)
for startup fixtures and the remaining acceptance scenarios.
