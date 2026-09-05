# WinUI integration

## Decision and boundaries

Preserve the approved prototype UI and behavior 1:1. Its 62 top-level C#/XAML files were transferred to the new host, initially changing only the namespace. The rejected minimal ShellWindow was removed. App lifetime and tray navigation are the first integration changes. The original experiment remains unchanged. The existing WPF application remains the shipping host; no personal data is moved, rewritten or deleted.

Current seams: Core already owns IHistoryService and HistoryService; the WPF HistoryViewModel depends on ICollectionView, Dispatcher, dialogs and Windows-specific services. Presentation logic must not bring those dependencies into the new WinUI host.

## First slice (implemented)

TypeWhisper.Presentation targets plain net10.0 and references Core only. HistoryReader loads and filters existing records through IHistoryService, returns a read-only snapshot and exposes failures/cancellation to its caller. It performs no writes, opens no default user-data path and owns no background subscription. The future host must provide its configured service, refresh lifecycle and UI-thread dispatch.

This boundary is now connected to the adopted history view, reading `LocalAppData/TypeWhisper-WinUI-DevUserData/history.json`. Sample history is no longer seeded, and recorder previews are not inserted into this persisted view. Loading, empty, query-empty and retryable error states reuse the existing layout. Existing TranscriptionRecord persistence is unchanged; absent source-device and recording-kind metadata is displayed as unknown, not inferred from AudioFileName. The read-only adapter derives display IDs without rewriting stored opaque IDs. Audio playback and original audio metadata projection remain pending.

## Runnable host

