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
- [ ] Complete WinUI localization: reuse the existing Windows app's translations and replace hard-coded UI text, tooltips, accessibility labels, errors and status messages with language resources.
- [ ] Connect and persist UI language selection with system-language fallback; verify language switching, locale-aware dates/numbers, missing-translation fallback and layouts with longer translations across all supported languages.

## Service integration checklist

- [x] Add a Core-only Presentation boundary for read-only history queries.
- [x] Read isolated local history through the existing HistoryService, without importing personal history.
- [x] Replace sample history with loading, empty, search-empty and retryable error states.
- [x] Preserve existing persistence schema; keep unknown device/kind metadata unknown.
- [x] Add opt-in strict history read errors without changing existing callers' default behavior.
- [x] Register configurable global Quick Launch shortcuts, support alternatives, preserve previous registrations on conflicts, and save in isolated development storage.
- [x] Prepare an optional SDK contract for plugin-contributed tray and Quick Launch commands.
- [ ] Validate populated history, detail/copy behavior and live refresh end-to-end.
- [x] Connect microphone capture, local Parakeet batch transcription and foreground text insertion; owner confirmed a real dictation round-trip.
- [x] Persist dictation results with timestamps and engine/model metadata before insertion.
- [x] Connect and persist Main Dictation shortcuts from settings/setup, including modifier-only chords and alternatives.
- [x] Implement hybrid Main Dictation: immediate key-down start, tap-to-toggle and hold-to-talk; owner confirmed all three behaviors.
- [x] Prepare the microphone without starting capture; remove the 300-ms capture-start delay.
- [x] Use capture RMS for the recording overlay and expose real recording/model status in the tray.
- [x] Replace character-by-character insertion with one Ctrl+V gesture; owner confirmed whole-text insertion.
- [x] Reuse native clipboard snapshots with strict WinUI preservation, sequence-guarded restore and no automatic paste retry.
- [x] Reproduce and fix Explorer FileContents blocking dictation insertion; owner confirmed dictation into Notepad with files still copied.
- [x] Add synthetic PCM and local generated-speech regression tests through the capture service and Parakeet; reproduce loss of the first word with a simulated 300-ms delay.
- [ ] Validate real microphone/driver latency and native keyboard-hook timing with a physical or virtual input end-to-end.
- [ ] Validate bitmap/HTML/file clipboard round-trips and slow target applications end-to-end.
- [ ] Verify populated dictation history, detail/copy behavior and live refresh during daily use.
- [ ] Connect configurable transcription model/device selection.
- [x] Connect the Audio microphone picker to real capture device preferences and isolated persistence; reject changes during recording.
- [x] Wire persisted recording sounds/output selection, whisper mode, output-volume reduction and legacy media Play/Pause behavior into the WinUI dictation lifecycle.
- [x] Use one shared audio output preference for feedback and volume reduction; future TTS must consume the same preference rather than add a separate device selector.
- [ ] Verify selected-output sounds, volume restoration, cancellation and unplug scenarios on real audio devices. Media pause currently uses the legacy toggle key and can start already-paused media.
- [x] Connect persisted energy-based silence auto-stop through the normal stop/transcribe/insert lifecycle, with configurable timeout and modifier-held protection.
- [ ] Validate silence auto-stop with real speech, quiet microphones, background noise and physical hybrid hotkeys; current detector uses RMS energy, not semantic voice activity detection.
- [ ] Connect spoken-feedback providers/voices; this control remains explicitly disabled until connected.
- [ ] Validate microphone switching, unplug/replug fallback and restart persistence with real devices. Model switching and forced-language configuration are not connected; Dictation now shows the actual Parakeet/automatic configuration instead of sample choices.
- [ ] Connect remaining dictation modes/hotkeys, live transcription, complete cancellation and durable recovery.
- [x] Wire actual Parakeet preview text into the WinUI transcript window using the legacy local polling approach, with the preview delay reduced to 1.5 seconds plus inference time. Drain preview inference before final decode and reject cancelled results.
- [ ] Validate real live-preview latency, long recordings and physical hotkey cancellation. Polling is not token streaming and shares the final recognizer.
- [x] Add an experimental optional Parakeet CTC plugin and wire it after final WinUI decoding with audio, token timings and dictionary/Term Pack hints. Publish it as a separate portable package. Persist opt-in under Dictation > Advanced; skip the legacy text booster while CTC is selected and retain explicit corrections afterward.
- [x] Validate actual English CTC emissions with official fixture audio, a positive correction (`live` to `love`) and rejection of the inverse incorrect hint. Activate the published package through the portable loader and repeat the positive control successfully.
- [ ] Validate real microphone/TDT-token alignment, WinUI enable/disable/restart interaction and German/name/TypeWhisper vocabulary accuracy. The English-trained 110M model, managed features and approximate BPE tokenizer are experimental, not a Mac/FluidAudio parity claim. Calibrate variable-length acoustic scoring before recommending general use.
- [x] Add a portable net10.0 SDK target alongside the legacy WPF target and an acoustic vocabulary-rescoring contract (audio, token timings, term hints, recording identity and replacement spans).
- [x] Exercise a separate portable test-plugin assembly: **6 contract tests passed** for framework independence, lifecycle/settings, request data, cancellation and explicit errors. This is not CTC inference or WinUI plugin discovery.
- [x] Add an independently tested portable package loader using the existing Windows `manifest.json` convention, shared SDK identity and collectible assembly contexts. The portable suite now has **21 passing tests**, including actual separate-assembly activation/unload and host-side vocabulary result validation. This loader is not yet connected to the WinUI Plugins screen.
- [x] Extend portable host coverage to **27 passing tests** with serialized vocabulary-plugin activation, disable/dispose draining, stale-result rejection, activation retry and cancellation. Prepare normalized dictionary catalogs off the UI thread and run final dictionary processing on a worker. These remain distinct from actual CTC inference and WinUI plugin discovery.
- [x] Connect personal dictionary entries, corrections and the 12 existing built-in Term Packs to isolated development persistence. Keep per-pack ownership so disabling a pack preserves personal terms and overlapping terms from other packs. Commercial/remote packs and snippet expansion remain unconnected.
- [x] Reuse the existing Windows text-based vocabulary algorithm and correction rules after final Parakeet decoding. Preserve raw text separately in History; use a per-recording read-only dictionary snapshot so processing cannot overwrite newer edits. Persist opt-in vocabulary boosting under Dictation > Advanced. This is not acoustic CTC rescoring.
- [ ] Validate full WPF compatibility and general plugin discovery/install UI. Portable package activation, result validation, lifecycle and real CTC controls are tested independently; complete WinUI acceptance remains pending. The portable and WPF SDK targets are not a promise that legacy WPF plugins load unchanged in WinUI.
- [ ] Connect recorder audio capture, source toggles, playback and persisted history entries.
- [ ] Connect general settings persistence, native services and model downloads/activation.
- [ ] Connect workflows, file transcription and snippet expansion to real services; add licensed remote dictionary packs after license integration.
- [ ] Connect plugin discovery/lifecycle and the shared command registry to both tray and Quick Launch.
- [ ] Handle plugin capability changes, unload, command identity, disabled state, cancellation and failures.
- [ ] Connect real dashboard/statistics data, onboarding checks, licensing and updates.
- [ ] Implement and validate sync/backup compatibility and recovery; current screens remain previews.

