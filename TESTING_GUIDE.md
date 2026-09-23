# Testing TypeWhisper for Windows

## Choose the right check

| Change | Start with |
| --- | --- |
| Application behavior | Relevant `tests/TypeWhisper.Presentation.Tests` cases |
| Persistence or text processing | `tests/TypeWhisper.Core.Tests` |
| Package lifecycle or portable contracts | `tests/TypeWhisper.PluginSDK.Portable.Tests` |
| A provider | Its `plugins/<provider>/Tests` suites |
| Windows platform services | `tests/TypeWhisper.Platform.Tests` on Windows |
| CLI or HTTP API | CLI/Presentation tests, then applicable `tests/native` probes |
| UI | Native build and focused visual/keyboard acceptance in the development host |
| Installer/update | Package checks and an isolated installed-upgrade VM |

Fake-provider tests establish application logic, not real provider quality or
hardware compatibility. Live provider calls and audio checks are separate,
opt-in validation.

## Automated suites

```powershell
./eng/Test-WinUIHeadless.ps1 -Configuration Release
```

This runs Core, plugin host/SDK, CLI and Presentation suites, Windows platform
tests on Windows, and discovered plugin-owned .NET tests. Results are written to
`artifacts/test-results/winui-headless`, including `summary.json`. It does not run
provider Python tests or the browser experiment automatically.

For a focused iteration:

```powershell
dotnet test tests/TypeWhisper.Presentation.Tests/TypeWhisper.Presentation.Tests.csproj --filter FullyQualifiedName~DictationOutput
```

For changed release tooling or the retained experiment:

```powershell
./eng/Get-ChangedPluginProjects.Tests.ps1
./eng/Test-WinUIDailyCandidate.Tests.ps1
./eng/Test-DailyUpgradePackage.Tests.ps1
./eng/Test-WinUILocalAudio.Tests.ps1
python -m unittest discover -s eng/tests -p 'test_*.py' -v
node --test experiments/browser-microphone-extension/tests/field-target.test.cjs
```

Run provider Python suites from their owning `Tests` directories when relevant.
The [plugin release gate](docs/PLUGIN-RELEASES.md) discovers both .NET and Python
package tests. Successful packaging does not imply native model execution.

## Build and launch the native UI

Use Windows with .NET 10 SDK and Windows SDK 26100 or newer for development.
Build prerequisites differ from the configured minimum OS and the validated OS
matrix. See [installation and migration](docs/DAILY-1.1-CANDIDATE.md).

On Marco's machine, always pass the current checkout/worktree to:

```powershell
& F:/typewhisper/typewhisper-dev-tools/build-typewhisper-windows-dev.ps1 --run <checkout-path>
```

The helper publishes to the stable development output and starts that host.
Do not launch a transient worktree binary or the installed production app.
General contributor instructions are in the [README](README.md#build).

Normal Debug data is in `%LOCALAPPDATA%/TypeWhisper-WinUI-DevUserData`.
For isolated first-run or fixture checks, set `TYPEWHISPER_WINUI_TEST_PROFILE` to a
name containing only ASCII letters, digits, hyphens and underscores (1–64 characters)
before using the helper. The profile lives under
`%TEMP%/TypeWhisper-WinUI-TestProfiles/<name>`. Release ignores this override.

Clear the test environment variables and run the helper again to return to the
normal development profile. Do not modify the production profile to make a test pass.

## Result-window smoke check

In a named Debug test profile, `TYPEWHISPER_WINUI_REVIEW_FIXTURE=1` opens a synthetic
insertion-failure result. It does not capture audio, contact a provider or write
History. Copying is a real clipboard operation when you click **Copy text**.

Verify the reason, readable retained text, storage note, Copy/Close keyboard access
and copy confirmation. With an action plugin installed, check that **More actions**
starts collapsed and exposes the picker only when expanded. Use a local test
action for execution acceptance; UI inspection alone does not prove external delivery.

Automated output tests cover disabled insertion, processing/action failure,
History/audio save warnings, cancellation and changed output preferences.
A native first-dictation test remains necessary for target capture/paste behavior.

## Focused native acceptance

Exercise the changed flow and its most relevant failure case. For release work,
use [release readiness](docs/WINUI-PROGRESS.md): setup, dictation, files, recorder,
plugins, persistence and updates have different acceptance environments.
Installed upgrade tests belong in a disposable VM; ARM64 execution requires ARM64
hardware. [Local audio checks](docs/WINUI-LOCAL-AUDIO-TEST.md) explain microphone
fixtures and their limits.

## CI and packaging

| Workflow | Responsibility |
| --- | --- |
| CI | WinUI solution build and headless application/provider suites |
| Candidate | x64/ARM64 candidates and gated Daily publication |
| Packaging | Installer/portable-package validation without publication |
| Plugins | Manifest and changed-provider builds; full sweep on manual runs |
| Release plugins | Tested plugin packages and catalog publication |
| Security | Dependency review and package audits |
| Store | Manual MSIX packaging; separate Store installation/activation acceptance |

Record the exact source/build, environment, scenario, outcome and unresolved
limits with validation evidence. Historical tests are preserved under
[docs/archive](docs/archive/README.md) and [docs/releases](docs/releases/README.md).
