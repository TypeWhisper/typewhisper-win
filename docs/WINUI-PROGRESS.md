# Windows 1.1 progress

## Recorder task flow (`9c419c55`), 2026-09-08

Evidence level: artifact/source review of Marco's report that Recorder playback works but the UI requires too many clicks and is unclear. No live expert walkthrough or new native user test was performed.

- **Major, high confidence:** after Stop, the completed-information page required opening Saved recordings and finding Play. Saving now opens Recordings with the newly saved file selected; Enter starts playback. The old information-only completion page is removed.
- **Moderate, high confidence:** each saved item repeated four actions and raw byte metadata. A compact single-selection list now shows title, duration and date, with one footer for the selected recording. Enter toggles play/pause, Delete opens deletion confirmation, F opens the folder and T adds to the transcription queue. Native controls and text inputs retain their own keys.
- **Moderate, high confidence:** Transcribe file only queued the recording, and the global title input remained visible while browsing saved files. The action now says Add to transcription queue; the title input hides in Recordings and returns on Record. Record and Recordings have stable tab labels. Queue processing still requires Start on the queue page.

Changing selection stops playback. Fresh library loads preserve selection by path, and completion selects the new file. The saved recording remains recoverable on existing save/retry paths; no audio pipeline or deletion policy was changed. Playback status updates between playing and paused. Footer actions recover after canceled deletion.

46 focused Recorder/encoding tests passed (`artifacts/test-results/recorder-ux/recorder-ux.trx`). The final prescribed normal-profile build/relaunch passed (`artifacts/recorder-ux-build.log`), with a nonzero main-window handle and unchanged diagnostic log. Native layout and the same capture-stop-play task need Marco's acceptance, including keyboard navigation, deletion cancellation and reopening Recordings. The prior basic playback success is user-reported and does not validate this redesign.

## Internal Recorder playback (`fca2257d`), 2026-09-08

Saved recordings now offers Play audio inside TypeWhisper instead of opening the associated desktop player. A fixed library playback area shows the file name and native WinUI transport controls for playback, pause and seeking, plus Stop playback. Playback uses the system-default audio output. Show in folder remains an Explorer action.

The owned WAV path is revalidated before and after asynchronous opening. A generation check prevents departed or superseded loads from playing. Leaving/hiding the Recorder, switching library views, confirmed deletion and shutdown release the player/source. Dictation/Recorder capture and speech read-back stop this player through the existing capture/playback hook. Starting playback first drains speech and checks session readiness; paused capture also prevents playback. File/device failures show library feedback and keep the recording intact.

775 Presentation tests passed (`artifacts/test-results/recorder-player/recorder-player.trx`), including playback path revalidation. The prescribed normal-profile build/relaunch passed (`artifacts/recorder-player-build.log`), with a nonzero main-window handle and unchanged diagnostic log. Native playback, seeking, end/replay, navigation, deletion and capture-interruption acceptance remain pending. No Computer Use was performed. Implementation reference: [WinUI media playback](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/media-playback).

Marco confirmed repeated lexicon tab navigation and the multi-line signature snippet test. Those user-reported results supersede the earlier pending basic tab/signature checks, without implying every disabled-snippet or placeholder case was checked.

## Persistent lexicon tabs (`41907c35`), 2026-09-08

Words, Corrections, Snippets and Term packs now share a fixed tab row above the scrollable content. Term packs uses the Dictionary heading and selected-tab styling; switching back clears the pack state, and keyboard focus returns to the activated tab. Esc from the pack list now exits to Quick Launch as from the other lists. Pack enablement and persistence are unchanged.

The prescribed build/relaunch passed (`artifacts/term-pack-tabs-build.log`), with a nonzero main-window handle and unchanged diagnostic log. The source-encoding regression test passed. Native tab switching, scrolling and keyboard/layout acceptance remain pending; no Computer Use was performed.

## Existing snippet integration verified, 2026-09-08

The proposed snippet implementation was already connected. Inspection confirms that normal dictation captures the snippet catalog, applies enabled replacements through DictationLexiconSnapshot after the optional LLM step, and passes the resulting final text to both insertion and History delivery. Existing tests cover persistence, case/punctuation/multiline expansion, placeholders, disabled entries, clipboard access, snapshot isolation and usage metadata. No duplicate implementation was added.

69 focused Presentation tests passed (`artifacts/test-results/snippet-verification/snippet-verification.trx`). The current development app remained running with a nonzero main-window handle. This is source and automated-test verification, not native dictation acceptance. Manual check: save an enabled snippet with spoken trigger "meine Signatur" and a multiline replacement, dictate the trigger without a workflow, and compare the inserted text with History. Then disable it and repeat; the trigger should remain unexpanded. LLM workflows run before snippet expansion, so a workflow can change the trigger before matching.

Marco also confirmed that the shared workflow default LLM works. This user-reported acceptance does not separately establish every restart, unavailable-provider or capture-isolation case.

## Shared workflow LLM (`fe9c924c`), 2026-09-08

Workflows now has a Default LLM footer action that saves a shared provider/model pair in the local profile (`workflow-llm-default.json`). New workflows use the explicit Use default selection and display the inherited model; existing explicit or unconfigured selections keep their meaning. Each workflow can still choose its own provider/model. The default dialog validates the current provider/model, uses Save/Cancel, reports failed writes, and is closed and drained during shutdown.

