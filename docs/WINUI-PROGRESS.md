# Windows 1.1 release readiness

This is the current release entry point.
An open issue may already have implementation work, and a merged change may not
be in the latest published Daily. Check the exact candidate revision before release.

## Implementation baseline

The WinUI host, portable plugins, local API/CLI, workflow output actions and
extended legacy-profile importer are implemented. The application and executable
are named TypeWhisper and `TypeWhisper.exe`. The package identity of an existing
installation is retained during updates.

Settings use card pages with System/Light/Dark themes and German, Japanese and
Simplified Chinese translations. Local speech engines run in a restartable worker
process. Settings > Advanced exports a JSON support report. Workflows can select
their own spoken language and transcription model. Local live preview uses a
rolling window of at most 30 seconds; the final transcript uses the full recording.

See the [capability map](WINUI-FUNCTIONAL-STATUS.md) for feature scope and
[plugin release tooling](PLUGIN-RELEASES.md) for package publication.

| Area | Current behavior |
| --- | --- |
| Data integrity | Catalog load failures cannot become empty writable catalogs. Stale edits are rejected; dictionary conflicts reload explicitly. Settings and sync use durable writes, preserve unknown legacy settings and skip unchanged data. Profile notifications run after the write lock is released. |
| Dictation | Hook diagnostics, stop processing and field automation run off the UI/input threads. Delayed starts are discarded while stop/cancel remains available. Paste does not wait for History persistence. Preview inference is canceled on stop and uses bounded audio. |
| Audio and providers | Recording uses a cached microphone list. Opt-in pre-roll respects lock/suspend state and rechecks the interactive desktop before resuming. Long cloud uploads split into contiguous chunks with corrected timestamps. Retries use provider status and retry metadata. Model selection is asynchronous; idle CTC models can unload. Worker results retain segment confidence. |
| Application structure | `ApplicationServices` and `DictationPluginServices` own construction and disposal. Views share subscription and shortcut handling. History rows are virtualized. |
| Plugins | Shared source helpers provide common provider/download behavior. Package localization uses culture-parent and English fallback. |
| Security and diagnostics | Downloads use pinned hashes; release errors reach diagnostics. The local API checks the peer user where supported. Windows loopback IP prefixes alone are not its security boundary. Invalid sync manifests stay intact and produce recovery instructions. |
| Build and tests | Central NuGet versions, isolated worktree output, analyzers and whitespace checks apply. CI includes audio tests and Core/Presentation coverage. Application packages include runtime dependency notices and licenses. |

Worker model loads and transcription have separate deadlines, including after
restarts and model changes. The inactivity watchdog measures awake time on Windows;
see [timer behavior during sleep](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/11/environment-tickcount-windows-behavior).

## Work that remains open

- Catalog/package signatures and trusted publisher keys are not implemented.
  HTTPS, pinned hashes and safe ZIP extraction do not authenticate the publisher
  independently of the feed. A coordinated follow-up must define the signed
  payload, key custody/rotation, rollback policy, legacy-feed migration and
  publisher/verifier rollout before enforcing signatures.
- The session's capture/output orchestration and broad `Changed` event still
  need gradual decomposition. The composition boundaries do not yet cover all
  session behavior. History still persists snapshots; moving
  its save after paste and virtualizing rows do not make storage append-only.
- Resource-based plugin localization is available, but Japanese and Simplified
  Chinese translations are not complete across every provider. GemmaLocal and
  LocalLlm retain their different native runtime pins until real model acceptance
  supports convergence. Changed packages need release version selection and
  catalog publication before installed users receive them.
- Native checks include large-History scrolling and selection, the
  application/provider matrix and installed upgrades. Run the scenarios below
  against the selected candidate and record their scope in its release or PR.
- Generated notices cover restored runtime packages. In particular, the old
  Win2D binary-license URL still needs distribution review; the included upstream
  source license is identified as such in `eng/licenses/sources.json`.

## Acceptance still needed or requiring a current check

| Topic | Required evidence or decision |
| --- | --- |
| First dictation | Check first dictation and window switching on the selected candidate, recording the target app and field-lock setting. |
| Licensing on 1.0 | The Polar version pin exists only in 1.1; installed 1.0 builds send unversioned requests. No 1.0.x hotfix is planned (decided 2026-09-24): 1.0 users receive the pin by moving to 1.1 ([#471](https://github.com/TypeWhisper/typewhisper-win/issues/471), closed 2026-10-04). |
| Settings and theme | Exercise the card-based pages, the Dictionary, Snippets and Workflow dialogs and the light theme in the History window, overlay, review window, workflow palette and setup wizard ([#583](https://github.com/TypeWhisper/typewhisper-win/pull/583)). Statistics, File transcription and the overlay editor keep the previous layout. |
| Recent dictation changes | Check start/stop, cancellation, long recordings with live text, per-workflow language/model selection, restoration of the global model, device loss and API-started recordings. |
| 1.0 upgrades | Complete the installed 1.0 → 1.1 → next-1.1 sequence, migration, interrupted import and rollback checks in the [upgrade guide](DAILY-1.1-CANDIDATE.md). Earlier updates between 1.1 Dailys do not cover this. |
| Published plugins | Verify install, configuration, actual use and update through the published catalog for release-critical providers. Preserve the legacy feed. |
| Display behavior | Complete the outstanding primary-display, mixed-DPI, overlay-layout and live-text positioning checks on the selected candidate. |
| Hardware support | Execute older-Windows acceptance before extending support claims. 1.1 is built for x64 only; Windows 11 on Arm through emulation is untested. |
| Licensing | Finish live lifecycle evidence and distribution follow-up in [Polar maintenance](POLAR-API-VERSION-MAINTENANCE.md). Version pinning and error handling are implemented. |
| Channels and notes | Confirm the candidate's actual package identity, runtime requirements, data import and feed availability. RC and Stable are separate rollout decisions. |
| Daily feedback | Reproduce reported blockers against the selected candidate. |

Also check first-run setup, x64 runtime installation/update, standby and microphone
recovery, pre-roll suspension while locked, automatic model reload, persistence
across restart, History search, Settings changes, account linking and Raycast.
Use [hotkey recovery validation](HOTKEY-RESUME-VALIDATION.md) for sleep/session checks.
Monitor power cycling/disconnection and target-window closure are outside the
current acceptance scope.

## Deferred product scope

Calendar OAuth registrations and usable sign-in remain absent. Browser microphone
integration and Parakeet Realtime EOU are research, not promised 1.1 deliverables.
Large feature requests such as managed deployment or voice-based field editing
need a separate scope decision.

## Release procedure

1. Select the source revision and plugin versions; inspect CI and package artifacts.
2. Record relevant automated and native acceptance against that candidate.
3. Publish to the intended prerelease channel and gather feedback.
4. Enable legacy Daily upgrades only after their installed-upgrade gate passes.
5. Publish an RC, then Stable, through the Candidate workflow with their release notes; see [Release candidates and Stable](DAILY-1.1-CANDIDATE.md#release-candidates-and-stable).

The mechanics and exact gate variables live in [Daily delivery](DAILY-1.1-CANDIDATE.md)
and [plugin releases](PLUGIN-RELEASES.md). Store delivery follows its
[own guide](STORE_SUBMISSION.md): Store beta updates can follow a published Daily
automatically ([#591](https://github.com/TypeWhisper/typewhisper-win/pull/591));
the stable Store submission remains a manual step.