Start through `F:\typewhisper\typewhisper-dev-tools\build-typewhisper-windows-dev.ps1 --run --winui <checkout>`.
The dev-tools script has an additive WinUI mode; its pre-existing local edits are preserved. Output is `F:\typewhisper\dev-output\typewhisper-win\WinUI`. The adopted UI still uses prototype sample/session data. No old history import is required (Marco's decision). No existing data is deleted. The host has no compile-time dependency on experiments.

Current surface: the approved prototype UI with real local history, global Quick Launch/Main Dictation hotkeys and local Parakeet dictation. Recorder, general settings persistence, sync, backups and other service operations still use previews. This is not a replacement-ready application. Prototype labels remain visible intentionally. Current host targets Windows build 26100; release compatibility must be decided before replacing the current Windows-10-capable host.

Publish must include App.xbf and TypeWhisper.WinUI.pri. A first startup failed because publish omitted these resources; the project now explicitly publishes them and the launcher validates their presence. WinUI opts into HistoryService.ThrowOnLoadFailure, preserving the previous behavior for existing callers. Missing files remain empty; corrupt files propagate to the retry UI without rewrites.

History integration tests cover missing-file/no-write, corrupt-file/no-rewrite/retry and metadata projection. Dictation results now save through the same history service before insertion. Populated-history UI still needs interactive inspection; third-party dictation does not automatically appear in this history.

Validation: published and launched through the prescribed script. The current Presentation suite has 33 passing tests; three isolated audio tests also pass. Eleven existing Core HistoryService tests passed at the earlier checkpoint. Owner feedback confirms immediate dictation, hybrid hold/toggle and whole-text insertion. Old personal history/audio were not imported. Full service integration, DPI transitions and packaging remain open.

## Clipboard insertion update

WinUI now sends one Ctrl+V gesture for the complete transcript, using the existing native clipboard transaction with a WinUI owner-window adapter. The WinUI build uses strict snapshot preservation: unsupported or unmaterializable formats abort before replacement instead of falling back to an empty snapshot or silently dropping advertised bitmap/file formats. Restoration checks the original lease's sequence under the clipboard lock and leaves newer clipboard contents alone. Temporary transcript data is marked for clipboard-history exclusion. There is no automatic retry or Enter submission.

The paste coordinator has automated tests for whole-text insertion, snapshot failure, focus/ownership changes, partial input and cleanup after failure. Real bitmap/HTML/file clipboard round-trips and target-application consumption remain manual validation items. Sending Ctrl+V is not proof that a target consumed the text; the current restore delay is 500 ms.

## Initial local dictation slice

The host loads existing Parakeet files read-only from the legacy development model directory. Main Dictation shortcuts are configurable and persisted separately in `dictation-hotkeys.txt`. Capture begins on key-down; the 300-ms threshold determines only whether release keeps recording (tap) or stops it (hold). Extra keys cancel speculative capture. The device is prepared without recording at startup. The recording overlay uses capture RMS. Results are persisted to isolated WinUI history before clipboard insertion. Insertion is skipped when the foreground window changed or modifiers remain held. No recording starts without a user gesture and no Enter key is sent.

This is an integration slice, not completed dictation parity: model/device selection is not wired to settings, the target guard tracks the top-level window rather than the exact editor field, and complete cancellation, recovery and live transcription remain pending. Keep the target text field focused. User-triggered capture/transcription/insertion is confirmed by owner testing. Synthetic audio reproduces the old first-word loss and verifies immediate capture; native device/hook latency remains a separate validation item. See `tests/TypeWhisper.Dictation.AudioTests/README.md`.

## Tray and plugin contributions

Quick Launch now shares one activation path for startup, tray and registered hotkeys: restore/show, temporarily topmost, request foreground, focus search; deactivation removes topmost. Settings → Shortcuts contains real global Quick Launch bindings, including alternatives. Bindings are stored separately in LocalAppData/TypeWhisper-WinUI-DevUserData/quick-launch-hotkeys.txt. New registrations are acquired before old ones are released; OS conflicts preserve previous bindings. No silent alternate hotkey is chosen. Other shortcuts remain previews. Cross-application focus and persisted hotkey editing still require interactive validation. Foreground behavior follows https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow and registration follows https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerhotkey; Windows foreground restrictions still apply.

The tray uses H.NotifyIcon.WinUI for the notification icon and a host-owned TrayMenuWindow with a dark MenuFlyoutPresenter and Mac-inspired sections. Closing hides the launcher; tray activation reopens it. Exit disposes the tray and exits the app. Only navigation and exit are connected; engine status and unavailable actions are explicitly marked. The library's SecondWindow mode was replaced after the user reported an empty first open: v2.4.1 measures before loading and its Loaded handler hides the initial flyout. The replacement waits for XAML loading before sizing/showing. Marco confirmed first-open behavior and approved the compact styling. Full keyboard, submenu, dismissal and monitor coverage remains pending. Marco also confirmed repeated Quick Launch hotkey presses now toggle visibility.

IPluginUserInterfaceProvider is an optional SDK contract modeled after the Mac PluginUserInterfaceProviding protocol. Plugins supply localized command descriptors separately for tray and Quick Launch, not native controls. Existing plugins need not implement it. This is a prepared contract only; plugin-manager discovery and both UI adapters are not wired yet.

The shared registry must scope IDs by plugin ID plus command ID, reject duplicates, group tray contributions by plugin, and expose plugin attribution in Quick Launch. NotifyCapabilitiesChanged must refresh both surfaces; unloading must remove contributions. Invocation must recheck enabled state/lifetime, handle async failures and cancellation, and prevent duplicate execution. Unknown semantic icons use a generic plugin fallback. Keep IActionPlugin's text-processing contract separate. Test these behaviors before connecting external plugins.

## Sequence

1. Add a separately runnable WinUI development host under src, sharing Core/Presentation and isolated development storage. Extend the prescribed dev build launcher before starting this host; keep WPF rollback available.
2. First complete vertical slice: Quick Launch → real History read/search/detail, explicit loading/error/empty states. Use isolated fixtures before opting into existing-data read access.
3. Introduce shared design resources and narrowly scoped view models. Port editing operations individually with service-level regression tests.
4. Connect settings, dictation/overlay/hotkeys, recorder and workflows through existing services; separate WPF-specific dispatch, dialogs and window ownership.
5. Integrate plugins, models, onboarding, statistics, licensing and updates. Sync/backup require separate compatibility and recovery tests; demo states must not masquerade as production capabilities.
6. Complete keyboard, real 200% DPI, monitor transitions, accessibility, packaging and upgrade/rollback tests before switching the default host.

## Validation

Run `dotnet test tests/TypeWhisper.Presentation.Tests/TypeWhisper.Presentation.Tests.csproj` for the first boundary. Local Windows application builds/launches use the prescribed `build-typewhisper-windows-dev.ps1 --run --winui <checkout>` route for the new host; the original WPF route is preserved.
