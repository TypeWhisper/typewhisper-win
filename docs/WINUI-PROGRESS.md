# Windows 1.1 release readiness

Reviewed against the source on 2026-10-08. This is the current release entry point;
old session checklists and test journals are [historical evidence](archive/README.md).
An open issue may already have implementation work, and a merged change may not
be in the latest published Daily. Check the exact candidate revision before release.

## Implementation baseline

The WinUI host, portable plugins, local API/CLI, workflow output actions and
extended legacy-profile importer are implemented. The application and executable
are named TypeWhisper and `TypeWhisper.exe`. The package identity of an existing
installation is retained during updates.

Since the previous review, Settings follow the macOS structure as card pages with a
System/Light/Dark theme ([#562](https://github.com/TypeWhisper/typewhisper-win/pull/562),
[#583](https://github.com/TypeWhisper/typewhisper-win/pull/583)), the UI is translated
into German, Japanese and Simplified Chinese ([#572](https://github.com/TypeWhisper/typewhisper-win/pull/572)),
local speech engines run in a restartable worker process ([#555](https://github.com/TypeWhisper/typewhisper-win/pull/555)),
Settings > Advanced exports a JSON support report ([#590](https://github.com/TypeWhisper/typewhisper-win/pull/590)),
recording starts from a cached microphone list ([#586](https://github.com/TypeWhisper/typewhisper-win/pull/586)),
and workflows can set their own spoken language and transcription model
([#599](https://github.com/TypeWhisper/typewhisper-win/pull/599), [#600](https://github.com/TypeWhisper/typewhisper-win/pull/600)).

See the [capability map](WINUI-FUNCTIONAL-STATUS.md) for feature scope. The old
September 10 inventory of unported plugins is not the current catalog status.
Use [plugin release tooling](PLUGIN-RELEASES.md) to verify selected package versions.

## Local checkup integration, 2026-10-08

The checkup changes are assembled on the local `seofood/checkup-integration`
branch. Topic branches retain their individual changes. This is not a published
Daily or a claim of native release acceptance.

| Area | Integrated changes |
| --- | --- |
| Data integrity | Catalog load failures no longer become empty writable catalogs. Baseline checks reject stale edits; dictionary conflicts reload explicitly. Settings and sync use durable writes, preserve unknown legacy settings and skip unchanged data. Profile notifications run after the write lock is released. |
| Dictation responsiveness | Hook diagnostics leave the input thread; stale shortcuts cannot start recording after a UI stall. Stop processing and field automation leave the UI thread. Paste no longer waits for History persistence. Preview inference is canceled on stop and uses a bounded snapshot; isolated native requests have deadlines even while heartbeats continue. |
| Audio and providers | Opt-in microphone pre-roll respects lock/suspend state. Long cloud uploads split into contiguous chunks with corrected timestamps; retry handling consumes provider retry metadata. Model selection is asynchronous, idle CTC models can unload, and worker results retain per-segment confidence. |
| Application structure | `ApplicationServices` owns application construction and `DictationPluginServices` owns plugin construction/disposal. Settings subscriptions and shortcut callbacks share common paths; native declarations are consolidated. History rows are virtualized. The obsolete select-control prototype is removed. |
| Plugins | Shared source helpers replace drifting provider/download implementations. Package localization resources are read by the host, with culture-parent and English fallback. SDK parsing, provider error tests and missing package documentation are extended. |
| Security and diagnostics | Sherpa downloads have pinned hashes; silent release errors reach diagnostics. The local API checks the peer user where supported and uses Windows loopback IP prefixes. Package downgrade behavior and privacy disclosures are tightened. These IP prefixes alone are not the API's security boundary. |
| Build and tests | Central NuGet versions, worktree isolation, analyzer checks, changed-whitespace checks, caches and a shared plugin-test build reduce duplication. Audio tests are included in CI; Core/Presentation coverage is available. Published app output includes runtime dependency notices and license files. |

Local live preview now shows a rolling window of at most 30 seconds. The final
transcript still uses the complete recording. Native acceptance must include a
recording longer than that window, stopping while preview inference is active,
and verifying the full final result.

The checkup's automated evidence is recorded locally under `artifacts/checkup`.
The full runner command is:

```powershell
./eng/Test-WinUIHeadless.ps1 -Suite All -CollectCoverage -Configuration Release -ResultsDirectory artifacts/checkup/final
```

The runner writes `summary.json`, individual TRX files and Core/Presentation
coverage reports. Build/launch evidence comes from the current checkout through
the development helper described in the [test guide](../TESTING_GUIDE.md).

### Work that remains open

- Catalog/package signatures and trusted publisher keys are not implemented.
  HTTPS, pinned hashes and safe ZIP extraction do not authenticate the publisher
  independently of the feed. A coordinated follow-up must define the signed
  payload, key custody/rotation, rollback policy, legacy-feed migration and
  publisher/verifier rollout before enforcing signatures.
- The session's capture/output orchestration and broad `Changed` event still
  need gradual decomposition. The new composition boundaries are an initial
  extraction, not the end of that work. History still persists snapshots; moving
  its save after paste and virtualizing rows do not make storage append-only.
- Resource-based plugin localization is available, but Japanese and Simplified
  Chinese translations are not complete across every provider. GemmaLocal and
  LocalLlm retain their different native runtime pins until real model acceptance
  supports convergence. Changed packages need release version selection and
  catalog publication before installed users receive them.
- Native checks still need first-dictation/focus/paste acceptance, start/stop and
  lock/resume with pre-roll, large-History scrolling and selection, provider
  execution and installed upgrades. Automated fixtures do not establish these.
- Generated notices cover restored runtime packages. In particular, the old
  Win2D binary-license URL still needs distribution review; the included upstream
  source license is identified as such in `eng/licenses/sources.json`.

## Acceptance still needed or requiring a current check

| Topic | Required evidence or decision |
| --- | --- |
| First dictation | [#513](https://github.com/TypeWhisper/typewhisper-win/issues/513) is fixed and closed (2026-09-24). Re-check on the selected candidate: first dictation after a fresh start into a Chromium/Electron field with the field lock enabled. |
| Licensing on 1.0 | The Polar version pin exists only in 1.1; installed 1.0 builds send unversioned requests. No 1.0.x hotfix is planned (decided 2026-09-24): 1.0 users receive the pin by moving to 1.1 ([#471](https://github.com/TypeWhisper/typewhisper-win/issues/471), closed 2026-10-04). |
| Settings and theme | Exercise the card-based pages, the Dictionary, Snippets and Workflow dialogs and the light theme in the History window, overlay, review window, workflow palette and setup wizard ([#583](https://github.com/TypeWhisper/typewhisper-win/pull/583)). Statistics, File transcription and the overlay editor keep the previous layout. |
| Recent dictation changes | Re-test on the candidate: recording start with the cached microphone list ([#586](https://github.com/TypeWhisper/typewhisper-win/pull/586)) and per-workflow spoken language and transcription model ([#599](https://github.com/TypeWhisper/typewhisper-win/pull/599), [#600](https://github.com/TypeWhisper/typewhisper-win/pull/600)), including the model restore after device loss and API-started recordings. The merged build has not been retested natively. |
| 1.0 upgrades | Complete the installed 1.0 → 1.1 → next-1.1 sequence, migration, interrupted import and rollback checks in the [upgrade guide](DAILY-1.1-CANDIDATE.md). Earlier updates between 1.1 Dailys do not cover this. |
| Published plugins | Verify install, configuration, actual use and update through the published catalog for release-critical providers. Preserve the legacy feed. |
| Display behavior | Complete the outstanding primary-display, mixed-DPI, overlay-layout and live-text positioning checks from the earlier handoff. |
| Hardware support | Execute ARM64 and older-Windows acceptance before extending support claims. |
| Licensing | Finish live lifecycle evidence and distribution follow-up in [Polar maintenance](POLAR-API-VERSION-MAINTENANCE.md). Version pinning and error handling are implemented. |
| Channels and notes | Confirm the candidate's actual package identity, runtime requirements, data import and feed availability. RC and Stable are separate rollout decisions. |
| Daily feedback | Reproduce blockers against the current host rather than treating old WPF reports as automatically fixed. |

Previously confirmed first-run, x64 runtime setup/update, standby/microphone,
focus-switching, account-linking and Raycast checks remain evidence for their
recorded scope. Re-run them when relevant changes justify it. The earlier handoff
excluded monitor power cycling/disconnection and target-window closure from its
requested scope; this guide does not reintroduce them as mandatory tests.

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
5. Prepare RC/Stable distribution and matching notes as subsequent stages.

The mechanics and exact gate variables live in [Daily delivery](DAILY-1.1-CANDIDATE.md)
and [plugin releases](PLUGIN-RELEASES.md). Store delivery follows its
[own guide](STORE_SUBMISSION.md): Store beta updates can follow a published Daily
automatically ([#591](https://github.com/TypeWhisper/typewhisper-win/pull/591));
the stable Store submission remains a manual step.
