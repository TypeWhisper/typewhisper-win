## Automatic correction learning (2026-09-08)

Settings > Premium now connects correction learning. In Debug, choose Commercial license or All access under Development access, then enable Learn automatically. Dictate into a supported editor, correct one word within 30 seconds, and commit with Enter, Tab or a focus change. Dictionary > Corrections contains the saved entry; the next dictation applies it. Existing manual or disabled entries are not overwritten. Turning learning off preserves existing corrections; delete them in Dictionary to stop applying them.

Observation starts only after successful automatic paste, never for failed insertion or review-only output. It is cancelled/drained for a new dictation and shutdown, and cancelled on opt-out or entitlement loss. It reads only the captured editor through bounded UI Automation, with no clipboard-copy or descendant-scan fallback. Password fields, terminal processes and detected browser address bars are excluded. Unavailable UIA providers fail without interrupting dictation. No observed field contents are logged or uploaded.

Validation: 57 relevant tests pass in Debug and Release (`artifacts/correction-tests-final.log`, `artifacts/correction-tests-release.log`). Tests cover committed/ambiguous edits, context matching, cancellation, duplicates, persistence failures, manual-entry preservation and application/deletion on the next dictionary snapshot. WinUI build: `artifacts/correction-build-final.log`.

Native smoke passed in Notepad with isolated profile `correction-20260908`: real clipboard insertion of the fixed synthetic sentence, edit `teh` to `the`, focus change, and a persisted `autoLearned` dictionary entry. Premium displayed one learned correction. This exercises insertion, native observation and persistence, without an audio or cloud request. The normal development profile was relaunched afterwards.

For repeatable native tests only, Debug accepts `TYPEWHISPER_WINUI_CORRECTION_PROBE=1` together with a named `TYPEWHISPER_WINUI_TEST_PROFILE`. The dictation shortcut inserts a fixed sample and runs the real observer without model readiness, microphone, History or cloud calls. Set development access and enable learning in that isolated profile first. The default new-profile shortcut is Ctrl+Shift+F9. These hooks are excluded from Release and never work on the normal profile.

## Premium development access (2026-09-08)

Open Settings > Premium. In Debug builds, Development access applies immediately and persists in the active profile's `premium-development.txt`. Select **Use actual access** to delete the override. Scenarios cover locked access, supporter, commercial license, signed-in Premium, combined access and signed-out Premium. Release builds neither read nor write this override and always use the actual-access provider.

The initial actual-access provider grants no entitlements: real license activation/account authentication and the three Premium feature implementations are not connected yet. The UI distinguishes access requirements from implementation availability. This is a development-testable foundation, not completed cloud sync, calendar automation or correction learning.

Validation: `PremiumAccessTests` passes 7 cases in Debug and 7 in Release, including the 16-combination access matrix, persistence/reset, malformed state, failed saves and release override rejection. Native WinUI smoke: navigation, commercial activation, supporter selection using Up/Enter and immediate status changes passed. Build: `artifacts/premium-build-final.log`.

# Testing the 1.1 application

## Public v2 marketplace preview, 2026-09-08

