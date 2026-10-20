# Windows documentation

These guides describe the Windows 1.3 source on `main`; the 1.2 release line and
its guides live on the `release/1.2` branch. A merged implementation,
a published Daily and a validated stable release are different milestones.
Use the release notes for the package you installed when they differ from this branch.

## Use TypeWhisper

- [Getting started](GETTING-STARTED.md): installation, first dictation and main workspaces.
- [Dictation results and troubleshooting](DICTATION-RESULTS.md): insertion, copying and storage warnings.
- [Import from other apps](IMPORT-FROM-OTHER-APPS.md): words and snippets from Wispr Flow or Handy.
- [Windows CLI](WINDOWS-CLI.md) and [HTTP API](WINUI-HTTP-API.md): supported automation contracts.
- [Privacy policy](PRIVACY.md): data handling overview.
- [Managed license activation](enterprise/windows-managed-license.md): provisioning a commercial license through machine policy.
- [Managed settings](enterprise/windows-managed-settings.md): registry policies, Group Policy templates, Intune packaging, network hosts and permissions.

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
- [Daily release-note source](releases/1.3-daily.md): copy for the current candidate workflow.
- [Store submission](STORE_SUBMISSION.md): separate MSIX packaging and acceptance.
- [Polar API maintenance](POLAR-API-VERSION-MAINTENANCE.md): license compatibility and follow-up.
- [Release notes](releases/README.md): current sources and published versions.

## Research and evidence

These documents have narrower scopes than the release guides. A successful test
on one provider, machine or commit does not establish general release readiness.

| Topic | Evidence or investigation |
| --- | --- |
| Plugin validation | [File Memory](../plugins/TypeWhisper.Plugin.FileMemory/README.md#manual-validation), [Copilot](../plugins/TypeWhisper.Plugin.GitHubCopilot/README.md), [screenshots](screenshots/README.md) |
| Dictation | [Standby recovery](HOTKEY-RESUME-VALIDATION.md), [immediate capture](testing/immediate-capture.md), [dictionary boundaries](testing/dictionary-term-boundaries.md) |
| Windows integration | [Calendar and meeting automation](windows-meeting-automation.md), [Premium and cloud compatibility](windows-premium-cloud-parity.md) |
| Research | [Browser microphone integration](BROWSER_MICROPHONE_INTEGRATION_WINDOWS.md), [Parakeet Realtime EOU evaluation](PARAKEET_REALTIME_EOU_WINDOWS.md), [acceleration investigation](AMD_ACCELERATION.md) |

## Keep these guides current

Update the owning guide when behavior changes. Record validation with its date,
source revision or change, environment and limitations in the relevant pull request
or release record. Keep research explicitly marked as research. Link to a single
contract instead of copying command lists or release gates into multiple files.