Manual execution, selected-text shortcuts, explicit dictation shortcuts and automatic App/Website/Global matching resolve the same pair. Automatic matching resolves only the selected rule. Resolution captures provider/model before execution; changes to the default cannot redirect an existing recording or selected-text operation. Explicit selections never fall back to another provider when unavailable. Missing or malformed defaults produce configuration feedback/review rather than silently selecting a service. Saving the default clears its stale shortcut notice.

Validation: 774 Presentation tests passed (`artifacts/test-results/workflow-defaults/workflow-defaults.trx`), including persistence, explicit-selection isolation from corrupt defaults, legacy missing-selection preservation, captured recording selection, automatic matching and manual resolution. The prescribed normal-profile build/relaunch passed (`artifacts/workflow-defaults-build.log`), with a nonzero main-window handle, Quick Launch title and unchanged diagnostic log. No Computer Use was performed; native picker/dialog/layout and actual inherited LLM processing remain pending manual acceptance.

Manual check: Workflows > Default LLM > choose provider/model > Save. Create a translation workflow, leave provider on Use default, and run it. Check an existing workflow with its own selection still uses that choice. Restart and check the shared selection persists; test missing/unavailable default feedback separately.

## Visible workflow notices and encoding audit (`80eb4fe7`), 2026-09-08

The workflow failure notice now uses a warning icon, bordered inset surface, a distinct heading, readable body text and bottom-row Edit workflow / Dismiss actions. Edit workflow opens the exact affected workflow without replacing an active editor or another workspace. Saving that workflow clears its stale notice. Keyboard access uses ordinary Tab and focused-button Enter behavior. Native layout, direct-edit and stale-notice acceptance remain pending.

The encoding audit covered 1,232 tracked text files across source, plugins, tests, scripts and documentation. It found damaged workflow provider separators, processing ellipsis and navigation glyphs, plus a mixed-encoding History XAML line. These are repaired with explicit Unicode escapes or an XML character reference. The repeat scan found no further candidates (`artifacts/encoding-scan-after.json`); this is a source-text audit, not a visual inspection of every window or runtime plugin string. A new Presentation test rejects invalid UTF-8 and known mojibake sequences in application source/localization text under src.

767 Presentation tests passed (`artifacts/test-results/workflow-notice/workflow-notice.trx`). The prescribed normal-profile build/relaunch passed (`artifacts/workflow-notice-build.log`). The app exposed a nonzero main-window handle and Quick Launch title; the diagnostic log was unchanged. No Computer Use was performed.

## Workflow configuration feedback (`fbf956d4`), 2026-09-08

Marco reported that workflow dictation worked after choosing an LLM, but the missing selection had not been clearly explained. The editor now highlights distinct messages for a missing LLM provider, missing model and unavailable configuration while allowing incomplete setup to be saved. Dictation workflow shortcuts check this configuration before recording and bring up a visible notice with the workflow name and corrective action. A stop press still finishes an existing capture. The selected-text activation label also replaces its malformed separator with a Unicode escape.

Validation: 766 Presentation tests passed (`artifacts/test-results/workflow-configuration/workflow-configuration.trx`). The prescribed normal-profile build/relaunch passed (`artifacts/workflow-configuration-build.log`), with a nonzero main-window handle, Quick Launch title and unchanged diagnostic log. Manual acceptance of the new warning and corrected label remains pending. Marco's workflow success report does not establish the separate isolation, cancellation or restart checks.

## Workflow dictation shortcuts (`fceab952`), 2026-09-08

The workflow editor now offers **Shortcut - dictation** alongside selected-text shortcuts. A press starts a normal recording with an immutable snapshot of that workflow; the next dictation-workflow shortcut press stops capture without switching the active snapshot. The explicit workflow takes precedence over app/website/global matching for that recording only. Ordinary dictation still selects its own rules. Processing uses the saved LLM provider/model and the existing dictation delivery, review-on-error and History preferences. Recording/model/output overrides remain unsupported.

Both shortcut kinds share native registration, persistence, enablement/deletion and conflict checks. The input coordinator owns the one-shot start callback, including pending stop/cancel and rejected competing starts, so a canceled or failed start cannot leave a workflow override for a later recording.

Validation: 761 Presentation tests passed (`artifacts/test-results/workflow-dictation/workflow-dictation.trx`), including snapshot isolation, editor round-trip, both registration kinds, rapid stop during start and canceled queued starts. The prescribed normal-profile Debug build/relaunch passed (`artifacts/workflow-dictation-build.log`). The process exposed a nonzero main-window handle and Quick Launch title, with no new diagnostic-log entries. Native workflow dictation and real LLM processing are still pending manual acceptance; no Computer Use was performed.

Manual check: create/edit a workflow, select Shortcut - dictation, choose a configured LLM provider/model and assign a free chord. Save, focus an external text field, press once, dictate and press again. Confirm the processed result follows dictation output settings. Then use ordinary dictation and confirm the explicit workflow has not carried over. Check cancellation and disabled/restarted shortcut behavior separately.

### History player acceptance update

Marco confirmed internal audio playback, deletion of the associated audio when deleting the entry, and playback stopping on Esc back to the History list. These are user-reported native results. Seek, individual pause/end/replay cases, device errors, capture interruption and the separate P text-readback action remain unverified.

## Inline History audio player (`7340cbf8`), 2026-09-08

Saved History audio now plays inside TypeWhisper instead of launching the associated desktop player. The footer contains a source-owned play/pause icon and A shortcut, position/duration, and a keyboard-accessible seek slider. F still opens the audio folder. P remains the separate text read-aloud action. The implementation uses Windows MediaPlayer/MediaSource with the verified local audio path and system-default playback output; it does not yet route this audio through the saved spoken-feedback output selection.

