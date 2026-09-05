# Work in progress: WinUI design and application migration

## Summary

Preserve the approved Windows UI prototype and progressively connect it to the existing application services. This is a draft checkpoint, **not ready to merge or ship**. The WPF host remains available. UI completion does not mean production functionality is connected.

## UI and interaction checklist

- [x] Preserve the approved prototype and its isolated model/check projects.
- [x] Adopt the prototype's views, fonts, icons and styling into an independent WinUI host under `src`.
- [x] Preserve compact Quick Launch, navigation and search layout.
- [x] Preserve designed history, recorder, workflows, plugins, marketplace, file transcription, dictionary and snippets surfaces.
- [x] Preserve categorized settings, shortcut editor, onboarding wizard, dashboard and statistics surfaces.
- [x] Preserve overlay variants and appearance controls as UI previews.
- [x] Add a Mac-inspired tray menu with compact typography/icons and disabled unconnected actions.
- [x] Fix the empty first tray-menu opening; owner confirmed the corrected behavior and styling.
- [x] Add foreground activation and repeated-hotkey visibility toggling; owner confirmed toggling.
- [ ] Complete keyboard, screen-reader, submenu and focus-restoration walkthroughs.
- [ ] Verify actual 200% DPI and mixed-DPI multi-monitor transitions throughout the application.

## Service integration checklist

- [x] Add a Core-only Presentation boundary for read-only history queries.
- [x] Read isolated local history through the existing HistoryService, without importing personal history.
- [x] Replace sample history with loading, empty, search-empty and retryable error states.
- [x] Preserve existing persistence schema; keep unknown device/kind metadata unknown.
- [x] Add opt-in strict history read errors without changing existing callers' default behavior.
- [x] Register configurable global Quick Launch shortcuts, support alternatives, preserve previous registrations on conflicts, and save in isolated development storage.
- [x] Prepare an optional SDK contract for plugin-contributed tray and Quick Launch commands.
- [ ] Validate populated history, detail/copy behavior and live refresh end-to-end.
- [ ] Connect real microphone capture, transcription engine/model selection and text insertion into the foreground application.
- [ ] Save real dictation results with timestamps and engine/model metadata; verify dictation-to-history during daily use.
- [ ] Connect dictation modes, remaining hotkeys, live transcript overlay, cancellation and recovery.
- [ ] Connect recorder audio capture, source toggles, playback and persisted history entries.
- [ ] Connect general settings persistence, native services and model downloads/activation.
- [ ] Connect workflows, file transcription, dictionary and snippets to real services.
- [ ] Connect plugin discovery/lifecycle and the shared command registry to both tray and Quick Launch.
- [ ] Handle plugin capability changes, unload, command identity, disabled state, cancellation and failures.
- [ ] Connect real dashboard/statistics data, onboarding checks, licensing and updates.
- [ ] Implement and validate sync/backup compatibility and recovery; current screens remain previews.

## Migration and release checklist

- [ ] Finish maintainable service/view-model boundaries and retire prototype-only dispatch/mock code incrementally without redesigning the UI.
- [ ] Save/review the companion `typewhisper-dev-tools` launcher change in its own repository; it is currently a local dependency and is **not included in this PR**.
- [ ] Decide supported Windows versions: the new host currently targets build 26100, unlike the older Windows-10-capable host.
- [ ] Validate packaging, installation, settings/data migration and upgrade/rollback.
- [ ] Run the complete relevant test suite and review CI before marking ready.
- [ ] Replace the default shipping host only after real end-to-end validation.

## Validation at this checkpoint

- WinUI development host built, published and launched through the prescribed dev launcher.
- Presentation/integration tests: **11 passed**.
- Existing Core HistoryService tests: **11 passed**.
- Plugin SDK build: **0 warnings, 0 errors**.
- Interactive owner feedback confirmed tray appearance/first opening and Quick Launch hotkey toggling.
- Full CI, populated-history walkthroughs, real dictation and comprehensive DPI/accessibility testing remain pending.

No personal history or audio has been imported or deleted. Third-party dictation does not automatically appear in TypeWhisper history. See `docs/WINUI-MIGRATION.md` for implementation boundaries and known limitations.
