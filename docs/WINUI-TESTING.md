# Testing the 1.1 application

Current implementation checkpoint: `ff77f43a` (2026-09-06). Functional coverage and outstanding features are tracked in the [Windows and Mac comparison](WINUI-FUNCTIONAL-STATUS.md); passing tests do not imply all displayed features are implemented.

## Required headless checks

```powershell
& ./eng/Test-WinUIHeadless.ps1 -Configuration Release
```

The runner executes portable host, Presentation and discovered `plugins/*/Tests/*.csproj` suites sequentially and writes TRX files plus `summary.json` under `artifacts/test-results/winui-headless`. No desktop, Computer Use, real microphone, downloaded models or provider credentials are required.

[CI workflow](../.github/workflows/winui-headless.yml) runs on Windows and Ubuntu. At [run 34060774341](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34060774341), verified from logs:

| Suite | Windows passed / skipped | Ubuntu passed / skipped |
|---|---|---|
| Portable SDK / host | 112 / 0 | 111 / 0 |
| Presentation | 96 / 0 | 96 / 0 |
| Groq plugin | 29 / 0 | 29 / 0 |
| NVIDIA/Sherpa plugin | 25 / 1 | 21 / 5 |
| Total | **262 / 1** | **257 / 5** |

The DPAPI test is compiled on Windows only. Five CUDA-specific cases require Windows x64; the complementary unsupported-platform test runs elsewhere. These tests do not install a real GPU runtime. [CodeQL run 34060775205](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34060775205) passed. CodeRabbit skipped the draft review; its green status does not establish review completion.

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

- [ ] Real local and Groq dictation through install/configure/select/record/insert/history, with accurate metadata, failure/cancel and provider switch-back.
- [ ] Published v2 install/use/update/restart/uninstall/reinstall, including retained configuration/models and failed hook/update recovery.
- [ ] Real microphone changes, unplug/replug, sleep/resume, sound devices, media/ducking restoration and short quiet utterances.
- [ ] Rich clipboard formats, focus changes within/across windows, editor consumption, modifier races and review-first behavior once wired.
- [ ] Durable recovery, history mutations/retention/audio, real files/recorder/workflows and all remaining data services as they are integrated.
- [ ] Full keyboard/screen-reader/high-contrast, actual 200% DPI and mixed-monitor transitions. Logical viewport previews are not OS DPI tests.
- [ ] Actual WinUI release build/installer/update/rollback and each claimed OS/architecture; headless CI is not a WinUI build gate yet.
- [ ] Deferred controlled legacy-versus-WinUI benchmark for PR #447: same audio/model/backend/thread settings, separate warm-up and steady state, capture onset, insertion latency, memory and explicit uncertainty.

For each new feature, test observable state and failure/race outcomes behind portable boundaries first, then validate the Windows adapter separately. Avoid tests that only repeat a UI implementation or use simulated output as transcription evidence.

## Output settings slice (2026-09-07)

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

File requests are limited to 20 queued files and 60 minutes per file. They use the selected provider and real SDK segments, optionally save file-source History and never auto-paste. Queue persistence, watch folders and file-specific CTC/dictionary/snippet stages remain open.

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

The prior pushed checkpoint `e45abf6f` passed [Windows/Ubuntu headless CI](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34114591033) and [CodeQL](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34114592095). Native default-player launch and broader hardware/editor acceptance remain open.