## Migration and release checklist

- [ ] At the end of the migration, run a controlled legacy-versus-WinUI benchmark and add methodology/results to PR #446. Deferred at the owner's request. Match audio/model/backend/thread settings, separate warm-up from steady-state inference, compare text quality, and measure hotkey/live-preview/history/paste separately from the file API. The prepared local probe is in `tools/TypeWhisper.Benchmarks`; API versus standalone decoder timings are not an end-to-end app comparison.
- [ ] Finish maintainable service/view-model boundaries and retire prototype-only dispatch/mock code incrementally without redesigning the UI.
- [ ] Save/review the companion `typewhisper-dev-tools` launcher change in its own repository; it is currently a local dependency and is **not included in this PR**.
- [ ] Decide supported Windows versions: the new host currently targets build 26100, unlike the older Windows-10-capable host.
- [ ] Validate packaging, installation, settings/data migration and upgrade/rollback.
- [ ] Run the complete relevant test suite and review CI before marking ready.
- [ ] Replace the default shipping host only after real end-to-end validation.

## Validation at this checkpoint

- Owner confirmed real Windows dictation of TypeWhisper with Strong and Auto. The diagnostic journal recorded an applied CTC replacement; the neutral sentence “Ich benutze diese App jeden Tag” remained unchanged. This is one successful live positive/negative pair, not comprehensive German accuracy validation. Latest portable suite: **43 passed**; Presentation suite: **73 passed**. WinUI was built, published and relaunched through the prescribed launcher, with CTC initialization succeeding.

- Align the core CTC comparison with the Mac's pinned FluidAudio 0.15.5: original BPE merge ranks/lowercase/NFKC, per-token normalized scores and adaptive context bonus (base 4.5). Auto now uses vocabulary-size thresholds of 52–60%; per-term presets are unchanged. Official tokenizer IDs match the installed ONNX vocabulary. Real audio positive/negative controls still pass. Full candidate/rescue heuristics and numeric model/frontend parity remain unported/unverified; this is not a full Mac-equivalence claim.

