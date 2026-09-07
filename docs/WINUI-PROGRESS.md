# Windows 1.1 progress

Current implementation checkpoint: `3d80de1` (2026-09-07), including Recovery UI fix `108a67c`, Review action wiring `e1588aa`, persistent Cancel shortcut `b359c5a`, the initialized Review fixture `d333cb6`, plugin field layout `64dcf8d`, local spoken feedback `ff3c44b`, launcher status reporting `2879b6e`, portable model operations `bea6c08`, multiline field fixes `76c4505` and generic model UI/Session integration `3d80de1`. Release-wide draft: [PR #447](https://github.com/TypeWhisper/typewhisper-win/pull/447), `seofood/release-1.1` against `main`.

The authoritative feature inventory is now the [full comparison against both previous Windows and Mac](WINUI-FUNCTIONAL-STATUS.md). This replaces the accumulated, contradictory milestone checklist. Historical test counts and superseded UI decisions remain available in Git history; they are not current completion claims.

## Committed checkpoint and working-tree validation

The persisted Cancel shortcut (`b359c5a`) defaults to unassigned and includes generation guards, failed-save rollback and conflict checks, including modifier-only shortcuts. Native `Ctrl+Alt+F10` save, idle no-op and restart persistence passed. Registered `Ctrl+Shift+F9` was captured completely, and the conflicting choice was disabled after the fix. The focused suite passed 18 tests. At `d333cb6`, the Debug Review fixture opens after initialization.

Latest verified CI at `76c4505`: [headless run 34133514065](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34133514065) passed on [Windows](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34133514065/job/101779012872) and [Ubuntu](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34133514065/job/101779013415). All [CodeQL jobs](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34133515204) passed: [C#](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34133515204/job/101779020056), [JavaScript](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34133515204/job/101779020306) and [Python](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34133515204/job/101779020455). No suite counts are inferred from these statuses. CodeRabbit skipped draft review.

Commit `64dcf8d` makes plugin text fields single-line by default; Filler Words explicitly uses a multiline editor.

Native Filler Words checks confirmed uninstalling the old package through the UI: the installed list changed from four to three and its index entry was removed. The rebuilt package was explicitly reinstalled through the offline store and enabled. The multiline value `ähm`, `sozusagen`, `quasi` was saved with CR separators. Commit `76c4505` fixes TextBox initialization order, which previously restored only the first line, handles CR-separated entries in FillerWordFilter, and adds a visible input frame. Native frame visibility passed. Native restart now confirmed all three saved lines: `ähm`, `sozusagen`, `quasi`.

Commit `bea6c08` adds portable model-state reads, download and exact-snapshot selection under the package lease, plus the download controller (12 new Host cases and six controller cases). Generic model UI and Session wiring are committed in `3d80de1`: download and explicit load/selection use exact snapshots under package leases, with cancellation drain and no automatic selection after download. The isolated offline fixture has native coverage described below; it cannot transcribe audio. Local spoken feedback is committed in `ff3c44b`; native restart persistence passed, while automatic dictation readback acceptance remains pending.

The complete local suite passed **1,485 tests with one skip**: Core 522, Host 186, Presentation 640, Filler Words 62, Groq 32, Obsidian 18 and NVIDIA 25 with one skip. After the version-getter lock fix, the separate Host run passed 188 cases. These focused cases are not added to the full-suite total.

Native model-fixture checks used synthetic local markers, with no inference or cloud calls. Models A and B completed their short downloads. Explicit Use A was followed by a process restart: assets remained downloaded while provider readiness was false; Use A then loaded and activated the model. After explicitly resetting B’s known synthetic marker, a new download remained accessible across detail navigation and Settings reopen, with Progress and Cancel visible. Cancel left B’s marker absent and selected A persisted. Dictation disabled B and the prerequisite-blocked options while A remained ready; the unsatisfied-prerequisite Download button was disabled. Actual NVIDIA Canary with German was restored through Dictation afterward. The final native uninstall showed Cancel as the default before explicit confirmation. The fixture registration and enabled state were then false, the UI showed no matching plugin, and model A’s marker and selected-model preference were retained. History’s SHA-256 was unchanged. All mutations used isolated synthetic test data. Provider selection immediately after the initial A/B downloads was not directly observed. The prescribed Debug build/launch on 2026-09-07 passed. Repeating Start B → detail → Settings → Cancel displayed “Download canceled. Refresh model status before trying again; existing files are retained.” B’s marker remained absent. The final build also showed the restored NVIDIA Canary configuration ready in the launcher; German had been restored through Dictation beforehand.

Historically, the complete local suite before the CR-separator fix passed **1,479 tests with one skip**: Core 522, Host 183, Presentation 640, Filler Words 59, Groq 32, Obsidian 18 and NVIDIA 25 with one skip. After the fix, the focused Filler Words suite passed **62 tests**. These historical runs precede the complete post-fix suite above. The prescribed Debug build/launch on 2026-09-07 succeeded in the isolated `smoke-20260907-r3` profile. CI remains scoped to `76c4505`; it does not validate the later integration commit.

Native Review showed **Save to Obsidian** after initialization. Explicit execution created exactly one UTF-8 Markdown note from the visible final text in an isolated vault, displayed Completed with the filename, and Done closed Review. History and snippet-usage hashes were unchanged. This validates the new-note action, not daily append or other action providers.

Bounded native TTS checks confirmed default-off, completed Test voice playback with the Windows default and Microsoft Mark, and explicit Stop returning to stopped with Test enabled again. Microsoft Mark and opt-in true were saved in `audio.json`. After a fresh process start, Audio showed spoken feedback on and Microsoft Mark selected, with the corrected footer and no “not connected” claim. Restart persistence passed. Spoken feedback was then switched off again in the test profile. Automatic readback after real dictation remains unverified. No cloud TTS request was made.

## Current local checkpoint, 2026-09-07

Website, App and Global workflow matching use recording-start snapshots; History stores only the normalized browser host when permitted. Native Brave checks matched `example.com`, rejected a focused address bar and left `example.org` unmatched. Setup uses real configuration and retains the accepted choices after restart.

Fresh WinUI profiles bootstrap only NVIDIA/Groq. Other bundled packages, including Filler Words and Obsidian, require explicit installation and enablement. Filler Words is a portable processor with persistent settings, ordered dictation/file execution and minimal History provenance. Obsidian's portable new-note writer and the generic action registry/controller exist; Review action UI is now wired, with early shutdown drain and no hidden raw-text context; native Obsidian new-note acceptance passed; other action providers remain unverified.

Dictation recovery Retry now completes real Canary decoding and opens a copyable Review. The crash came from a workflow-local editor style referenced as a global resource; using the shared multiline style fixed it. Native Delete default Cancel preserved the audio; reopening Settings retained Recovery Review and a second Canary Retry succeeded. Explicit deletion removed only the synthetic audio, showed zero saved recordings and retained copyable Review text.

## Connected today

- Real hotkey microphone dictation through NVIDIA Parakeet/Canary or Groq, local live preview, persisted provider/model/language selection and history before whole-text paste.
- Main Dictation and Quick Launch shortcuts, microphone priority, sound/output preferences, whisper mode, media pause/ducking and silence auto-stop. Overlay configuration and provider configuration are separate from recording state.
- Dictionary terms/corrections, noncommercial built-in term packs, snippets with recording snapshots and automatic internal Parakeet CTC. Successful snippet usage is counted; portable text processors share the ordered pipeline; full text-pipeline parity remains open.
- Isolated history read/search/raw-final details/copy/edit, selected export/delete, confirmed clear-history and explicit retention and actual usage aggregation. Model, provider and app/task appear in details only.
- Integrations with Installed and Discover. NVIDIA/Groq bootstrap on first launch; explicitly installed portable transcription/LLM/text-processor packages use capability-based runtime bindings. Plugin settings own model downloads and credentials; Dictation selects provider/model. There is no global Models settings page or independent CTC integration.
- Persistent package installation/uninstallation, staged updates, checksums and extraction/identity validation. Plugin-owned output folders/tests and optional install/uninstall hooks with status messages. See [package contract](PLUGIN-PACKAGES-1.1.md).
- All eight workflow templates, explicit manual execution and Website/App/Global dictation rules with start snapshots and mandatory review on processing failure. Workflow shortcuts, selected-text capture and broader overrides remain open.
- Opt-in file queue restart recovery, a real recording library and persisted recorder source/output-device choices.
- Local backup export and reviewed category merge for dictionary, snippets, workflows and text History. All writers drain before publication and closing; startup recovery blocks profile access on failure. Device sync remains unavailable.
- Headless CI for shared Core, portable host, presentation and plugin-owned suites on Windows and Ubuntu.

## Remaining release work

These are major implementation areas, not final polish. Detailed per-feature gaps, reference sources, a 50-row grouped plugin inventory and acceptance criteria are in the full comparison.

- [x] Connect Review first / AutoPaste and history saving with persistence and delivery tests.
- [x] Connect recording mode, native task, number/punctuation/regional preferences and explicit retention.
- [ ] Complete remaining runtime settings; language hints already persist and are gated by explicit SDK capability. Make other preview controls unambiguous.
- [ ] Complete remaining plugin capabilities and job recovery; portable text processors, cancellation, file checkpoints and minimal processor provenance are connected. Complete broader recovery failure-path acceptance beyond the confirmed Retry/Copy, Settings reopen and Delete Cancel/confirm flow.
- [ ] Extend workflows with shortcuts, selected-text execution, remaining overrides and retry; verify live provider calls.
- [ ] Extend connected file decoding and microphone/system recording with durable dictation/capture jobs, pause/tracks, remaining formats and watch folders.
- [ ] Complete remaining history audio actions, correction learning, broader backup categories and sync; bulk mutation/export/clear, retention and statistics are connected.
- [ ] Generalize provider/settings/contribution integration and rebuild the selected additional plugins; publish and verify the single v2 catalog end to end.
- [ ] Complete real first-dictation acceptance beyond the verified setup/restart configuration, licensing, updates, production startup identity, localization and HTTP API/CLI. Bounded startup/redirect file activation queues files without starting transcription.
- [ ] Decide which Mac additions belong in Windows: Inbox/audio sync, meeting automation, live field text, media imports and platform-specific alternatives.
- [ ] Complete OS/architecture/distribution decisions, native accessibility/DPI/device/editor acceptance and the deferred legacy-versus-WinUI benchmark.

## Evidence

Previously recorded complete local headless run passed **1,432 tests with one skip**: Core 522, Host 171, Presentation 605, Filler Words 59, Groq 32, Obsidian 18 and NVIDIA 25 with one skip. [Headless CI at `0e2c4fc`](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34126461982) passed **1,432 with one skip on Windows** and **1,423 with five skips on Ubuntu**. Historical [CodeQL run 34125334102 at `2d672c5`](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34125334102) also passed.

At `2d672c5`, [headless CI](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34125333030) passed on Windows (**1,393 passed, 1 skipped**) and Ubuntu (**1,384 passed, 5 skipped**). [CodeQL](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34125334102) passed. These runs precede `0e2c4fc` and do not validate the later Review action UI.

Historical local suites passed Core 522, Host 162, Presentation 601, Groq 32 and NVIDIA 25 with one skip; Filler Words passed 59 after its unload-cleanup test fix. Subsequent focused runs passed Bootstrap 21, Action Registry 16, Action Controller 4, Obsidian 18 and Recovery/Delivery 45. These are separate runs, not additive full-suite totals.

Earlier local build/launch, native inference, owner feedback and focused UI checks are scoped in [testing](WINUI-TESTING.md). The last live v2 Discover check returned HTTP 404. Package operations have fixture coverage, not published-feed acceptance. No full real Groq dictation acceptance is recorded. This documentation audit does not rerun native tests.

Version 1.1 is greenfield: no legacy plugin binary compatibility promise, no required old-history import and no automatic production-data migration. The current WinUI host uses isolated development storage and targets Windows build 26100; it is not replacement-ready.

## Historical checkpoint, 2026-09-07: portable runtime and additional connected behavior

- Portable transcription/LLM roles share one runtime owner; Groq and dynamic provider/API-key settings consume it. NVIDIA/CTC keep their existing dedicated owner. Workflows and other capability consumers remain separate work.
- Real dictionary/snippet JSON transfer, verified Mac dictionary import, preserved metadata and explicit replacement; usage counters update successful snippet expansion without overwriting concurrent edits.
- Saved spoken-command profiles and optional target-app formatting now reach the ordered text pipeline.
- Bounded shortcut coordination preserves stop/cancel during asynchronous startup and prevents later recordings from queued processing-time presses.
- Named Debug test profiles isolate preferences, data, keys, packages and model assets for native restart/mutation tests. Native dictionary/snippet import and multiline editor checks passed.

Latest focused validation: Host 133 and Presentation 285 passing cases; prescribed Debug build/launch passed. This is incremental evidence, not a new complete release acceptance run.


## Historical checkpoint, 2026-09-07: real manual workflows and silence filtering

- Custom manual workflows now persist prompts, exact provider/model and enabled state, with confirmed deletion and protected unsupported entries. Run invokes registered LLM providers and preserves source text on cancellation/failure. Automatic triggers, selected-text capture, templates and full override editing remain open.
- Saved quiet-clip handling, original-sample thresholds, decoder-only padding and actual final no-speech metadata now reach dictation processing.
- Complete headless validation at `a28367e3`: 517 passed, one platform skip; prescribed Debug build/launch passed. Native workflow create/save/restart/disable/delete checks passed in an isolated named profile. No successful live cloud workflow request is claimed.

## Historical checkpoint, 2026-09-07: media queue and additional preferences

- Selected media reaches Windows decoding and the actual transcription provider. Serial requests support cancellation/drain, retry, optional History and TXT/provider-timed subtitle export. Queue persistence and file-specific vocabulary/snippet stages remain open.
- Explicit SDK language-hint capability replaces implicit first-language fallback. Connected NVIDIA/Groq providers do not advertise it.
- Overlay text size and successful-output duration persist, including immediate dismissal at zero; unavailable online batch preview is hidden.
- Latest complete local run: 560 passed, one platform skip; prescribed Debug build/launch passed. Native file selection/removal/readiness and overlay preference persistence were checked in an isolated profile. Ubuntu workflow fault injection was corrected after CI exposed Windows-only lock assumptions.

## Historical checkpoint, 2026-09-07: final cancellation and awaited shutdown

- Tray cancellation reaches final processing immediately; linked tokens guard provider, CTC, formatting and output stages. Native work is drained before disposal.
- Exit waits for initialization, active session work, file queue and shortcut coordination. Shutdown permanently rejects new queue jobs and reports cleanup failures. Durable recovery remains open.
- Complete local validation: 569 passed, one platform skip; prescribed Debug build/launch passed. Groq package unload now has an explicit collection assertion after Windows CI exposed a test-lifetime issue; its 17 focused tests passed.

## Historical checkpoint, 2026-09-07: real recorder, bulk history and automatic CTC setup

- Recorder now captures microphone and/or system output, preserves loopback silence on a shared timeline, writes a named local WAV atomically and retains unsaved audio for retry. It reserves the dictation session and drains before shutdown. File-queue handoff requires an explicit Start. Recording library, pause/tracks and crash recovery remain open.
- History multi-selection supports selected TXT/MD/CSV/JSON export, confirmed atomic deletion and clear-history snapshots that retain entries added during confirmation. Native selection, export, default Cancel, selective deletion, Escape and clear-all checks passed.
- NVIDIA provisions its internal CTC model and tokenizer with pinned checksums, bounded BZip2/TAR extraction and cancellable activation. Actual first-run download, verified installation and Ready UI passed in a fresh test profile. Accuracy across languages remains separate acceptance work.
- Complete local suite: **611 passed, one NVIDIA platform skip** (Host 160, Presentation 394, Groq 32, NVIDIA 25). An additional **64 Windows audio tests passed** in Debug. Prescribed WinUI build/launch passed. Real system capture, explicit file handoff and Canary transcription succeeded; the corrected timeline retained 47.632 seconds during a 47.713-second measured UI run, including leading/trailing silence and a German filename.
- Latest pushed baseline before this slice, `536f7905`, passed Windows/Ubuntu headless CI and CodeQL. Fresh CI for these commits is tracked on the PR; CodeRabbit still skips draft review.

## Historical checkpoint, 2026-09-07: recording library and file lexicon

- Saved recordings are discovered from real WAV files after restart, with actual duration, size/date and visible corrupt-file errors. Default-app playback/folder navigation, explicit file handoff and confirmed deletion are connected. A queued source cannot be deleted; deleting the current saved result removes its stale actions. Hidden recorder controls are collapsed while browsing the library.
- Files share dictation dictionary/snippet processing and eligible Parakeet CTC. Provider segments remain original for subtitles. History and snippet usage commit only after final queue acceptance; canceled late results cannot leave a History entry or increment usage.
- Complete local validation: **640 passed, one NVIDIA platform skip** (Host 160, Presentation 423, Groq 32, NVIDIA 25). Prescribed Debug build/launch passed. Native library enumeration, queue protection, cancel/confirmed delete and Canary file-to-snippet-to-correction processing passed. The fixture usage count stayed at one while viewing/reopening its result.
- Recorder preferences and durable file recovery are the next slices; neither is claimed complete here. CI passed on both platforms plus CodeQL for the previous pushed checkpoint `905434c5`.

## Historical checkpoint, 2026-09-07: opt-in file recovery and recorder device preferences

- Explicit opt-in checkpoints retain the file queue and completed results after restart. Interrupted jobs require Retry; restored results never replay History or snippet usage. Pending side-effect receipts show uncertainty instead of claiming cross-store exactly-once delivery. Corrupt checkpoints require confirmed reset; disabling recovery removes saved queue data but retains this session.
- Recorder microphone/system choices and an explicit output endpoint persist and are shared by the recorder and settings. Missing selected devices fail visibly. The capture adapter owns the endpoint and retains cleanup state for retry if disposal fails.
- Complete local validation: **673 passed, one NVIDIA platform skip** (Host 160, Presentation 456, Groq 32, NVIDIA 25), plus **68 Windows audio tests passed** in Debug. Prescribed build/launch passed. Native restart restored a completed file result without changing History or usage. Recovery off erased the checkpoint while retaining the visible result. The selected Creative endpoint survived restart and produced a non-silent 33.208-second mono 16 kHz WAV titled `Gerätetest`.
- Windows/Ubuntu headless CI and CodeQL passed at the previous pushed checkpoint `e45abf6f`. Durable dictation/capture recovery, watch folders, pause/tracks and broader device acceptance remain open.

## Historical checkpoint, 2026-09-07: workflow rules and reviewed local backup

- Eight shared Core templates now persist in the editor, including an explicit translation target and optional fine-tuning. App rules outrank Global fallback; enabled state and priority participate in matching. Prompt/provider/model and language are captured at dictation start. A required LLM failure retains the transcript for review and cannot auto-paste, whether History is enabled or disabled.
- Backup export and merge preview use the real four category stores. Confirmed restore stops admission, drains runtime work, retention, History mutations and open transfer operations, publishes with a recoverable journal, and closes the app. Startup opens no stores if recovery fails. Existing recordings, credentials and device preferences are outside the backup.
- The expanded CI suite now includes Core. At `77763cfd`, Windows passed **1,214 tests with one skip**, and Ubuntu passed **1,205 with five skips**. The initial Ubuntu run exposed an older Windows-only path separator assumption; platform-independent device-ID validation fixed it. Native build/launch passed for `25cc018`.
- Native Translation create/reopen/discard and real backup export/preview/default Cancel/confirmed merge passed in the isolated profile. Restoring added one item to each selected category, kept History byte-for-byte unchanged and closed the app. A malformed journal showed only the themed recovery window and created no profile stores. Live cloud processing and native forced-crash/save-failure acceptance remain open.

## Historical checkpoint, 2026-09-07: real setup, startup and workflow visibility

- Setup now uses actual microphone priority, shortcuts/mode, ready provider/model choices, supported languages and output/history preferences. Model downloads and API keys stay in plugin settings. Navigation progress persists; Finish requires a ready configuration and records no synthetic transcription. The setup layout uses the existing theme and a distinct primary action.
- Development startup registration has an explicit published-output identity and verified readback. Test profiles never access the registry. Silent launches keep the tray and shortcuts available; production startup identity and updater integration remain open.
- Unsupported workflows remain visible and can be enabled or disabled while preserving their other metadata. History details now show the stored workflow name and processing failure.
- At `85d53e2`, Windows CI passed **1,251 tests with one skip**; Ubuntu passed **1,242 with five skips**. CodeQL passed. Native setup selected the real QuadCast microphone, Toggle, German and Review first; Finish persisted and reopening retained those choices. An imported unsupported workflow could be enabled/disabled with all other stored metadata unchanged.
