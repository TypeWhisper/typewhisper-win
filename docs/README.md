# Windows documentation

These guides describe the current Windows 1.1 source. A merged implementation,
a published Daily and a validated stable release are different milestones.
Use the release notes for the package you installed when they differ from this branch.

## Use TypeWhisper

- [Getting started](GETTING-STARTED.md): installation, first dictation and main workspaces.
- [Dictation results and troubleshooting](DICTATION-RESULTS.md): insertion, copying and storage warnings.
- [Windows CLI](WINDOWS-CLI.md) and [HTTP API](WINUI-HTTP-API.md): supported automation contracts.
- [Privacy policy](PRIVACY.md): data handling overview.

## Develop and validate

- [Application architecture](WINUI-MIGRATION.md): source boundaries and the WinUI host.
- [Test guide](../TESTING_GUIDE.md): automated suites and focused native acceptance.
- [Current capability map](WINUI-FUNCTIONAL-STATUS.md): implemented surfaces and known limits.
- [Platform scope](PLATFORM_PARITY.md): shared outcomes and platform-specific exclusions.
- [Local audio validation](WINUI-LOCAL-AUDIO-TEST.md): opt-in microphone and provider tests.
- [Portable plugin development](PLUGIN-MIGRATION-1.1.md), [package contract](PLUGIN-PACKAGES-1.1.md),
  [settings saves](PLUGIN-SETTINGS-SAVE.md), and [plugin releases](PLUGIN-RELEASES.md).
- [Experiments](../experiments/README.md): the retained browser proof of concept.

## Prepare a release

- [1.1 readiness](WINUI-PROGRESS.md): remaining decisions and acceptance boundaries.
- [Daily delivery and 1.0 migration](DAILY-1.1-CANDIDATE.md): package identities, import and rollout gates.
- [Daily release-note source](releases/1.1-daily.md): copy for the current candidate workflow.
- [Store submission](STORE_SUBMISSION.md): separate MSIX packaging and acceptance.
- [Polar API maintenance](POLAR-API-VERSION-MAINTENANCE.md): license compatibility and follow-up.
- [Release records](releases/README.md): dated validation and previous release notes.

## Research and evidence

These documents have narrower scopes than the release guides. A successful test
on one provider, machine or commit does not establish general release readiness.

| Topic | Evidence or investigation |
| --- | --- |
| Plugin acceptance | [File Memory](FILE-MEMORY-ACCEPTANCE.md), [Copilot](copilot-live-acceptance.md), [screenshots](screenshots/README.md) |
| Dictation | [Standby recovery](HOTKEY-RESUME-VALIDATION.md), [immediate capture](testing/immediate-capture.md), [dictionary boundaries](testing/dictionary-term-boundaries.md) |
| Windows integration | [Calendar and meeting automation](windows-meeting-automation.md), [Premium and cloud compatibility](windows-premium-cloud-parity.md) |
| Research | [Browser microphone integration](BROWSER_MICROPHONE_INTEGRATION_WINDOWS.md), [Parakeet Realtime EOU evaluation](PARAKEET_REALTIME_EOU_WINDOWS.md), [acceleration investigation](AMD_ACCELERATION.md) |
| Historical WinUI implementation | [Archived progress and test journals](archive/README.md) |

## Keep these guides current

Update the owning guide when behavior changes. Record validation with its date,
source revision or change, environment and limitations; do not turn old test counts
into a current quality claim. Keep implementation journals in the archive and
research explicitly marked as research. Link to a single contract instead of
copying command lists or release gates into multiple files.
