# Testing TypeWhisper for Windows

## Choose the right check

| Change | Start with |
| --- | --- |
| Application behavior | Relevant `tests/TypeWhisper.Presentation.Tests` cases |
| Persistence or text processing | `tests/TypeWhisper.Core.Tests` |
| Package lifecycle or portable contracts | `tests/TypeWhisper.PluginSDK.Portable.Tests` |
| A provider | Its `plugins/<provider>/Tests` suites |
| Windows platform services | `tests/TypeWhisper.Platform.Tests` on Windows |
| Microphone capture and audio devices | `tests/TypeWhisper.Dictation.AudioTests` on Windows |
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
and dictation audio tests on Windows, and discovered plugin-owned .NET tests. The
audio suite replays PCM through the capture service without opening a microphone;
its Parakeet and provider-startup cases skip unless their `TYPEWHISPER_TEST_PARAKEET_*`
or `TYPEWHISPER_STARTUP_*` variables are set. Results are written to
`artifacts/test-results/winui-headless`, including `summary.json`. It does not run
provider Python tests or the browser experiment automatically.

Use `-Suite App` for application, plugin host/SDK and Windows platform tests, or
`-Suite Plugins` for plugin-owned tests only. The default `-Suite All` keeps the
complete local and Candidate check. CI runs `App` on Windows and Linux; the
Plugins workflow runs `Plugins` on both systems, including after shared SDK or
host changes. These plugin tests use fake providers and local endpoints, not
live services or provider credentials. The runner restores and builds all plugin
test projects once through a generated `PluginTests.slnx` in the results
directory and then runs each project with `--no-build`; `summary.json` records
that shared build under `pluginBuild`.

Use `-CollectCoverage` with `-Suite App` to write Cobertura reports for Core and
Presentation alongside the test results. CI collects these reports on both
platforms. Plugin lifecycle tests stay uninstrumented because coverage changes
collectible assembly lifetimes. SDK analyzers run during builds, incorrect
`ValueTask` consumption fails compilation, and CI checks changed whitespace.

App publishes include `THIRD-PARTY-NOTICES.txt` and a `Licenses` directory built
from restored NuGet metadata and upstream notices. Verify that packaging with
`./eng/Write-RuntimeNotices.Tests.ps1`.

For a focused iteration:

```powershell
dotnet test tests/TypeWhisper.Presentation.Tests/TypeWhisper.Presentation.Tests.csproj --filter FullyQualifiedName~DictationOutput
```

For changed release tooling or the retained experiment:

```powershell
./eng/Get-ChangedPluginProjects.Tests.ps1
./eng/Test-WinUIHeadless.Tests.ps1
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

Build the application project from your checkout as described in the
[README](README.md#build) and launch the resulting `TypeWhisper.exe`. Do not use
the installed production app for development validation. The maintainer's helper
script is described in [Maintainer's local setup](#maintainers-local-setup).

Normal Debug data is in `%LOCALAPPDATA%/TypeWhisper-WinUI-DevUserData`.
For isolated first-run or fixture checks, set `TYPEWHISPER_WINUI_TEST_PROFILE` to a
name containing only ASCII letters, digits, hyphens and underscores (1–64 characters)
before launching. The profile lives under
`%TEMP%/TypeWhisper-WinUI-TestProfiles/<name>`. Release ignores this override.

Clear the test environment variables and launch again to return to the normal
development profile. Do not modify the production profile to make a test pass.

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

## Tray-menu layout regression check

The Debug-only native probe uses the real tray menu in a named test profile,
without initializing recording, providers or the normal application profile.
Set `TYPEWHISPER_WINUI_TEST_PROFILE` to `tray-layout-probe` and
`TYPEWHISPER_WINUI_TRAY_LAYOUT_PROBE` to `1`, launch a Debug build, and clear
both variables afterwards. The maintainer's helper invocation is listed in
[Maintainer's local setup](#maintainers-local-setup).

The probe briefly opens and closes the menu, then exits. It writes
`%TEMP%/TypeWhisper-WinUI-TestProfiles/tray-layout-probe/tray-layout-probe.json`;
check that its timestamp belongs to this run, `passed` is `true` and `failures`
is empty. It checks repeated opens, recovery from a constrained window,
content changes while open, anchor stability and dismissal during a queued
layout update. Run with enough desktop work area to fit the complete menu.
Mixed-DPI monitor transitions and keyboard interaction still need manual checks.
Launch again without the probe flag or test profile to return to the normal
development profile.

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
| CI | WinUI solution build and headless application, plugin host/SDK and platform suites |
| Candidate | x64/ARM64 candidates and gated Daily publication |
| Packaging | Installer/portable-package validation without publication |
| Plugins | All plugin tests on Windows/Linux, release tooling, Python sidecar tests and manifests; changed-provider builds, with all builds on manual runs |
| Release plugins | Tested plugin packages and catalog publication |
| Security | Dependency review and package audits |
| Store | Manual MSIX packaging; separate Store installation/activation acceptance |

Record the exact source/build, environment, scenario, outcome and unresolved
limits with validation evidence. Historical tests are preserved under
[docs/archive](docs/archive/README.md) and [docs/releases](docs/releases/README.md).

## Maintainer's local setup

These steps apply to the maintainer's development machine, where a helper script
outside this repository publishes the current checkout to a stable development
output and starts that host with a separate development profile. Always pass the
current checkout/worktree:

```powershell
& F:/typewhisper/typewhisper-dev-tools/build-typewhisper-windows-dev.ps1 --run <checkout-path>
```

Do not launch a transient worktree binary or the installed production app.
For isolated profiles, set `TYPEWHISPER_WINUI_TEST_PROFILE` before running the
helper. Clear the test environment variables and run the helper again to return
to the normal development profile.

For the tray-menu layout regression check, the helper selects the test profile:

```powershell
$env:TYPEWHISPER_WINUI_TRAY_LAYOUT_PROBE = '1'
try {
    & F:/typewhisper/typewhisper-dev-tools/build-typewhisper-windows-dev.ps1 --run --profile tray-layout-probe <checkout-path>
} finally {
    Remove-Item Env:TYPEWHISPER_WINUI_TRAY_LAYOUT_PROBE -ErrorAction SilentlyContinue
}
```

Run the helper again without the probe flag or `--profile` to return to the
normal development profile.