The [v2 catalog](https://typewhisper.github.io/typewhisper-win/plugins-v2.json) is now published on `gh-pages` at `afdaf5b9c83d898283468b0a1dc3fb7e7c50acb5`. It contains NVIDIA Parakeet, Groq and Deepgram 1.1.0 for Windows x64, backed by separate assets in the [portable plugin preview release](https://github.com/TypeWhisper/typewhisper-win/releases/tag/plugins-v2-preview-20260908). The release is a prerelease and is not marked latest. The old `plugins.json` blob and the existing latest-release identity were checked before/after and are unchanged.

Each public ZIP was downloaded and verified against its catalog size/SHA-256. The actual `PortablePluginCatalog`, `PortablePluginStore` and package loader then fetched, installed and activated all three public packages in an isolated profile (`artifacts/marketplace-v2/public-install-validation.log`). This checks public distribution and package loading, without provider requests or local model downloads. Existing personal plugin versions were not updated. Marco separately confirmed a real Deepgram Nova-3 dictation in History after configuring his key.

The selected-provider tests passed on Windows and Linux. The full headless run at `80989c12` had an unrelated Ubuntu failure in Obsidian's concurrent same-name writer test; Obsidian is not in this initial catalog. This publication does not claim that the entire PR's CI is green, nor ARM64 or real-weight update acceptance.

## Legacy package isolation and local Deepgram installation, 2026-09-08

The legacy `manifest.json` files for Groq, NVIDIA, Deepgram and CTC are restored byte-for-byte to the pre-expansion state. Portable builds use separate `manifest.portable.json` files, copied as `manifest.json` only into the 1.1 output. Groq/NVIDIA report their previous versions on the Windows/WPF target and 1.1.0 on the portable target. Deepgram's existing Windows implementation is restored; the new implementation is compiled from `DeepgramPlugin.Portable.cs` only for the portable target. The CTC portable manifest is supplied only inside NVIDIA's portable dependency folder.

Validation: all headless suites passed (`artifacts/test-results/plugin-isolation-headless/`); 54 selected existing Windows plugin tests passed (`artifacts/test-results/plugin-isolation/legacy-plugin-isolation.trx`), and the legacy Deepgram Windows build passed without warnings. These checks do not claim native acceptance for every legacy plugin. The development build/relaunch passed. Deepgram 1.1.0 was subsequently installed and enabled in Marco's normal WinUI development profile using the verified local ZIP and the existing package-store API, with the app stopped during installation. No API key was changed. The app was relaunched using the prescribed script. No public feed or release asset was published; the old catalog and published packages remain untouched.

## Portable provider expansion (`e9b3cde2`, `c0827132`), 2026-09-08

Groq now uses the generic registry for settings, activation, model selection, credentials and all recorded/file/recovery transcription paths. Its dedicated settings view and session configuration wrapper are removed. Shared settings distinguish a saved key from a successful connection check. Existing stored Groq selection IDs are preserved. WAV upload limits and opt-in local PCM preview come from SDK capabilities; streaming support alone never opens a live-text window. The NVIDIA model adapter chooses its initial model from saved/plugin/recommended metadata instead of a fixed model ID.

The package store validates `bundledDependencies` from manifests and excludes `isInternalDependency` packages from standalone bootstrap. NVIDIA declares its bundled CTC component; the store no longer copies or validates provider-specific dependency IDs. Invalid staged updates fall back to a validated previous package, persist a visible warning, and allow a later retry. This does not claim rollback for arbitrary plugin activation failures or plugin-owned data migrations.

Deepgram is a portable 1.1.0 package with its own build descriptor and nine tests. The real package is installed from a simulated HTTPS ZIP response and exercised through the generic registry: explicit activation, key/model persistence, restart, disable, uninstall and reinstall. Additional tests cover request content, cancellation, classified errors without provider-response leakage, and loading without WPF. Deepgram API behavior follows the [official prerecorded transcription reference](https://developers.deepgram.com/reference/speech-to-text/listen-pre-recorded). No live provider request was made. The local ZIP and checksum are in `artifacts/plugin-packages/deepgram-1.1.0.*`; no feed or release artifact was published.

Validation: 1,682 passed and one platform-specific skip across the Windows headless suites (`artifacts/test-results/plugin-expansion-verified/`; the final host suite has 217 passes). The prescribed normal-profile build/relaunch passed (`artifacts/plugin-expansion-verified-build.log`), a nonzero main-window handle was verified, and the diagnostic log is unchanged. No Computer Use was performed. Existing personal package installations and credentials were not replaced; the rebuilt NVIDIA and Groq packages are version 1.1.0 and require a package update to adopt their new capability declarations.

Scope limit: NVIDIA still uses its dedicated native inference/CTC adapter and local model settings. The generic registry already supports other PCM/downloadable engines, but this change does not move NVIDIA inference/CTC into it or connect cloud streaming. Real-key/provider acceptance and remote catalog install/update acceptance remain open. Marco separately confirmed the preceding Recorder tab-position, naming and keyboard checks.

## Stable tab placement, 2026-09-08

Section tabs now precede the headings in Recorder, History, lexicon, and integration views. Recorder keeps its navigation at the same position on Record and Recordings: the optional recording name is an input inside Record, below the tabs and heading, rather than the shared shell input. The recording form scrolls independently, with a compact timer/status area to avoid clipping in the small window. Actions remain in the footer. Existing global search bars in other sections are unchanged.

The prescribed normal-profile build/relaunch passed (`artifacts/tab-position-build.log`); the application has a nonzero main-window handle and its diagnostic log is unchanged. The source encoding test passed (`artifacts/tab-position-encoding.log`). Native acceptance is pending for stable placement when switching tabs, name entry/saving, and the scrollable recording form. No Computer Use was performed.

## Shared section tabs (`2dc89f59`), 2026-09-08

TabBar is now the shared implementation for Recorder navigation, History kind filters, lexicon sections, installed/discover navigation in both integration views, and installed-plugin filters. New section/filter tab strips should reuse this component. It uses the existing History selected/unselected styles, common spacing/minimum height, a single Tab entry point, Left/Right and Home/End navigation, and selected accessibility status. Selection changes remain owned by each existing view; programmatic selection updates do not invoke user navigation.

Recorder tabs move from the top-right title row to the left below the title. Lexicon tabs keep stable controls rather than rebuilding buttons on each render, preserving keyboard focus. Section tabs stay outside scrollable content; narrow strips can scroll to the focused item. Existing editor/detail visibility and navigation guards are retained.

775 Presentation tests passed (`artifacts/test-results/shared-tabs/shared-tabs.trx`), including the source encoding guard. The prescribed normal-profile build/relaunch passed (`artifacts/shared-tabs-build.log`) with a nonzero main-window handle and unchanged diagnostic log. These tests do not execute the WinUI tab component. Native visual alignment, tab/arrow-key navigation, integration switching, and record-stop auto-selection require manual acceptance. No Computer Use was performed.

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

The prescribed normal-profile build/relaunch passed (`artifacts/history-startup-fix.log`). The restarted process exposed a nonzero main-window handle and title `TypeWhisper Quick Launch Application`; the diagnostic log remained unchanged from before the corrected launch. Earlier History read-back launch claims based only on process existence were insufficient and are superseded by this evidence. Native History read/stop acceptance still remains open.

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

## Manual acceptance after restoring the normal development profile

The prescribed Debug build/launch succeeded after removing all `TYPEWHISPER_WINUI_*` variables from the launch environment. The previous isolated run had retained the workflow probe, which deliberately bypasses audio capture; that test process was not suitable for normal dictation acceptance. Marco subsequently confirmed that dictation works and that Groq returns the correct final text after recording stops. Groq live preview is not connected. This is user-reported acceptance, not an automated observation of microphone capture, insertion, or the transcript contents.

Shutdown fix `a6dcb8e9` skips activation and window-change handling during shutdown/profile restoration. It addresses the logged `IsAlwaysOnTop` exception during window deactivation; the build passed, but repeated native shutdown acceptance remains open. The separate LastCompletedDictation backend has a verified focused Presentation result of **735 passed**, including 20 added cases; this does not replace the complete 1,604-pass/one-skip result below or establish a working Copy-last UI.

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

Current committed implementation checkpoint: `db2c9c8e` (2026-09-07), including Recovery UI fix `108a67c`, Review action wiring `e1588aa`, persistent Cancel shortcut `b359c5a`, the initialized Review fixture `d333cb6`, plugin field layout `64dcf8d`, local spoken feedback `ff3c44b`, launcher status reporting `2879b6e`, portable model operations `bea6c08`, multiline field fixes `76c4505` and generic model UI/Session integration `3d80de1`. Functional coverage and outstanding features are tracked in the [Windows and Mac comparison](WINUI-FUNCTIONAL-STATUS.md); passing tests do not imply all displayed features are implemented.

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

## Required headless checks

```powershell
& ./eng/Test-WinUIHeadless.ps1 -Configuration Release
```

The runner executes shared Core, portable host, Presentation and discovered `plugins/*/Tests/*.csproj` suites sequentially and writes TRX files plus `summary.json` under `artifacts/test-results/winui-headless`. No desktop, Computer Use, real microphone, downloaded models or provider credentials are required.

Previously recorded complete local headless run passed **1,432 tests with one skip**: Core 522, Host 171, Presentation 605, Filler Words 59, Groq 32, Obsidian 18 and NVIDIA 25 with one skip. [Headless CI at `0e2c4fc`](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34126461982) passed **1,432 with one skip on Windows** and **1,423 with five skips on Ubuntu**. Historical [CodeQL run 34125334102 at `2d672c5`](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34125334102) also passed.

Historical CI baseline: [CI workflow](../.github/workflows/winui-headless.yml) runs on Windows and Ubuntu. At `2d672c5`, [headless run 34125333030](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34125333030) passed **1,393 tests with one skip on Windows** and **1,384 with five skips on Ubuntu**. [CodeQL run 34125334102](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34125334102) passed. These CI results precede `0e2c4fc`; they do not establish acceptance of subsequent changes or the later Review action UI.

Earlier local suite results were Core 522, Host 162, Presentation 601, Groq 32 and NVIDIA 25 with one skip. Filler Words passed 59 after a cleanup fix that explicitly verifies collectible package-context unloading. Subsequent focused runs passed Bootstrap 21, Action Registry 16, Action Controller 4, Obsidian 18 and Recovery/Delivery 45. Focused counts overlap suite cases and must not be summed into a new full-run total.

The DPAPI test is Windows-only. CUDA-specific cases remain platform-dependent; headless results do not establish GPU runtime, microphone or full application acceptance. A skipped draft CodeRabbit review is not completed review evidence.

## Current native evidence and remaining acceptance

- Brave matched the `example.com` Website rule with normal page focus. A focused address bar was rejected; `example.org` did not match. Workflow selection uses Website/App/Global rules and recording-start snapshots. Only the normalized host, not a full address/path/query, may enter History under its existing privacy choices. This bounded check does not prove every browser or tab/focus race.
- Real setup configuration and restart passed: microphone, mode, model/language, output/history choices and completion persisted. Downloads and keys remain in plugin settings; setup generates no fake transcript. Real first-dictation acceptance remains separate.
- Fresh-profile package bootstrap is restricted to NVIDIA/Groq. Portable Filler Words processing/settings/History integration is connected. Obsidian's atomic new-note writer and generic action registry/controller have focused tests. Review action UI is now wired with early drain and `OriginalText = null`; native Obsidian new-note acceptance passed with unchanged History and usage.
- Prescribed native build passed after replacing the unavailable global `WorkflowEditorStyle` lookup (a workflow-local resource) with the shared multiline style. Recovery Retry performed real Canary decoding and opened Review; Copy placed exactly the visible test text on the clipboard. Saving recovery Off with 30-day retention retained the existing 246,232-byte audio. History and Snippets hashes were unchanged. Delete default Cancel preserved the file. Closing Settings and opening a new Settings window retained Recovery Review; another real Canary Retry succeeded without a disposed-controller failure. Explicit Delete then removed only the synthetic 246,232-byte file, showed zero saved recordings and left Review text copyable.

## What the tests establish

- Metadata/identity/compatibility inspection, portable plugin activation/settings, PCM result and token-timing contracts, lifecycle failure/cancellation and disposal behavior using isolated fixtures.
- Catalog/package parsing, HTTPS/size/SHA-256 validation, unsafe archive rejection, installed-index persistence, staged update/restart and uninstall/data retention. Actual package fixture hooks exercise progress and failed operations; no provider owner's installation is removed.
- Model selection/persistence/rollback, activation retry, download progress/cancel, HTTP failures, incomplete responses and temporary-file cleanup through controlled dependencies.
- Real Groq provider multipart/WAV construction, model/language forwarding, auto-language omission, headers, auth/rate-limit/server failures, retry and cancellation through an injected HTTP handler. Windows separately verifies DPAPI persistence/corruption/removal with synthetic secrets.
- History load/search/projection, clipboard coordination through injected platform services, hotkey state transitions, preview cancellation, audio-effects/silence decisions and dictionary/snippet snapshots/persistence. Provider-first selection covers unavailable providers, missing models and fallback within a provider only.
- Managed CTC scoring/timing decisions, serialized activation/draining/stale-result handling and automatic parent-dependent enablement. These establish logic, not recognition quality.

These suites do not compile/render the full WinUI app, transcribe real cloud audio, validate microphone/keyboard drivers, publish the catalog or prove editor paste consumption. The old WPF/Core test suites are not equivalent to WinUI end-to-end coverage merely because source is shared.

## Native builds and inference

Local app builds, launches and UI smoke tests must use:

```powershell
& F:/typewhisper/typewhisper-dev-tools/build-typewhisper-windows-dev.ps1 --run --winui <checkout>
```

Use the current checkout/worktree, not a transient build output or the installed production app. Output is `F:/typewhisper/dev-output/typewhisper-win/WinUI`.

Optional native inference is documented in [audio tests](../tests/TypeWhisper.Dictation.AudioTests/README.md) and [CTC probe](../tests/TypeWhisper.ParakeetCtc.Probe/README.md). These load published plugin folders and existing isolated weights. They are separate from headless CI and require explicit environment configuration; do not download models or use credentials just to run the default suite.

## Existing bounded native evidence

These are earlier implementation-session checks, not rerun during the documentation audit:

- The prescribed development build/launch passed. Owner feedback confirmed immediate dictation, hybrid hold/tap, whole-text insertion and plugin toggle behavior.
- Published Parakeet/Canary generated-speech inference passed, including model/language metadata. Canary downloaded anonymously and selection survived restart. UI cancellation was not confirmed before completion; cancellation evidence comes from headless tests.
- CTC produced finite emissions for official English fixture audio, accepted a positive correction and rejected the inverse hint through the published portable package. Whole-utterance fixture timings do not prove real TDT/microphone alignment. Broader German, names, vocabulary, false positives and FluidAudio equivalence remain unverified.
- Earlier timing, adaptive candidate/scoring and diagnostics regressions are covered by the managed/native fixtures. Development diagnostics are bounded/rotating and record alignment/scoring metadata, not additional audio or transcript text. Per-term threshold UI and real vocabulary success need their own acceptance evidence.
- Earlier enable/disable, keyboard focus and restart checks predate the final internal-CTC presentation. Current headless tests enforce parent-controlled CTC; do not reproduce the removed standalone toggle as today's UI.
- Synthetic Groq key editing, encrypted save/enable/removal passed during setup development. Later Installed/Discover and provider-settings navigation checks showed the two actual integrations. A configured/ready key is not evidence of a successful live Groq transcription.
- The uninstall dialog was inspected and canceled. Actual uninstall/reinstall mutations use isolated package fixtures. The last live Discover check returned HTTP 404 for `plugins-v2.json`; published-feed acceptance remains open.

No personal keys, recordings or weights belong in test fixtures, logs, commits or screenshots. The documentation audit changes no application state.

## Outstanding acceptance gates

- [ ] Resolve the Recent transcriptions shortcut save failure or missed click, then verify native save, restart, invocation and workspace guards.
- [ ] Real local and Groq dictation through install/configure/select/record/insert/history, with accurate metadata, failure/cancel and provider switch-back.
- [ ] Published v2 install/use/update/restart/uninstall/reinstall, including retained configuration/models and failed hook/update recovery.
- [ ] Real microphone changes, unplug/replug, sleep/resume, sound devices, media/ducking restoration and short quiet utterances.
- [ ] Rich clipboard formats, focus changes within/across windows, editor consumption, modifier races and full real-dictation review-first acceptance; delivery is already wired.
- [ ] Complete broader recovery failure-path acceptance beyond the passing native Retry/Copy, Settings reopen and Delete Cancel/confirm flow; continue remaining audio and end-to-end file/recorder/workflow checks.
- [ ] Full keyboard/screen-reader/high-contrast, actual 200% DPI and mixed-monitor transitions. Logical viewport previews are not OS DPI tests.
- [ ] Actual WinUI release build/installer/update/rollback and each claimed OS/architecture; headless CI is not a WinUI build gate yet.
- [ ] Deferred controlled legacy-versus-WinUI benchmark for PR #447: same audio/model/backend/thread settings, separate warm-up and steady state, capture onset, insertion latency, memory and explicit uncertainty.

For each new feature, test observable state and failure/race outcomes behind portable boundaries first, then validate the Windows adapter separately. Avoid tests that only repeat a UI implementation or use simulated output as transcription evidence.

## Historical validation log

The following sections record earlier implementation checkpoints. Their counts, open-at-the-time features and UI observations are historical; the current checkpoint and unresolved failures above take precedence. They are not cumulative test totals.

### Historical output settings slice (2026-09-07)

Sixteen new `DictationOutputTests` exercise the actual portable delivery boundary: independent history/paste choices, failed/throwing history and paste, changes while history loads, recording-start restrictions, restart, corrupt/incomplete/unreadable preferences and atomic-save failure cleanup. After the final preferences change, all 112 Presentation cases passed; the preceding full headless run passed all four suites (112 host, 111 Presentation, 29 Groq, 25 NVIDIA, one platform skip). The additional unreadable-path case brings the tested total to 278 passed, one skipped across those runs.

The real dev build exposed a pre-existing missing `TabViewScrollButtonBackground` resource during startup. Explicitly merging `XamlControlsResources` restores the host window. Computer Use verified the real output picker, persisted Review first selection and restoration of Insert directly, and the Privacy page with unavailable retention/memory/learning controls disabled. History settings were not changed through Computer Use; their write behavior is tested with isolated services.

For a repeatable visual check of the real review window without microphone/cloud/history changes, Debug builds accept `TYPEWHISPER_WINUI_REVIEW_FIXTURE=1` in the environment of the prescribed `--run --winui` build script. It opens a labeled synthetic result using the same window as real delivery. Remove the environment variable after launch; Release builds ignore it. This fixture is UI evidence, not real-dictation acceptance.

The Debug review fixture rendered successfully with both paragraphs after fixing TextBox initialization order (enable multiline before assigning text) and removing the one-line search template. The Copy text click could not be verified because Computer Use could not activate that window; no successful copy acceptance is claimed. Full review-after-real-dictation acceptance remains separate from the fixture.

Window-title appearance: the prescribed Debug build/launch passed with shared title-bar styling applied to all six WinUI window classes. Computer Use verified that the review window caption stays dark both inactive and with the result field focused. No new headless tests were added for this appearance-only change.

## Dictation provenance, 2026-09-07

Provider adapters now preserve detected language; the session records the captured target process and resolves provider language names/codes without guessing automatic input. Targeted cloud/local adapter tests passed (23 cases), and language provenance tests passed (9 cases). These are portable fixture tests, not live provider acceptance. App/task presentation follows in the history-actions slice.

## Recording modes and history actions, 2026-09-07

The combined Presentation suite passed 153 cases, including mode state transitions/persistence and history edit/delete/export failure paths. The prescribed Debug build/launch passed. Computer Use opened the advanced Recording mode picker, selected Toggle and verified the persisted profile file. Actual physical tap/hold microphone acceptance remains separate from these state-machine tests.

## Connected settings and native history fixture, 2026-09-07

The full portable headless run passed 373 cases and one platform skip (Host 115, Presentation 204, Groq 29, NVIDIA 25). The new Groq request regression was then moved into the actual plugin-owned suite and all 32 Groq cases passed. Source-kind/filter cases brought the subsequent Presentation run to 215 passing cases. This is incremental evidence; do not describe it as one 387-case full run.

The prescribed Debug build/launch passed with an opt-in `TYPEWHISPER_WINUI_HISTORY_FIXTURE=1`. In Debug only, this creates one synthetic entry in a new temporary history store and opens History; normal startup and Release never seed it. Computer Use opened details and the editor, confirmed both paragraphs, inserted a test prefix and saved it. Reading the temporary JSON confirmed the edit persisted while raw text and model/app metadata stayed unchanged. No personal history, audio or provider request was used for this editing test. The same fixture was exported through the native save picker to a new test artifact: both paragraphs survived. Confirmed deletion removed only the temporary entry, verified by reading the empty fixture JSON. The app was then relaunched without the fixture. Failure and restart behavior are covered separately by portable tests.

Native translation reuses provider capability metadata. Groq translation requests omit the input-language field, following the [Groq API reference](https://console.groq.com/docs/api-reference) and [speech-to-text guide](https://console.groq.com/docs/speech-to-text); transcription still sends the selected input language. Unsupported models reject translation before audio encoding/inference.

## Lexicon transfer, 2026-09-07

The Presentation suite passed 232 cases after adding dictionary/snippet import/export coverage, including verified Mac dictionary JSON, preserved metadata, duplicate/conflict rejection, explicit replacement, pack retention and atomic export failure cleanup. The prescribed WinUI Debug build/launch passed. Native import/replacement acceptance remains to be exercised with a fully isolated test profile.

## Isolated native profiles

Debug builds accept `TYPEWHISPER_WINUI_TEST_PROFILE=<name>` when launching through the prescribed script. Use a unique name of up to 64 ASCII letters, digits, hyphens or underscores. Preferences, history, lexicon, plugin registrations, secrets and model assets then live under `%TEMP%/TypeWhisper-WinUI-TestProfiles/<name>`. Restart with the same name to verify persistence; omit it to return to the normal development profile. Release ignores this variable. Test profiles do not reuse the normal model asset directory or existing credentials. The older history-only fixture remains available for focused transcript tests.

The normal profile's recording mode was restored to Hybrid through the real picker and its JSON was verified. Statistics rendered actual retained-history totals and the history-based limitation text.

## Runtime ownership and snippet usage, 2026-09-07

All 133 portable host cases passed after adding actual registry-backed cloud adapter tests, including saved configuration, failed writes, serializing requests/configuration, cancellation/draining before uninstall, shared LLM ownership and a pumping UI synchronization context. No HTTP requests or real credentials were used. The app built and launched with the named temporary profile. The separate Presentation run passed 252 cases for profile isolation and snippet usage/concurrent edits; subsequent formatting regressions passed 271 cases.

Computer Use imported a synthetic dictionary containing an umlaut term and a correction, then a multiline snippet into `smoke-20260907`. The temporary JSON retained both dictionary records and the exact two-line replacement; the initial snippet usage count was zero. Personal profile files were not changed.

## Formatting and asynchronous shortcut intent, 2026-09-07

The Presentation suite passed 285 cases, including engine/model/language-specific formatting profiles, output-language handling, ordered application/command steps, immutable saved choices and 14 asynchronous input coordination cases. The prescribed Debug build/launch passed after wiring the real settings UI. The input coordinator retains a release or cancellation while capture starts, gives cancellation precedence and discards starts during final processing instead of queuing a later recording. Real microphone tap/hold acceptance remains separate from these deterministic task/dispatch tests.


## 2026-09-07: manual workflows and quiet-recording policy

At `a28367e3`, the complete local headless command passed **517 tests**: Host 135, Presentation 325, Groq 32 and NVIDIA 25. One platform-dependent NVIDIA case was skipped. The prescribed Debug build and launch also passed. These results cover the committed quiet-clip policy, provider no-speech metadata and manual workflow slice together.

Computer Use in the named `smoke-20260907` profile created a custom workflow with two instruction paragraphs, saved it without a provider, verified that Run stayed disabled with source text, relaunched the app, observed the persisted workflow, disabled and saved it, then confirmed deletion. The profile JSON retained both instruction lines, recorded `IsEnabled: false` after the toggle, and contained an empty array after deletion. No cloud request or personal credentials were used. Provider execution, exact selection, cancellation, empty/error results and persistence failures have portable coverage; a successful live Groq workflow request remains acceptance work.

Quiet recognition defaults off, matching the previous Windows capture policy. Captures below 40 ms are skipped; pre-gain RMS thresholds are 0.003 below one second and 0.006 otherwise. Silence padding is applied only to decoder input, preserving original history duration and CTC sample positions. Final provider no-speech probabilities are preserved and filtered at the previous Windows threshold; missing values stay unknown. Empty final text is still discarded instead of substituting preview text. Real microphone/quiet-speech acceptance is not established by these policy tests.

## 2026-09-07: real file requests and additional preferences

The next complete local run passed **560 tests**: Host 147, Presentation 356, Groq 32 and NVIDIA 25, with one NVIDIA platform skip. Host coverage includes real Windows Media Foundation decoding of a synthetic 48 kHz stereo WAV to 16 kHz mono, cancellation, missing/invalid files and unchanged source bytes. Queue tests cover serialization, retry, late result/error/progress rejection and actual subtitle segments. The prescribed Debug build/launch passed for the file implementation committed as `aeeeaea`.

Native checks in the named profile saved text size 14 and result duration Immediately (`overlay.json`: 14 and 0). The file view accepted `file-smoke.wav` through the native picker, displayed Queued, kept Start disabled without a ready model, and removed the queue entry while retaining the 38,444-byte source. These checks do not establish model inference or subtitle export through the new file screen. No model or cloud credentials were used.

Ordered language hints use the new explicit SDK capability; an explicit language takes precedence. Audited Gemini/Meta/Soniox source implementations advertise it but are not yet portable WinUI packages. NVIDIA/Groq correctly keep the controls unavailable. Overlay text size and successful-paste duration persist; zero hides immediately, while errors remain visible for five seconds.

Ubuntu CI at `09bc34b6` exposed two workflow tests relying on Windows file-share locks. Commit `eadf18b` replaces their failure injection and checks unchanged snapshots/bytes. The focused Presentation suite passed locally; Ubuntu then passed at `1c253f13`. The same run exposed a Windows-only Groq test teardown lifetime issue, corrected by `be1e0498` with an explicit collectible-context release assertion.

File requests are limited to 20 queued files and 60 minutes per file. They use the selected provider and real SDK segments, optionally save file-source History and never auto-paste. Opt-in queue persistence and per-file dictionary/snippet processing are connected; eligible local Parakeet Transcribe requests can use CTC timings. Restored results never replay History or usage side effects. Watch folders remain open.

## 2026-09-07: cancellation and application shutdown

The complete local headless run for `beee306` passed **569 tests** (Host 147, Presentation 365, Groq 32, NVIDIA 25) with one NVIDIA platform skip. The prescribed Debug build/launch passed. New coverage exercises cancellation before output, linked file requests, repeated/reentrant shutdown, late native completion and permanent queue closure. Application shutdown cancels all owners and awaits initialization, active processing and file/hotkey drains before releasing audio, insertion and plugin resources. The tray exposes Cancel processing independently from capture state. Durable recovery is not part of this slice.

Commit `be1e0498` fixes Groq package test teardown by ending the package-owning stack frame and asserting actual collectible load-context release before directory deletion. All 17 focused cloud tests passed. Fresh Windows and Ubuntu headless CI passed at `536f7905` ([run 34107868683](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34107868683)); CodeQL also passed. No cleanup exception is suppressed. CodeRabbit skipped the draft PR, so its successful status is not a completed review.

## 2026-09-07: native file inference and TXT export

Computer Use selected the copied Canary 180M Flash model in the isolated `smoke-20260907` profile, selected English, imported a synthetic Microsoft SAPI English voice WAV, and started the real file queue. The result contained the complete test sentence; the shared number formatter changed `ten` to `10`. The profile history retained raw/final text separately with `SourceKind=file`, `EngineUsed=sherpa-onnx`, `ModelUsed=canary-180m-flash`, language `en` and 7.6933125 seconds. Native TXT export produced the same final text in a 95-byte file. Canary returned no usable segments, so the screen correctly withheld subtitle formats. No microphone or cloud request was used.

The earlier attempt to synthesize German with an installed English-only SAPI voice returned no speech. That exercised visible failure and explicit retry, but it is not German recognition acceptance. A separate probe of the published plugin and the same Media Foundation decoder confirmed nonzero PCM and complete English recognition. Existing local model weights were copied into the test profile; no personal preferences, keys or history were copied. This checks the running `beee306` application build and its installed NVIDIA package; later source changes still require their own build.

## 2026-09-07: recorder and CTC integration checks

The first new native recorder check used only system output in the isolated `smoke-20260907-r2` profile. Playing the synthetic English fixture produced a real mono 16 kHz WAV with nonzero samples. The explicit Transcribe file action queued that WAV without starting it; choosing Start with Canary then produced the complete fixture sentence. The result displayed `NVIDIA · Canary 180M Flash · 00:00:08`. Model activation left only the main application window visible. This run exposed missing loopback silence: the initial WAV contained 8.11 seconds of received packets instead of the full capture interval. A monotonic timeline correction and targeted tests follow; this initial run does not establish timing or combined-source acceptance.

Native testing also exposed an invalid WinUI selected-items mutation in single-selection mode, fixed by clearing SelectedIndex before switching to multiple selection. The real pinned CTC BZip2 archive downloaded and passed its SHA-256 check, but archive auto-detection failed with `Failed to read TAR header`. Explicit BZip2 decompression followed by bounded TAR reading replaced that path. The actual archive was independently decoded; all seven compressed-archive provisioning tests passed after finalizing the test compressor correctly. Fresh native setup and history selection checks remain required for these corrections.

The corrected build underlying `a3045411` passed the prescribed build/launch. The full headless run passed **611 cases** (Host 160, Presentation 394, Groq 32, NVIDIA 25; one NVIDIA platform skip), and 64 focused Windows capture/audio cases passed in Debug. The legacy Release test build required unavailable ARM64 redistributable files; this does not establish ARM64 distribution validation.

In fresh `smoke-20260907-r3`, Computer Use enabled selection, selected all three synthetic history rows and exported exactly those rows to TXT. Enter accepted the default Cancel action and retained all three. Deselecting the first and confirming deletion removed only the other two; Escape left selection mode without leaving History. Confirmed clear-history removed the last row and the persisted JSON became `[]`. Native first-run CTC setup downloaded the 104,337,827-byte pinned BZip2 archive, extracted the model, downloaded/verified the tokenizer and displayed NVIDIA Ready plus the included dictionary-boosting message. No account credential was used.

A second system-only capture named `Zeitachse Prüfung` lasted 47.713 seconds as measured around the native Start/Stop actions; its WAV contained 47.6324375 seconds at mono 16 kHz. Non-silent fixture audio began at 18.3954375 seconds and ended at 25.229625 seconds, confirming that both leading and trailing silence remained. The German title persisted in the filename and the UI displayed `00:00:47`. Combined-source synchronization currently uses callback receipt times with 50 ms jitter tolerance because this adapter does not expose device timestamps; no sample-exact synchronization claim is made.

## 2026-09-07: library and accepted file processing

The prescribed Debug build underlying `3728d98` passed; the complete portable suite passed **640 tests** with one NVIDIA platform skip (Host 160, Presentation 423, Groq 32, NVIDIA 25). New cases cover WAV metadata/corrupt files/contained deletion, queued-source protection, deletion of the current saved result, lexicon snapshots, failed clipboard/usage writes and the final History/usage acceptance boundary. Cancel before acceptance leaves neither History nor usage; a reentrant History notification cannot revoke an already accepted result. One non-blocking xUnit style warning remains in the library test.

Computer Use reopened the isolated `smoke-20260907-r3` profile and found the actual 47-second recording plus a synthetic seven-second fixture in Saved recordings. Library mode removed the underlying capture controls from the accessibility tree. Transcribe file queued the fixture without starting. Explicit Start invoked Canary and transformed the recognized `next steps` through a snippet (`review milestone`) and then a correction (`verified milestone`). History retained the original sentence, final text, `sherpa-onnx` / `canary-180m-flash`, `en` and 7.6933125 seconds. Snippet usage was exactly one after viewing the result and returning to the library; Canary still correctly offered TXT only without usable provider segments.

Deleting the queued fixture showed an explanatory refusal and kept the WAV. Enter canceled the other recording's deletion dialog and preserved both files. Explicit confirmation then deleted only that other synthetic recording, and the library showed one remaining file. No personal recording, microphone capture or cloud call was involved. Native default-player launch, source-device choice, broader formats and restart recovery are not covered by this run. The latest prior remote baseline `905434c5` passed [Windows/Ubuntu headless CI](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34113096006) and [CodeQL](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34113096762).

## 2026-09-07: file checkpoint restart and selected recorder endpoint

The complete portable suite passed **673 tests** (Host 160, Presentation 456, Groq 32, NVIDIA 25; one NVIDIA platform skip). An additional **68 Windows audio tests passed** in Debug, including selected-endpoint creation and retryable device disposal. The prescribed Debug build/launch passed for the implementation through `cd1e0204`. The earlier library xUnit style warning was corrected.

In the isolated `smoke-20260907-r3` profile, Computer Use enabled queue recovery and explicitly transcribed the synthetic library fixture again with Canary. The checkpoint contained one Ready result with a Completed side-effect receipt. History and snippet usage were both two after these two deliberate runs. After the prescribed restart, the completed result reopened with the same text and model, Start remained disabled and both counts stayed two. Turning recovery off persisted `Enabled: false` and an empty Jobs list while the in-memory result remained available. Pending-receipt crash cases, corrupt-file reset and changed/missing sources have portable coverage; no native crash or cross-store exactly-once guarantee is claimed.

Recorder microphone Off and system audio On were reflected in Settings and persisted. Selecting the actual Creative Pebble Pro endpoint, then restarting, retained the exact endpoint ID and both source choices. A new system-only recording named `Gerätetest` captured the synthetic English SAPI fixture and saved **33.2079375 seconds** of mono 16 kHz PCM with RMS **0.043014**. The recorder returned to Start after saving. No microphone, cloud request or personal recording was used. This establishes the selected-device path and persisted choices, not hot-unplug or sample-exact dual-device synchronization.

The prior pushed checkpoint `e45abf6f` passed [Windows/Ubuntu headless CI](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34114591033) and [CodeQL](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34114592095). Native History-audio default-player launch has since passed with an isolated synthetic WAV; broader hardware/editor acceptance remains open.

## 2026-09-07: templates, automatic workflow rules and local backup

The first expanded local headless run passed **1,202 tests** (Core 501, Host 160, Presentation 484, Groq 32, NVIDIA 25; one NVIDIA skip). The first compile attempt found a nonexistent workflow trigger property, corrected to the actual hotkey collection before this passing run. The prescribed WinUI build passed after correcting the live-region enum namespace. A separate 25-case sync test run passed after Ubuntu CI exposed a platform-specific path validation assumption. At `77763cfd`, [headless CI](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34119041476) passed **1,214 cases with one skip on Windows** and **1,205 with five skips on Ubuntu**, including all 513 Core cases. Previous Windows audio evidence remains 68 passing cases; this slice does not rerun acoustic acceptance.

Computer Use created `Vorlagenprüfung` using Translation, left fine-tuning empty, saved target `Deutsch`, reopened it, changed the draft target to `French` and discarded that change through Escape and explicit Discard. The saved JSON retained `Deutsch`. Automatic App/Global precedence, immutable prompt snapshots, ordered LLM processing, late cancellation and actual delivery refusal on workflow failure have portable coverage. No successful cloud call is claimed.

In `smoke-20260907-r3`, native backup export kept History deselected and wrote the actual dictionary, snippet and workflow into `backup-r3-native.json`. A synthetic variant contained one new item per category. The actual preview reported three files and one added item in each category; no profile data changed. Enter selected the default Cancel and retained counts 1/1/1 with two History rows. Explicit Restore then produced counts 2/2/2, closed the app and removed the completed journal. The History SHA-256 stayed `8EBE5B3F4362E2EFA62D8A65C72A9B1B676382A414FE7F8D8AE5F24EDD55AA1F` before and after. The imported dictionary threshold was 0.62. No personal data or credentials were used.

A separate `backup-blocked-20260907` profile contained only a deliberately malformed `.profile-restore/journal.json`. Native startup displayed the dark `Profile restore` window with a clear blocking message and optional technical details; Main, Settings, model initialization and profile stores did not open. The folder still contained only the unchanged fixture transaction. Closing that window ended the app. Portable recovery cases cover interrupted publication, exact rollback bytes, committed cleanup, safe preparation remnants, stale previews and preserved unknown contents. Native forced-crash and recorder-save-failure acceptance remain open; code review additionally closed hotkey/retention/UI admission before the recorder save retry boundary.

The prescribed restart returned to `smoke-20260907-r3` and showed both `Vorlagenprüfung` and `Wiederherstellungsprüfung` as saved Translation workflows. The restored dictionary retained its 0.62 CTC threshold. [CodeQL](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34119042236) also passed at `77763cfd`.

## 2026-09-07: setup, development startup and visible unsupported rules

At `85d53e2`, [headless CI](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34121574678) passed **1,251 tests with one skip on Windows** and **1,242 with five skips on Ubuntu** (Core 513, Presentation 521; platform-specific host/provider counts differ). [CodeQL](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34121575505) passed. Focused local runs passed 60 workflow/startup, 78 History and 35 setup/startup cases before subsequent development. Prescribed Debug builds passed.

In `smoke-20260907-r3`, Computer Use verified the disabled startup control and its isolated-profile explanation. The published-output receipt contained this checkout and the prescribed external output directory. No actual registry registration or Windows sign-in test was performed.

A synthetic backup added one unsupported App workflow. An initially malformed test backup was rejected visibly; correcting it produced a one-file preview and a successful confirmed import. The restarted list showed all three workflows, including the unsupported entry. Edit and Run were disabled. Native enable/disable changed only `IsEnabled`; a serialized comparison retained all other metadata, including the unknown settings value and action-plugin ID. The fixture was left disabled. History retained its prior SHA-256.

The real setup wizard listed three connected microphones. Selecting HyperX QuadCast 2 saved its actual endpoint ID; selecting Toggle wrote the recording-mode preference. Canary offered English, German, French and Spanish. German and Review first persisted, Finish wrote `Step=4, Completed=true`, and the Dictation page reflected both choices. A prescribed restart retained microphone, mode, model, German, Review first and completion. No microphone or cloud transcription was performed by setup. An earlier native App-rule edit also checked missing-process validation, then persisted disabled `notepad` with priority `-5`.


## History footer actions (layout and action shortcuts confirmed by Marco)

History list and detail actions now share a fixed bottom action area. Detail shortcuts are Enter (copy), E (edit), X (export), Delete (confirmation), P (saved audio), and F (audio folder). Selection supports S, A, X, and Delete. Text inputs, modifiers, dialogs, and flyouts keep their own keyboard handling; Enter on a tab-focused button invokes that button. Opening a transcript moves focus out of search to its primary action.

The prescribed development build and launch succeeded on 2026-09-07 (artifacts/history-footer-build.log). No automated native click or keyboard acceptance was performed. Marco should verify the footer at the normal window size, keyboard copy/edit/export, Delete followed by cancellation, text entry in the editor, and Tab/Shift+Tab navigation. Also verify the History shortcut with Settings open and that Groq shows no live transcript while recording but still returns its final text after stopping.

Product convention: place page actions at the bottom, expose keyboard shortcuts, and use Enter for the primary action while preserving text editing and focused-control behavior. This change applies that convention to History; it does not claim a completed application-wide audit.

## Watched-folder and direct file-processing acceptance (2026-09-08)

Use an isolated Debug profile and locally synthesized speech for unattended native checks. The `watch-folder-20260908` run used a copied Canary 180M model and no cloud credentials. Do not point an automatic watcher at personal folders during smoke tests.

- Files > Watch folder: choose an input folder; blank output defaults to its Transcripts subfolder. Start, add a completed WAV, and verify one real TXT export and an unchanged original.
- Open the result in one click, copy with Enter, return with Escape, then alternate Enter Start/Pause. Focus must remain on the current primary action rather than the title-bar controls.
- A normal restart retains the result but does not resume unless startup watching was enabled. Startup resume must also work while Quick Launch is the visible page.
- Recorder > Recordings > Transcribe starts only that recording and opens the resulting transcript. Other queued files must not start implicitly.
- Automated suites cover source stability, exclusive-write contention, interrupted requests, late-result cancellation, atomic progress failures, safe export recovery, folder isolation, Unicode, collisions and real-timestamp subtitle requirements.

Evidence: `artifacts/watch-folder-native-evidence.json`, `artifacts/watch-folder-headless-final.log`. The native batch export picker and newly recorded microphone/system-audio quality remain outside this acceptance run.

## Learned-correction overlay feedback (2026-09-08)

Successful automatic dictionary additions reuse the recording overlay at its configured anchor. The acknowledgement shows the original and replacement, with a left-shrinking countdown for 12 seconds. It does not activate the window or open live transcription. A new dictation cancels feedback and restores the recording layout. Duplicate or unsaved corrections do not produce success feedback.

Native acceptance in the isolated correction-learning profile verified a real Notepad correction from teh to the, the saved mapping and animated countdown in the existing overlay, retained editor focus, and automatic dismissal. Custom anchors, multiple monitors and every recording-overlay mode were not exercised in this run.

## Guided word training (2026-09-08)

Dictionary > Words or Corrections > Train word opens a three-sample workflow using the current ready model and selected microphone. Enter one correctly spelled word, choose German or English example sentences, record each sentence, and review the recognized variants before saving. Record again starts a replacement take directly. The selected sentence language is passed through the engine's language-hint capability. Training uses raw transcription, without CTC, dictionary correction, snippets, workflows, History, saved audio or automatic paste.

The recorder reservation excludes competing dictation, recorder, file and model operations for the wizard's lifetime. Samples stop automatically after 30 seconds. Cancel/shutdown cancels and drains decoding before releasing the reservation. Saving rereads the dictionary, validates conflicts and writes the word plus approved unique variants in one dictionary replacement. Existing disabled entries stay disabled; ambiguous sentence differences do not generate candidates.

Validation: 38 focused Presentation tests passed (DictionaryTrainingTests and FileLexiconTests), covering punctuation/case, ambiguous changes, Unicode words, reviewed-only saves, duplicates, disabled entries, a concurrent conflict and a corrupt dictionary. The prescribed build/relaunch passed. Native UI checks covered setup, sample navigation, actual microphone start, Escape during recording and reopening after cancellation. No sample was sent for transcription during this native check and no dictionary entry was saved. Complete three-sentence recognition, review/save and cancellation during a slow provider request still need native acceptance.

## HTTP API real-app acceptance (2026-09-08)

The initial connected endpoints are status, models, capabilities and uploaded/local-file transcription. 90 focused Presentation tests passed (20 real-loopback host, 53 parser/formatter, 17 existing file-lexicon regression cases). Native acceptance used the isolated http-api-20260908 profile with Canary 180M and previously synthesized English speech; no personal recordings or cloud keys were used.

The actual app returned matching text for raw WAV, multipart and local-path requests. Tests verified Unicode-compatible response encoding and lowercase segment fields, language normalization, malformed audio 422, missing/incorrect token 401, browser Origin 403, unknown options 400, model mismatch 409, wrong method 405 and unknown route 404. Concurrent native requests returned one 200 and one 409. Source/History hashes remained unchanged and temporary uploads were removed.

The prescribed-build lifecycle script verified Mac-compatible api-port/api-discovery.json, a protected owner-only Windows ACL, disabled-startup cleanup, removal of synthetic stale discovery after malformed settings, and a stable DPAPI-backed token after restart. Normal-profile launch was restored in finally. Evidence: artifacts/http-api-tests.log, artifacts/http-api-lifecycle-evidence.json and artifacts/http-api-lifecycle-run.log. Transport cancellation and active-backend drain have loopback coverage; no real cloud-provider, native forced-shutdown-during-inference or unimplemented endpoint acceptance is claimed.
