# Windows 1.1 progress

Current connected-settings checkpoint: `5d72fca` (2026-09-07). Release-wide draft: [PR #447](https://github.com/TypeWhisper/typewhisper-win/pull/447), `seofood/release-1.1` against `main`.

The authoritative feature inventory is now the [full comparison against both previous Windows and Mac](WINUI-FUNCTIONAL-STATUS.md). This replaces the accumulated, contradictory milestone checklist. Historical test counts and superseded UI decisions remain available in Git history; they are not current completion claims.

## Latest slices: runtime settings and history (2026-09-07)

Persisted Hybrid/Toggle/Hold modes, number/short-punctuation/regional preferences and native English translation now control processing. Snippets run before corrections. History supports edit, confirmed delete, atomic export and explicit retention; dashboard/statistics derive only from retained history. New dictations preserve language, target process and source kind. An opt-in Debug history fixture supports native editing tests without changing profile history.

### Earlier output-preferences slice

Automatic paste / Review first and Save to history now persist and control actual delivery independently. Review and failed-delivery results remain copyable without a history write. Sixteen new portable cases cover combinations, restrictions during processing, restart, malformed/unreadable files, failed saves and failed paste. Memory, correction learning and exact-field locking are disabled; retention was connected in the subsequent slice. WinUI standard control resources are now explicitly merged, fixing startup failure on `TabViewScrollButtonBackground`.

## Connected today

- Real hotkey microphone dictation through NVIDIA Parakeet/Canary or Groq, local live preview, persisted provider/model/language selection and history before whole-text paste.
- Main Dictation and Quick Launch shortcuts, microphone priority, sound/output preferences, whisper mode, media pause/ducking and silence auto-stop. Overlay configuration and provider configuration are separate from recording state.
- Dictionary terms/corrections, noncommercial built-in term packs, snippets with recording snapshots and automatic internal Parakeet CTC. Snippet usage counts and full text-pipeline parity remain open.
- Isolated history read/search/raw-final details/copy/edit/delete/export, explicit retention and actual usage aggregation. Model, provider and app/task appear in details only.
- Integrations with Installed and Discover. Only NVIDIA Parakeet and Groq have connected runtime bindings. Plugin settings own model downloads and credentials; Dictation selects provider/model. There is no global Models settings page or independent CTC integration.
- Persistent package installation/uninstallation, staged updates, checksums and extraction/identity validation. Plugin-owned output folders/tests and optional install/uninstall hooks with status messages. See [package contract](PLUGIN-PACKAGES-1.1.md).
- Headless CI for portable host, presentation and plugin-owned suites on Windows and Ubuntu.

## Remaining release work

These are major implementation areas, not final polish. Detailed per-feature gaps, reference sources, a 50-row grouped plugin inventory and acceptance criteria are in the full comparison.

- [x] Connect Review first / AutoPaste and history saving with persistence and delivery tests.
- [x] Connect recording mode, native task, number/punctuation/regional preferences and explicit retention.
- [ ] Connect language hints and remaining runtime settings; make other preview controls unambiguous.
- [ ] Connect complete text processing, generic plugin capabilities, cancellation, queued jobs, durable recovery and accurate provenance.
- [ ] Replace workflow examples with persistent workflows, triggers, LLM processing, selected-text execution and retry.
- [ ] Replace file/recorder simulations with real decoding/capture, output files, transcription jobs, subtitles and watch folders.
- [ ] Complete remaining history bulk/audio actions, correction learning, backup and sync; mutation/export/retention/statistics are connected.
- [ ] Generalize provider/settings/contribution integration and rebuild the selected additional plugins; publish and verify the single v2 catalog end to end.
- [ ] Connect onboarding, licensing, updates, autostart, localization, API/CLI and Windows shell activation.
- [ ] Decide which Mac additions belong in Windows: Inbox/audio sync, meeting automation, live field text, media imports and platform-specific alternatives.
- [ ] Complete OS/architecture/distribution decisions, native accessibility/DPI/device/editor acceptance and the deferred legacy-versus-WinUI benchmark.

## Evidence

At `ff77f43a`, [headless CI](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34060774341) passed on Windows (262 passed, 1 skipped) and Ubuntu (257 passed, 5 skipped). [CodeQL](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34060775205) passed. CodeRabbit skipped review because the PR is draft; its successful status is not a completed review.

Earlier local build/launch, native inference, owner feedback and focused UI checks are scoped in [testing](WINUI-TESTING.md). The last live v2 Discover check returned HTTP 404. Package operations have fixture coverage, not published-feed acceptance. No full real Groq dictation acceptance is recorded. This documentation audit does not rerun native tests.

Version 1.1 is greenfield: no legacy plugin binary compatibility promise, no required old-history import and no automatic production-data migration. The current WinUI host uses isolated development storage and targets Windows build 26100; it is not replacement-ready.

## 2026-09-07: portable runtime and additional connected behavior

- Portable transcription/LLM roles share one runtime owner; Groq and dynamic provider/API-key settings consume it. NVIDIA/CTC keep their existing dedicated owner. Workflows and other capability consumers remain separate work.
- Real dictionary/snippet JSON transfer, verified Mac dictionary import, preserved metadata and explicit replacement; usage counters update successful snippet expansion without overwriting concurrent edits.
- Saved spoken-command profiles and optional target-app formatting now reach the ordered text pipeline.
- Bounded shortcut coordination preserves stop/cancel during asynchronous startup and prevents later recordings from queued processing-time presses.
- Named Debug test profiles isolate preferences, data, keys, packages and model assets for native restart/mutation tests. Native dictionary/snippet import and multiline editor checks passed.

Latest focused validation: Host 133 and Presentation 285 passing cases; prescribed Debug build/launch passed. This is incremental evidence, not a new complete release acceptance run.


## 2026-09-07: real manual workflows and silence filtering

- Custom manual workflows now persist prompts, exact provider/model and enabled state, with confirmed deletion and protected unsupported entries. Run invokes registered LLM providers and preserves source text on cancellation/failure. Automatic triggers, selected-text capture, templates and full override editing remain open.
- Saved quiet-clip handling, original-sample thresholds, decoder-only padding and actual final no-speech metadata now reach dictation processing.
- Complete headless validation at `a28367e3`: 517 passed, one platform skip; prescribed Debug build/launch passed. Native workflow create/save/restart/disable/delete checks passed in an isolated named profile. No successful live cloud workflow request is claimed.

## 2026-09-07: media queue and additional preferences

- Selected media reaches Windows decoding and the actual transcription provider. Serial requests support cancellation/drain, retry, optional History and TXT/provider-timed subtitle export. Queue persistence and file-specific vocabulary/snippet stages remain open.
- Explicit SDK language-hint capability replaces implicit first-language fallback. Connected NVIDIA/Groq providers do not advertise it.
- Overlay text size and successful-output duration persist, including immediate dismissal at zero; unavailable online batch preview is hidden.
- Latest complete local run: 560 passed, one platform skip; prescribed Debug build/launch passed. Native file selection/removal/readiness and overlay preference persistence were checked in an isolated profile. Ubuntu workflow fault injection was corrected after CI exposed Windows-only lock assumptions.
