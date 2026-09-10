# WinUI 1.1 Daily candidate

The `WinUI Daily` workflow creates validation artifacts for x64 and ARM64 on pull requests and release-branch pushes. Scheduled main runs and explicit main dispatches with `publish_daily=true` publish a GitHub prerelease after both architectures pass. The legacy workflow no longer schedules Daily builds; stable release delivery is unchanged. Candidate versions are `1.1.0-daily.YYYYMMDD.RUN`.

The candidate bundles .NET, the Windows App SDK runtime, the CLI and portable plugin packages. It currently targets Windows build 26100 or newer. CI runs the headless suites, checks package contents, verifies application/CLI versions and executable architecture, rejects development/user state, and records the commit plus ZIP SHA-256. Cross-building ARM64 does not count as testing on ARM64 hardware.

## Profile boundaries

The first successful candidate run is [34451171076](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34451171076), built from `a2acf689`. Both x64 and ARM64 publish, rejection checks, content validation and artifact upload passed. The artifacts are validation-only; this evidence does not establish native startup, installation, or data migration acceptance. Later fixes require a new candidate before distribution.

The installer candidate from `ed83c032` passed both architectures in [34455620426](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34455620426), including Velopack packing and artifact upload. [Headless checks 34455620383](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34455620383) passed on Windows and Linux. Locally, 31 focused migration/backup tests and the prescribed development build/relaunch passed. These results do not replace clean-machine installer acceptance or an actual historical-profile migration check.

- Release WinUI: `%LOCALAPPDATA%/TypeWhisper-WinUI`.
- Normal development WinUI: `%LOCALAPPDATA%/TypeWhisper-WinUI-DevUserData`.
- Named debug smoke profiles: `%TEMP%/TypeWhisper-WinUI-TestProfiles/<name>`.
- Release model assets stay under the release profile; they do not share the development NVIDIA model directory.
- Development and release use different single-instance identities.
- Unbound CLI discovery prefers release WinUI, then legacy production, then development. Explicit `--dev`, `--profile`, and installed CLI profile bindings retain their precedence.

On first Release launch, an absent WinUI profile receives a copy of dictionary, snippets, workflows and history from `%LOCALAPPDATA%/TypeWhisper-UserData/Data`. Only when that legacy root is absent does the importer try `%LOCALAPPDATA%/TypeWhisper/Data`. An existing WinUI profile, even an empty directory, is never merged or replaced. Debug builds never invoke this importer.

The importer uses the validated portable backup format, stages privately on the destination volume, and publishes by a no-overwrite directory rename before opening profile stores. Failure or cancellation leaves the source and destination unchanged; retry is safe. A process termination can leave an unused `.typewhisper-import-*` staging directory beside the profile; it is never treated as a completed import. Unknown fields, malformed data and linked source paths fail closed. The previous app should be closed during migration.

Settings, sign-ins, license credentials, plugins, model files, audio, recorder archives and recovery recordings remain in the old installation. History is imported as text without device-bound audio references. The wizard explains these limits and configures the new app. This is a portable-data migration, not complete settings/plugin parity. `legacy-import.json` records completion without storing user content or source paths. Removing the Daily installation does not delete either user-data directory.

## Side-by-side installer

The candidate workflow also packs a Velopack `TypeWhisperDaily` installer for each architecture, using pinned tooling `0.0.1298`. It installs separately from legacy `TypeWhisper`; shortcuts and uninstall identity use **TypeWhisper Daily**. The WinUI entry point handles Velopack callbacks before XAML and profile access. Installed Daily builds can register their own `TypeWhisperDaily` startup value, and uninstall removes only that owned value. Startup is unavailable for the standalone ZIP.

Artifacts include the setup executable, packages, feed metadata and SHA-256 files. Main publishing runs attach them to a Daily prerelease without marking it as the latest stable release. The new `win-x64-winui-daily` and `win-arm64-winui-daily` channels are distinct from the legacy Daily channels; old clients must not receive packages with a different installation identity. There is no automatic update polling or feed transition; existing Daily users must explicitly install this candidate. Close the old app before testing to avoid competing hotkeys. The old installation and its update feed remain available for rollback.

## Before distributing to existing Daily users

1. Produce and inspect both candidate artifacts; launch the x64 candidate on a clean supported Windows machine and confirm runtime prerequisites. Test ARM64 on hardware before claiming support.
2. Test installer install/reinstall/uninstall and startup on a clean supported Windows machine. The existing Release workflow still packages WPF; automatic Daily update delivery remains separate work.
3. Validate the copied portable data against an actual old Daily profile and confirm rollback to the preserved old app. Automated fixture tests cover source preservation, existing destinations, failure/retry, cancellation and invalid data; they do not establish compatibility with every historical profile.
4. Validate the supported v2 plugin catalog/packages and clearly list unavailable plugins. See `PLUGIN-MIGRATION-1.1.md`.
5. Check dictation, workflows, API/CLI/Raycast and account/sync in the actual candidate. Publish only to the intended Daily track; do not alter stable or RC feeds.

Calendar OAuth and pending plugin ports are not completed by packaging the app.
