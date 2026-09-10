# WinUI 1.1 Daily candidate

The `WinUI Daily Candidate` workflow creates validation artifacts for x64 and ARM64. It does not publish a GitHub release, replace the legacy release pipeline, or modify any update feed. Candidate versions are `1.1.0-daily.YYYYMMDD.RUN`.

The candidate bundles .NET, the Windows App SDK runtime, the CLI and portable plugin packages. It currently targets Windows build 26100 or newer. CI runs the headless suites, checks package contents, verifies application/CLI versions and executable architecture, rejects development/user state, and records the commit plus ZIP SHA-256. Cross-building ARM64 does not count as testing on ARM64 hardware.

## Profile boundaries

The first successful candidate run is [34451171076](https://github.com/TypeWhisper/typewhisper-win/actions/runs/34451171076), built from `a2acf689`. Both x64 and ARM64 publish, rejection checks, content validation and artifact upload passed. The artifacts are validation-only; this evidence does not establish native startup, installation, or data migration acceptance. Later fixes require a new candidate before distribution.

- Release WinUI: `%LOCALAPPDATA%/TypeWhisper-WinUI`.
- Normal development WinUI: `%LOCALAPPDATA%/TypeWhisper-WinUI-DevUserData`.
- Named debug smoke profiles: `%TEMP%/TypeWhisper-WinUI-TestProfiles/<name>`.
- Release model assets stay under the release profile; they do not share the development NVIDIA model directory.
- Development and release use different single-instance identities.
- Unbound CLI discovery prefers release WinUI, then legacy production, then development. Explicit `--dev`, `--profile`, and installed CLI profile bindings retain their precedence.

Opening a candidate does not import or modify legacy settings. Data migration is a separate, explicit operation; a separate empty profile is not a completed upgrade experience. Do not tell testers their existing settings have migrated yet.

## Before distributing to existing Daily users

1. Produce and inspect both candidate artifacts; launch the x64 candidate on a clean supported Windows machine and confirm runtime prerequisites. Test ARM64 on hardware before claiming support.
2. Connect the WinUI executable to the installer/update lifecycle. The existing Release workflow still packages WPF; this candidate ZIP is not a Velopack update and has no automatic-update implementation.
3. Define and test the legacy-to-WinUI data copy, preserving original settings, history, credentials and plugin files. Include failure/retry and return-to-old-version cases.
4. Validate the supported v2 plugin catalog/packages and clearly list unavailable plugins. See `PLUGIN-MIGRATION-1.1.md`.
5. Check dictation, workflows, API/CLI/Raycast and account/sync in the actual candidate. Publish only to the intended Daily track; do not alter stable or RC feeds.

Release startup registration is currently unavailable until an installation identity is connected. This must be resolved or explicitly included in Daily limitations. Calendar OAuth and pending plugin ports are not completed by packaging the app.
