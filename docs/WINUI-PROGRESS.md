# Windows 1.1 progress

Current source checkpoint: `ff77f43a` (2026-09-06). Release-wide draft: [PR #447](https://github.com/TypeWhisper/typewhisper-win/pull/447), `seofood/release-1.1` against `main`.

The authoritative feature inventory is now the [full comparison against both previous Windows and Mac](WINUI-FUNCTIONAL-STATUS.md). This replaces the accumulated, contradictory milestone checklist. Historical test counts and superseded UI decisions remain available in Git history; they are not current completion claims.

## Connected today

- Real hotkey microphone dictation through NVIDIA Parakeet/Canary or Groq, local live preview, persisted provider/model/language selection and history before whole-text paste.
- Main Dictation and Quick Launch shortcuts, microphone priority, sound/output preferences, whisper mode, media pause/ducking and silence auto-stop. Overlay configuration and provider configuration are separate from recording state.
- Dictionary terms/corrections, noncommercial built-in term packs, snippets with recording snapshots and automatic internal Parakeet CTC. Snippet usage counts and full text-pipeline parity remain open.
- Isolated history read/search/raw-final details/copy. Actual model and provider appear in details only.
- Integrations with Installed and Discover. Only NVIDIA Parakeet and Groq have connected runtime bindings. Plugin settings own model downloads and credentials; Dictation selects provider/model. There is no global Models settings page or independent CTC integration.
- Persistent package installation/uninstallation, staged updates, checksums and extraction/identity validation. Plugin-owned output folders/tests and optional install/uninstall hooks with status messages. See [package contract](PLUGIN-PACKAGES-1.1.md).
- Headless CI for portable host, presentation and plugin-owned suites on Windows and Ubuntu.

## Remaining release work

These are major implementation areas, not final polish. Detailed per-feature gaps, reference sources, a 50-row grouped plugin inventory and acceptance criteria are in the full comparison.

- [ ] Wire visible runtime settings, especially Review first / AutoPaste and history/privacy; make unavailable controls unambiguous.
- [ ] Connect complete text processing, generic plugin capabilities, cancellation, queued jobs, durable recovery and accurate provenance.
- [ ] Replace workflow examples with persistent workflows, triggers, LLM processing, selected-text execution and retry.
- [ ] Replace file/recorder simulations with real decoding/capture, output files, transcription jobs, subtitles and watch folders.
- [ ] Complete history mutation/export/audio, retention, correction learning, statistics, backup and sync.
- [ ] Generalize provider/settings/contribution integration and rebuild the selected additional plugins; publish and verify the single v2 catalog end to end.
- [ ] Connect onboarding, licensing, updates, autostart, localization, API/CLI and Windows shell activation.
- [ ] Decide which Mac additions belong in Windows: Inbox/audio sync, meeting automation, live field text, media imports and platform-specific alternatives.
- [ ] Complete OS/architecture/distribution decisions, native accessibility/DPI/device/editor acceptance and the deferred legacy-versus-WinUI benchmark.

## Evidence

At `ff77f43a`, [headless CI](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34060774341) passed on Windows (262 passed, 1 skipped) and Ubuntu (257 passed, 5 skipped). [CodeQL](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34060775205) passed. CodeRabbit skipped review because the PR is draft; its successful status is not a completed review.

Earlier local build/launch, native inference, owner feedback and focused UI checks are scoped in [testing](WINUI-TESTING.md). The last live v2 Discover check returned HTTP 404. Package operations have fixture coverage, not published-feed acceptance. No full real Groq dictation acceptance is recorded. This documentation audit does not rerun native tests.

Version 1.1 is greenfield: no legacy plugin binary compatibility promise, no required old-history import and no automatic production-data migration. The current WinUI host uses isolated development storage and targets Windows build 26100; it is not replacement-ready.