Navigation, History close, editing/deletion and shutdown dispose the player/source and invalidate pending loads. Starting dictation or Recorder capture stops the player first. Text read-back stops audio playback; starting audio drains shared speech before playing. Startup and media-event callbacks recheck the current request/player so delayed loading cannot play a departed entry. Background History refresh is deferred while an audio player is retained. Playback/seek/media failures leave the transcript intact and show actionable feedback. No audio export or History/clipboard write is involved.

The prescribed normal-profile Debug build/relaunch passed (`artifacts/history-player-build.log`). The process had a nonzero main-window handle and the Quick Launch title; the diagnostic log remained unchanged from before the launch. The preceding 757-pass Presentation result is not a new test run for this WinUI-only player. Native play/pause/seek, end/replay, navigation and deletion release, capture interruption, missing-file/device-error and layout acceptance remain for Marco. No Computer Use was performed.

API references: [MediaSource playback](https://learn.microsoft.com/en-us/windows/apps/develop/media-playback/media-playback-with-mediasource) and [MediaPlayer playback session](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/media-playback).

## History read-back startup correction, 2026-09-08

Marco reported that the app was not visible. The process existed but had no main window: the diagnostic log showed `Delegate to an instance method cannot have null this` in the MainWindow constructor. The new History delegates had been assigned before `_dictation` was constructed. Initialization now precedes those bindings.

The prescribed normal-profile build/relaunch passed (`artifacts/history-startup-fix.log`). The restarted process exposed a nonzero main-window handle and title `TypeWhisper Quick Launch Prototype`; the diagnostic log remained unchanged from before the corrected launch. Earlier History read-back launch claims based only on process existence were insufficient and are superseded by this evidence. Native History read/stop acceptance still remains open.

## History read-back (`90a3914b`), 2026-09-08

History details now offer Read aloud / Stop reading in the bottom action area with P. Saved-audio playback moves to A; F still opens its folder and Enter still copies text. The displayed entry text and language reach the existing local speech controller with current voice/output preferences, independently of automatic feedback. Reading does not replace the last-dictation snapshot or write History/clipboard data.

Navigation back, closing History, editing, deletion and shutdown request cancellation of the owned history playback. Cancellation matches the exact request reference, so stale History cleanup cannot stop newer automatic/shortcut speech. Background History refresh is deferred during playback, including synchronous startup notifications, to avoid canceling a newly started read. Text inputs, modifiers and dialogs retain their keyboard behavior. Existing speech size/duration limits remain visible rejections without truncation.

Focused Presentation validation passed **757 tests**, including three request-ownership cases covering cancellation drain, equal-but-distinct requests and stale cleanup after newer speech starts. The prescribed normal-profile Debug build/launch passed (`artifacts/history-readback-build.log`); no native UI automation was used. Marco confirmed last-dictation read/stop via hotkey before this change. History P start/stop, returning to the list, editor/delete cancellation, saved-audio A and narrow-layout acceptance remain manual checks; new-head CI is pending.

## Read last dictation shortcut (`26e6d088`), 2026-09-08

Read last transcription is connected in Quick Launch and Settings > Shortcuts, with an unassigned-by-default persistent global binding and bidirectional conflicts against the other connected shortcuts. It reads the final session snapshot through the shared Windows speech controller and current voice/output selections, independently of the automatic spoken-feedback toggle. Repeating the action cancels active speech and waits for synthesis/playback drain; it does not queue a second reading. Shutdown/profile restore unregister the shortcut and drain the shared speech activity. Successful playback does not activate Main, write History, copy or paste text.

The existing speech limits apply without truncation: up to 4,000 text characters and the backend's bounded audio duration/size. Empty sessions and unavailable voices/outputs report a reason. Start admission is blocked while recording/processing/configuring; starting a new dictation uses the existing speech-cancellation path. History-entry read-back is separate and remains open.

Focused Presentation validation passed **754 tests**, including five new cases for exact text/language/voice/output, empty input, oversized text, stop/drain without duplicate playback, and closed-controller rejection. The prescribed normal-profile Debug build/launch passed (`artifacts/read-last-build.log`). Native read/stop, real voice/output selection, persistence and new-recording interruption await Marco. No Computer Use was performed. Marco confirmed the preceding copy-last shortcut works; that confirmation does not establish all its persistence/conflict/History-off edge cases.

## Copy last dictation shortcut (`a5bed04c`), 2026-09-08

The Copy last transcription row in Settings > Shortcuts is now connected, with a matching Quick Launch action. It defaults to unassigned, persists in `copy-last-transcription-hotkeys.txt`, reuses atomic registration/save/rollback, and checks conflicts in both directions against launcher, dictation (including modifier-only prefixes), Cancel, History and workflow shortcuts. Shutdown and profile restoration unregister it.

The action copies the exact completed session snapshot, including with History off or Review first. A successful global invocation does not activate Main or paste text. Startup has no retained dictation; empty/error feedback explains the outcome without clearing the clipboard. Active capture/output or shortcut editing prevents copying. The snapshot is RAM-only and does not load historical entries after restart. History read-back remains open.

Focused Presentation validation passed **749 tests**, including six copy-action cases for exact final text, admission, fresh/closed sessions and clipboard failure/retry. The prescribed normal-profile Debug build/launch passed (`artifacts/copy-last-build.log`) and the development process was verified running. Native shortcut capture/persistence, conflict handling, copy/paste, focus preservation, History-off and restart checks remain for Marco; no Computer Use was performed. Earlier CI evidence below applies to its named commits, not this change.

## Verified CI for `a28986cd`, 2026-09-07

[Headless run 34158614534](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34158614534) and [CodeQL run 34158615082](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34158615082) both completed successfully. Both runs report exact head SHA `a28986cd28b1c9d22ba889c5e853dd463b6c049b`; all three CodeQL language jobs passed.

Downloaded TRX artifacts (`artifacts/ci-a28986cd/`) establish **1,632 passed and one skipped on Windows**, and **1,623 passed and five skipped on Ubuntu**. Counts were read from individual test results, including `NotExecuted` results, because the TRX summary's `notExecuted` counter is zero despite platform skips. Presentation passed 743 cases on both systems. Ubuntu omits five Windows-only Host cases and skips five Windows CUDA cases; Windows skips the unsupported-CUDA-platform case. These are platform-specific complete CI suite results, not local native acceptance.

Marco confirmed the History footer arrangement and Enter/E/X/Delete shortcuts outside the text editor. Marco also confirmed Groq shows the recording indicator, no live-text window during recording, and the correct final text after Stop. This is user-reported native acceptance of the Groq gating fix. On 2026-09-08 Marco confirmed live preview returns after switching from Groq to a local NVIDIA model, Alt+W opens History with Settings left open and without the old warning, and one normal Tray Exit closes the app without a visible error. These user-reported checks do not establish repeated shutdown, shutdown while busy, or every saved-preference combination. Editor input, dialog cancellation and complete Tab navigation are not covered by that confirmation. This CI evidence belongs to the exact SHA above; subsequent documentation-only commits are not represented as separately validated code.

## Manual-test fixes through `2864ed3c`, 2026-09-07

- `2864ed3c`: History list/detail actions occupy a fixed footer. Enter opens/copies; E edits, X exports and Delete opens confirmation. Audio and selection actions also expose keyboard shortcuts. Text inputs, modifiers, dialogs and flyouts retain their keyboard behavior; a tab-focused button keeps normal Enter invocation. Marco confirmed the visible footer arrangement and Enter/E/X/Delete shortcuts outside the text editor; this is user-reported native acceptance. Editor input, dialog cancellation and full Tab navigation remain unverified.
- `a9f94191`: the History hotkey can open History while the separate Settings window remains open. Marco's earlier Alt+W screenshot proves registration/invocation reached the old overly restrictive guard; Marco subsequently confirmed on 2026-09-08 that Alt+W opens History with Settings still open and without the old warning.
- `fdb72396`: live text uses an explicit host availability boolean. Groq and other registry providers without a connected live path do not show live text during recording/processing; completed final text remains available. Settings retain the preference for supported local models. Marco confirmed the corrected Groq behavior: recording indicator visible, no live-text window during recording, and final text delivered after Stop. On 2026-09-08 Marco confirmed the live preview returns after switching back to a local NVIDIA model. Explicit preference-off preservation across switches remains unverified.
- `a6dcb8e9`: activation/window-change callbacks return during shutdown and profile restoration, addressing the logged presenter exception. Marco confirmed one normal Tray Exit without a visible error on 2026-09-08. Repeated shutdown and shutdown while busy remain unverified.
- `135b3ca2`: a bounded in-memory last-completed-dictation snapshot preserves final text independently of History and paste success. Twenty cases cover publication and lifecycle behavior. Copy-last and read-back actions are not connected by this backend commit.

The prescribed Debug build/launch passed with these changes using the normal development profile, with all `TYPEWHISPER_WINUI_*` probe variables removed. Evidence: `artifacts/history-footer-build.log`. The focused Presentation TRX records **743 passed**, including 20 snapshot cases and eight live-window-policy cases. The previous complete suite remains **1,604 passed with one skip**; no newer full-suite count is inferred. Computer Use and subagents remain stopped while Marco tests. The draft is not ready for release; the broader parity gaps below remain open.

## Recent transcriptions shortcut — committed checkpoint `25c720b8`

The global Recent transcriptions shortcut now opens or focuses the existing History workspace. It defaults to unassigned and reuses atomic registration/persistence with rollback and authoritative native bindings. Conflicts are checked in both directions against Quick Launch, dictation (including modifier-only prefixes), Cancel and workflow shortcuts. Recording, pending starts, processing and other workspaces in Main prevent navigation; a visible notice explains refusals. Separate Settings windows are allowed by follow-up `a9f94191` and keep their drafts. Shutdown and profile restore unregister the shortcut.

The complete local suite passed **1,604 tests with one skip**: Core 544, Host 208, Presentation 715, Filler Words 62, Groq 32, Obsidian 18 and NVIDIA 25 with one skip. The prescribed Debug build/launch on 2026-09-07 passed. The 14 new History-shortcut cases are included in that total.

Native inspection reached the enabled editor through Settings search. `Ctrl+Shift+F9` showed the Main dictation conflict and disabled Use; `Ctrl+Alt+F11` reached a ready draft. After clicking Use, `recent-transcriptions-hotkeys.txt` was absent. Computer Use stopped through Escape before the resulting UI error could be inspected. Whether the click or registration failed is unresolved. **Native save, restart persistence, invocation and workspace-guard acceptance remain open.** A separate snapshot backend is still working-tree work and has no UI acceptance claim.

## Tray actions — committed checkpoint `db2c9c8e`

The tray slice connects persistent dictation-hotkey pause and guarded recovery navigation. Paused status no longer invites recording through the paused shortcut; shutdown disables the actual menu presenter. The stale plugin-actions placeholder was removed, and shortcut help now includes selected-text workflow cancellation. These changes are committed in `db2c9c8e`. The prescribed Debug build/launch on 2026-09-07 passed, including the corrected UI hints.

The focused Presentation suite passed **701 tests**, including nine new tray cases. At that checkpoint, the prior complete suite had **1,581 passed with one skip**, before the tray additions; the newer complete run is recorded above.

Native checks used the explicit isolated Debug profile with `TYPEWHISPER_WINUI_TRAY_PROBE=1`: its button opened the real `TrayMenuWindow` because the taskbar tray was not exposed by the test environment. Pause persisted `Paused=true`. With the workflow dry-run probe enabled, pressing `Ctrl+Shift+F9` left the probe-file hash unchanged, confirming no capture through that paused shortcut. Recovery opened a new Settings window directly at Files & recovery with zero recovery entries and no processing. When Settings was already on Shortcuts, the tray action preserved that page and displayed a navigation notice. An unsaved recovery toggle set to On also remained On after the tray action while `recovery.json` still stored `Enabled=false`; it was then returned to Off without saving. This demonstrates preservation of that settings draft, not a shortcut-capture draft: shortcut capture is canceled on focus loss.

A fresh process restored `Paused=true` in both UI and JSON, and `Ctrl+Shift+F9` again left the workflow-probe hash unchanged. The native tray status clearly explained that dictation hotkeys were paused. Tray Resume persisted `Paused=false` and changed the available action back to Pause. Pressing `Ctrl+Shift+F9` then reached the Debug workflow probe; with Main in the foreground, it reported `external_target_required` and the UI stated “No audio was recorded”. The probe hash changed to `FEEAC48CF763235590D417E92718B31AA073645E62392B63E9C758A936B86130`. Clicking native Tray Exit terminated the TypeWhisper.WinUI process. The History hash remained unchanged.

These checks do not cover resuming with keys already held, active capture, save failure or shutdown while busy. The menu probe does not establish taskbar-icon accessibility. Other unfinished shortcut actions and release gates remain open.

## Model removal checkpoint `b1613315` and recorder `128ab85`

Commit `6411de7` adds Host/controller/local model removal; `b1613315` connects UI and Session. This committed slice adds explicit removal for providers that advertise the SDK capability. Generic removal revalidates the exact package activation, engine, version and model under its serialized lease, checks actual downloaded state, and confirms the result without changing provider or selection. Selected models and models that may still be loaded are blocked. A canceled or failed load remains conservatively protected until the plugin is disabled and re-enabled; select another model before removing the protected model. Already absent models are handled idempotently. The local adapter also rechecks package generation and selected/active models. UI removal requires explicit confirmation with Cancel as the default action. Downloads and removals share single-operation admission and cancellation/shutdown drain. Cancellation does not promise to restore files already removed; actual status must be refreshed.

Recorder commit `128ab85` adds Pause/Resume for microphone/system recording. Pause stops both sources while retaining captured segments and the exclusive session reservation. Resume uses the original source choices; paused time is excluded from the resulting audio and active duration. Stop publishes the combined recording, failed writes retain audio for retry, and shutdown handles paused captures. Separate tracks and additional formats remain outside this slice.

The complete local headless suite passed **1,581 tests with one skip**: Core 544, Host 208, Presentation 692, Filler Words 62, Groq 32, Obsidian 18 and NVIDIA 25 with one skip. New focused coverage comprises 14 generic Host removal cases, six shared model-controller cases, six local model-management cases and nine recorder cases. These are included in the full-suite total, not additional tests to add to it.

The prescribed Debug build/launch on 2026-09-07 passed, and the ignored model fixture was installed offline without activation, then explicitly enabled. Model removal is now committed through `b1613315`. Native fixture acceptance confirmed selected/downloaded A could not be deleted and showed the reason. After downloading B, the delete dialog defaulted to Cancel; Cancel retained both 37-byte markers and selection A. Confirming removal and then canceling the operation produced Canceled with both markers still present. Confirming again and reopening settings showed B as not downloaded after completion; filesystem checks confirmed A present and B absent. Selection A, the local dictation provider and the History hash were unchanged. The reopened view was observed after completion, so progress visibility during a reopened active removal is not claimed. Fixture uninstall was then confirmed natively: the UI showed “No matching plugins”, its ID was absent from the installed index, and `Enabled=false` persisted. Selection A and its marker were retained; B remained absent. NVIDIA settings showed active, ready Canary with its Remove button disabled after scrolling it into view; Parakeet was not downloaded and offered only Download. No actual NVIDIA model deletion was performed or claimed.

Recorder Pause/Resume is committed in `128ab85`. Native system-audio-only acceptance used the Creative output with microphone capture off. Pause froze the displayed duration at 14 seconds through observations 7.5 and 36.6 seconds later; navigating to the launcher and back retained Paused with source controls disabled. Resume followed by Stop produced a 16 kHz mono PCM16 WAV of 31.83825 seconds, matching 75.576 seconds wall time minus 43.737 seconds paused (31.839 seconds expected active capture). A separate Stop-from-Paused check produced a 24.3431875-second WAV with 24 seconds shown in the UI. Both test WAVs remain in the isolated profile. These checks establish timing and navigation behavior, not audio quality, microphone coverage or a native sixty-minute limit test. The History hash stayed unchanged.

After the complete 1,581-pass run, a separate focused Presentation run passed 701 tests, including nine new tray cases. This is not a new full-suite total. The tray slice is committed in `db2c9c8e`; scoped native evidence and remaining checks are recorded above.

## Selected-text workflow shortcuts — committed checkpoint `d55ec0d`

Commit `eb0857f4` adds portable selected-text capture and the shortcut catalog; `d55ec0d` connects the UI and runtime. This committed slice adds explicit **Process selected text** shortcuts. A shortcut captures the original window/process, waits for modifiers to be released, sends Copy once and accepts only a changed clipboard sequence owned by that source. Capture has bounded waits and a 128 Ki-character limit; it never falls back to existing clipboard text. The temporary clipboard transaction restores only its own marker or verified copied sequence, including cancellation, and preserves intervening foreign clipboard changes.

The captured selection runs directly through the saved workflow's exact LLM provider/model and opens a copyable result for review. There is no second Run confirmation, automatic paste, History write, recording start or automatic retry. Registration is persisted with workflow edits, validates reserved/conflicting chords and modifier-only prefixes, unregisters disabled/deleted workflows, and rolls back failed storage changes. Failed native rollback suspends execution; callback snapshots do not share mutable draft collections. Unsupported workflow semantics remain preserved and inactive in this shortcut subset. **Workflow-specific Start dictation shortcuts remain open**, as do broader overrides and retry behavior.

The complete local headless suite passed **1,546 tests with one skip**: Core 544, Host 188, Presentation 677, Filler Words 62, Groq 32, Obsidian 18 and NVIDIA 25 with one skip. Portable coverage includes capture ownership, foreign clipboard changes, cancellation/restore, size limits, native partial failures, throwing rollback, persistent enable/delete failures, conflicts and immutable snapshots. This records the local workflow validation at that checkpoint; later verified CI is listed separately below.

The prescribed Debug build/launch on 2026-09-07 passed after the shortcut key-event fix and again after the clipboard adapter's same-process broker fix. These builds validated the workflow implementation now committed through `d55ec0d`. The ignored offline **Workflow test fixture** was built and installed only in the isolated r3 profile. Its two synthetic models echo supplied text after cancellable three- or fifteen-second delays, without network or microphone access.

Native acceptance in the isolated profile confirmed:

- The editor captured `Ctrl+Alt+F8`; the conflicting `Ctrl+Shift+F9` blocked Create. Saving persisted a canonical Hotkey/ProcessSelectedText workflow with the fixture provider and `fixture-text` model. A fresh process restored its registration.
- Selecting only the first of two Notepad lines and pressing `Ctrl+Alt+F8` opened Completed Review containing the exact first line with the fixture prefix, excluding the second line. Explicit Copy and paste into Notepad reproduced that expected result. The clipboard held the second line before capture; pasting into a separate test tab immediately after capture reproduced that second line, confirming restoration.
- With `fixture-slow`, both the review Cancel button and the configured `Ctrl+Alt+F10` cancel shortcut produced Canceled while keeping the original source copyable. Repeating the workflow shortcut while busy did not open a second window. With no Notepad selection, capture timed out without processing existing clipboard text.
- Disable persisted false and prevented another Review. After re-enabling, starting a request and closing its window with `Alt+F4` removed the window and made the session editable again. Explicit Delete, with Cancel shown as the default dialog action, removed the workflow JSON entry; its former shortcut then opened no Review.

The r3 History SHA-256 remained `8EBE5B3F4362E2EFA62D8A65C72A9B1B676382A414FE7F8D8AE5F24EDD55AA1F`. Fixture cleanup was explicitly confirmed through Uninstall, with Cancel shown as the default dialog action. The installed-package index no longer contains its ID, its persisted `Enabled` setting is false, and the UI shows “No matching plugins”. The History hash remained unchanged after cleanup. This acceptance covers the synthetic fixture and Notepad, not real cloud processing, other applications, native execution of a throwing SDK callback or a guaranteed field lock. Existing live-provider and broader release gates remain open.

## History audio checkpoint at `472a3f6`

Core and Presentation History audio are committed in `f8e25ec`; WinUI Settings, Session, History details and the Debug fixture are committed in `472a3f6`. Audio retention defaults off and uses original capture samples. Worker writes recheck both History and audio permissions, coordinate profile mutations, and track owned WAV hashes and pending cleanup without scanning unrelated files. Shared references are preserved; cleanup failures remain visible through retry and retention handling.

The complete local suite passed **1,522 tests with one skip**: Core 544, Host 188, Presentation 653, Filler Words 62, Groq 32, Obsidian 18 and NVIDIA 25 with one skip. The same 22 focused Core audio cases then passed with an additional failed-write null assertion; these are not extra tests to add to the full-suite total. The prescribed Debug build/launch on 2026-09-07 passed. The subsequent prescribed Debug build/launch also passed. Native History details scrolled through the complete content, including the full second text paragraph.

An independent temporary History fixture used a generated one-second quiet tone and a missing-file entry. Available-audio details were correct; Play opened Media Player with the exact WAV filename and one-second duration, and Show folder opened the correct directory. Both external windows were closed. Delete Cancel retained both entries and the WAV. Confirmed Delete left only the missing-file entry and removed only the owned WAV and its index ownership; pending cleanup was empty. Missing-audio UI showed the correct status, no audio actions and copyable text. A separate ephemeral fixture verified failed-delete Retry: its synthetic WAV was held open with read-only sharing. Explicit Delete removed the History entry while retaining the locked WAV; the ownership index listed exactly that filename as pending, and the native cleanup banner and Retry action appeared. After releasing the lock, Retry removed the WAV and its ownership entry, left pending cleanup empty and cleared the native cleanup banner. No microphone or cloud operation was involved. Native opt-in persistence and dependency checks passed: legacy JSON without the audio property showed off; enabling saved `SaveHistoryAudio=true`, and a fresh process without History fixtures showed on. Disabling History disabled the audio toggle while retaining its saved true preference. History was then re-enabled and audio explicitly switched off. Final preferences were `AutoPaste=false`, `SaveToHistory=true`, `SaveHistoryAudio=false`. The r3 History SHA-256 remained unchanged (`8EBE5B3F4362E2EFA62D8A65C72A9B1B676382A414FE7F8D8AE5F24EDD55AA1F`). The prescribed Debug build/launch on 2026-09-07 passed. Actual microphone-to-History audio acceptance remains unverified. This fixture did not use the r3 History store.

Latest reported CI is for `584c7751`: [headless run 34145110314](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34145110314) passed on [Windows](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34145110314/job/101815333773) and [Ubuntu](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34145110314/job/101815333575). [CodeQL run 34145110957](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34145110957) passed for [C#](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34145110957/job/101815337882), [JavaScript](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34145110957/job/101815338049) and [Python](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34145110957/job/101815338025). Both run head SHAs were re-read and verified as `584c7751441832d1c36326e27198e94aa4586618`. No suite counts are inferred from CI statuses. This is not CI evidence for `25c720b8`; the PR remains a draft.

Current committed implementation checkpoint: `db2c9c8e` (2026-09-07), including Recovery UI fix `108a67c`, Review action wiring `e1588aa`, persistent Cancel shortcut `b359c5a`, the initialized Review fixture `d333cb6`, plugin field layout `64dcf8d`, local spoken feedback `ff3c44b`, launcher status reporting `2879b6e`, portable model operations `bea6c08`, multiline field fixes `76c4505` and generic model UI/Session integration `3d80de1`. Release-wide draft: [PR #447](https://github.com/TypeWhisper/typewhisper-win/pull/447), `seofood/release-1.1` against `main`.

The authoritative feature inventory is now the [full comparison against both previous Windows and Mac](WINUI-FUNCTIONAL-STATUS.md). This replaces the accumulated, contradictory milestone checklist. Historical test counts and superseded UI decisions remain available in Git history; they are not current completion claims.

## Earlier committed checkpoints and scoped validation

The persisted Cancel shortcut (`b359c5a`) defaults to unassigned and includes generation guards, failed-save rollback and conflict checks, including modifier-only shortcuts. Native `Ctrl+Alt+F10` save, idle no-op and restart persistence passed. Registered `Ctrl+Shift+F9` was captured completely, and the conflicting choice was disabled after the fix. The focused suite passed 18 tests. At `d333cb6`, the Debug Review fixture opens after initialization.

Historical CI at `76c4505`: [headless run 34133514065](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34133514065) passed on [Windows](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34133514065/job/101779012872) and [Ubuntu](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34133514065/job/101779013415). All [CodeQL jobs](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34133515204) passed: [C#](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34133515204/job/101779020056), [JavaScript](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34133515204/job/101779020306) and [Python](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34133515204/job/101779020455). No suite counts are inferred from these statuses. CodeRabbit skipped draft review.

Commit `64dcf8d` makes plugin text fields single-line by default; Filler Words explicitly uses a multiline editor.

Native Filler Words checks confirmed uninstalling the old package through the UI: the installed list changed from four to three and its index entry was removed. The rebuilt package was explicitly reinstalled through the offline store and enabled. The multiline value `ähm`, `sozusagen`, `quasi` was saved with CR separators. Commit `76c4505` fixes TextBox initialization order, which previously restored only the first line, handles CR-separated entries in FillerWordFilter, and adds a visible input frame. Native frame visibility passed. Native restart now confirmed all three saved lines: `ähm`, `sozusagen`, `quasi`.

Commit `bea6c08` adds portable model-state reads, download and exact-snapshot selection under the package lease, plus the download controller (12 new Host cases and six controller cases). Generic model UI and Session wiring are committed in `3d80de1`: download and explicit load/selection use exact snapshots under package leases, with cancellation drain and no automatic selection after download. The isolated offline fixture has native coverage described below; it cannot transcribe audio. Local spoken feedback is committed in `ff3c44b`; native restart persistence passed, while automatic dictation readback acceptance remains pending.

The earlier complete local suite passed **1,485 tests with one skip**: Core 522, Host 186, Presentation 640, Filler Words 62, Groq 32, Obsidian 18 and NVIDIA 25 with one skip. After the version-getter lock fix, the separate Host run passed 188 cases. These focused cases are not added to the full-suite total.

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
- All eight workflow templates, explicit manual execution and Website/App/Global dictation rules with start snapshots and mandatory review on processing failure. Selected-text shortcuts and capture are committed through `d55ec0d` with isolated Notepad/fixture native acceptance; workflow-specific dictation shortcuts and broader overrides remain open.
- Opt-in file queue restart recovery, a real recording library and persisted recorder source/output-device choices.
- Local backup export and reviewed category merge for dictionary, snippets, workflows and text History. All writers drain before publication and closing; startup recovery blocks profile access on failure. Device sync remains unavailable.
- Headless CI for shared Core, portable host, presentation and plugin-owned suites on Windows and Ubuntu.

## Remaining release work

These are major implementation areas, not final polish. Detailed per-feature gaps, reference sources, a 50-row grouped plugin inventory and acceptance criteria are in the full comparison.

- [x] Connect Review first / AutoPaste and history saving with persistence and delivery tests.
- [x] Connect recording mode, native task, number/punctuation/regional preferences and explicit retention.
- [ ] Resolve the Recent transcriptions shortcut save failure or missed click, then verify native save, restart, invocation and workspace guards.
- [ ] Complete remaining runtime settings; language hints already persist and are gated by explicit SDK capability. Make other preview controls unambiguous.
- [ ] Complete remaining plugin capabilities and job recovery; portable text processors, cancellation, file checkpoints and minimal processor provenance are connected. Complete broader recovery failure-path acceptance beyond the confirmed Retry/Copy, Settings reopen and Delete Cancel/confirm flow.
- [ ] Verify selected-text workflows with real providers and add workflow-specific dictation shortcuts, remaining overrides and retry; verify live provider calls.
- [ ] Extend connected file decoding and microphone/system recording with durable dictation/capture jobs, separate tracks, remaining formats and watch folders; broaden Pause/Resume device and microphone acceptance.
- [ ] Broaden real microphone-to-History audio acceptance, correction learning, backup categories and sync; bulk mutation/export/clear, retention and statistics are connected.
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

- Portable transcription/LLM roles share one runtime owner; Groq and dynamic provider/API-key settings consume it. NVIDIA/CTC keep their existing dedicated owner. Manual, Website/App/Global and selected-text shortcut workflows now consume those LLM roles; remaining capability consumers are separate work.
- Real dictionary/snippet JSON transfer, verified Mac dictionary import, preserved metadata and explicit replacement; usage counters update successful snippet expansion without overwriting concurrent edits.
- Saved spoken-command profiles and optional target-app formatting now reach the ordered text pipeline.
- Bounded shortcut coordination preserves stop/cancel during asynchronous startup and prevents later recordings from queued processing-time presses.
- Named Debug test profiles isolate preferences, data, keys, packages and model assets for native restart/mutation tests. Native dictionary/snippet import and multiline editor checks passed.

Latest focused validation: Host 133 and Presentation 285 passing cases; prescribed Debug build/launch passed. This is incremental evidence, not a new complete release acceptance run.


## Historical checkpoint, 2026-09-07: real manual workflows and silence filtering

- Custom manual workflows now persist prompts, exact provider/model and enabled state, with confirmed deletion and protected unsupported entries. Run invokes registered LLM providers and preserves source text on cancellation/failure. Subsequent commits add all eight templates, Website/App/Global triggers and selected-text shortcuts. Workflow-specific dictation shortcuts and full override editing remain open.
- Saved quiet-clip handling, original-sample thresholds, decoder-only padding and actual final no-speech metadata now reach dictation processing.
- Complete headless validation at `a28367e3`: 517 passed, one platform skip; prescribed Debug build/launch passed. Native workflow create/save/restart/disable/delete checks passed in an isolated named profile. No successful live cloud workflow request is claimed.

## Historical checkpoint, 2026-09-07: media queue and additional preferences

- Selected media reaches Windows decoding and the actual transcription provider. Serial requests support cancellation/drain, retry, optional History and TXT/provider-timed subtitle export. Subsequent commits add opt-in queue checkpoints and per-file vocabulary/snippet processing with acceptance-bound side effects. Watch-folder automation remains open.
- Explicit SDK language-hint capability replaces implicit first-language fallback. Connected NVIDIA/Groq providers do not advertise it.
- Overlay text size and successful-output duration persist, including immediate dismissal at zero; unavailable online batch preview is hidden.
- Latest complete local run: 560 passed, one platform skip; prescribed Debug build/launch passed. Native file selection/removal/readiness and overlay preference persistence were checked in an isolated profile. Ubuntu workflow fault injection was corrected after CI exposed Windows-only lock assumptions.

## Historical checkpoint, 2026-09-07: final cancellation and awaited shutdown

- Tray cancellation reaches final processing immediately; linked tokens guard provider, CTC, formatting and output stages. Native work is drained before disposal.
- Exit waits for initialization, active session work, file queue and shortcut coordination. Shutdown permanently rejects new queue jobs and reports cleanup failures. Durable recovery remains open.
- Complete local validation: 569 passed, one platform skip; prescribed Debug build/launch passed. Groq package unload now has an explicit collection assertion after Windows CI exposed a test-lifetime issue; its 17 focused tests passed.

## Historical checkpoint, 2026-09-07: real recorder, bulk history and automatic CTC setup

- Recorder now captures microphone and/or system output, preserves loopback silence on a shared timeline, writes a named local WAV atomically and retains unsaved audio for retry. It reserves the dictation session and drains before shutdown. File-queue handoff requires an explicit Start. Subsequent commits add the recording library and Pause/Resume. Separate tracks and durable recorder crash recovery remain open.
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
- Windows/Ubuntu headless CI and CodeQL passed at the previous pushed checkpoint `e45abf6f`. Subsequent commits add opt-in dictation recovery and recorder Pause/Resume. Durable recorder crash recovery, watch folders, separate tracks and broader device acceptance remain open.

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
