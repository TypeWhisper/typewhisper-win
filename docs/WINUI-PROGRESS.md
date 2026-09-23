# Windows 1.1 release readiness

Reviewed against the source on 2026-09-23. This is the current release entry point;
old session checklists and test journals are [historical evidence](archive/README.md).
An open issue may already have implementation work, and a merged change may not
be in the latest published Daily. Check the exact candidate revision before release.

## Implementation baseline

The WinUI host, portable plugins, local API/CLI, workflow output actions and
extended legacy-profile importer are implemented. The application and executable
are named TypeWhisper and `TypeWhisper.exe`. The package identity of an existing
installation is retained during updates.

See the [capability map](WINUI-FUNCTIONAL-STATUS.md) for feature scope. The old
September 10 inventory of unported plugins is not the current catalog status.
Use [plugin release tooling](PLUGIN-RELEASES.md) to verify selected package versions.

## Acceptance still needed or requiring a current check

| Topic | Required evidence or decision |
| --- | --- |
| First dictation | Verify the [#513](https://github.com/TypeWhisper/typewhisper-win/issues/513) capture retry on the selected candidate: first dictation after a fresh start into a Chromium/Electron field with the field lock enabled. |
| Licensing on 1.0 | The Polar version pin exists only in 1.1. Installed 1.0 builds send unversioned requests; decide on a 1.0.x hotfix before Polar's default changes ([#471](https://github.com/TypeWhisper/typewhisper-win/issues/471)). |
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
[own guide](STORE_SUBMISSION.md).