- Reproduced zero-length CTC intervals at the reported 0.64-second timestamp with positive durations that round away during addition: three new cases failed before the fix. Validate the computed end and fall back to the next distinct timestamp/audio boundary rather than dropping a piece. This addresses interval validity; the real TypeWhisper correction still requires live confirmation.

- Preserve transducer tokens sharing a timestamp instead of dropping zero-duration pieces; **37 portable tests passed**, including shared-frame/final-frame and invalid-time regressions. The live TypeWhisper case still needs confirmation with the diagnostic journal.
- Add persisted per-term CTC similarity controls matching the Mac editor: Auto (engine default), Strong 0.50, Balanced 0.65, Precise 0.80, Advanced 0.40–0.95. The current Windows engine default remains 0.80; these are candidate thresholds, not acoustic bonuses. WinUI builds and launches; visual and keyboard acceptance of the new editor controls remains pending.

- CTC diagnostics: **34 portable tests passed**, including bounded/rotating local logs and I/O failure isolation. Real fixture inference verifies accepted/rejected candidate scores and an invalid-timing skip without transcript text. The development host writes `PluginData/com.typewhisper.parakeet-ctc/ctc-diagnostics.jsonl` with recording IDs, input counts, alignment/skip reasons and acoustic scores; one rotated file is retained. No additional audio or transcript is stored. The reported TypeWhisper correction failure still needs a new real dictation with diagnostics enabled.

- Experimental CTC update: **32 portable SDK/host/CTC tests passed**, **68 Presentation tests passed**, and **74 Core tests passed** with the Dictionary/VocabularyBoost name filter. This broader Core filter is not directly comparable to the earlier 68-test subset.
- Official English audio decoded to `I love you.` with finite CTC emissions; positive and negative acoustic controls passed. The actual published plugin package also loaded and passed the positive control. Fixture timings span the whole utterance, so this is not yet a real TDT-timing or microphone acceptance test. See `tests/TypeWhisper.ParakeetCtc.Probe/README.md`.
- The verified CTC model archive and extracted files reside only in the isolated local development profile; model weights are not in Git. CTC is opt-in and disabled by default. The compact native-switch presentation follows the existing settings design.

- WinUI development host built, published and launched through the prescribed dev launcher.
- Presentation/history/hotkey/paste-coordinator/overlay/audio-effects/silence/live-preview/dictionary tests: **68 passed**. Audio-effects tests use mocks, not actual output-volume or media changes. Silence tests use injected elapsed time and RMS levels, not real microphone input. Preview tests use a fake decoder to verify publication, cancellation/draining and error isolation. Dictionary tests cover pack ownership/reload, malformed files, opt-in matching, corrections and concurrent UI edits without stale writes.
- Existing Core dictionary and vocabulary-boosting regression tests: **68 passed** after extracting reusable read-only snapshot entry points; the original matching algorithm is unchanged.
- Legacy Windows SDK target (`net10.0-windows`) builds with **0 warnings and 0 errors**. Full WPF application/plugin compatibility remains unvalidated.
- Visually inspected the real Dictionary and Term Packs surfaces in the WinUI development host. The stored personal term TypeWhisper is visible. Real fixture-based CTC inference is now validated separately; real plugin-driven microphone dictation remains pending.
- Clipboard format-policy regression tests: **5 passed**. The exact current clipboard snapshot failed before the fix and passed afterward without changing its sequence number.
- The FileContents exception requires an already captured nonempty CF_HDROP; unavailable virtual-file-only, bitmap and unknown representations remain protected.
- Added native clipboard round-trip fixtures; the expanded isolated suite has not run here because private window-station creation is denied. Do not count it as passed. Full slow-target and restored-file-paste coverage remains pending.
- Isolated audio regression tests: **3 passed**, repeated successfully with the local Parakeet model and Microsoft David Desktop speech synthesis.
- Immediate audio: `Bananas are yellow. This is a test of immediate recording.` Simulated 300-ms delay: `are yellow. This is a test of immediate recording.` Exactly 4,800 samples are lost in the delayed control.
- Audio tests use in-memory synthetic speech and a replay input, not a real microphone or clipboard. See `tests/TypeWhisper.Dictation.AudioTests/README.md` for reproduction and prerequisites.
- Existing Core HistoryService tests: **11 passed**.
- Plugin SDK build: **0 warnings, 0 errors**.
- Interactive owner feedback confirmed tray appearance/first opening, Quick Launch toggling, immediate dictation start, hybrid hold/toggle and whole-text insertion.
- Full CI, populated-history walkthroughs, real-device latency, rich clipboard round-trips and comprehensive DPI/accessibility testing remain pending.

No personal history or audio has been imported or deleted. Third-party dictation does not automatically appear in TypeWhisper history. See `docs/WINUI-MIGRATION.md` for implementation boundaries and known limitations.
