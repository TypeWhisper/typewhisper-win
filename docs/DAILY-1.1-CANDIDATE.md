# TypeWhisper Daily delivery and 1.0 migration

The Candidate workflow builds x64 and ARM64 validation artifacts on relevant pull requests and manual dispatches. Scheduled main runs publish a Daily prerelease. The app is always named **TypeWhisper** and its executable is **TypeWhisper.exe**; Daily is an update channel, not a product name.

## Installation compatibility

| Existing installation | Package ID | Daily feed | Entry point after update |
| --- | --- | --- | --- |
| Original TypeWhisper 1.0 | `TypeWhisper` | `win-<arch>-daily` | `TypeWhisper.exe` |
| Separate early 1.1 Daily | `TypeWhisperDaily` | `win-<arch>-winui-daily` | `TypeWhisper.exe` |

The internal `TypeWhisperDaily` identity is retained for existing installations. Both packages use the TypeWhisper display title. Each updater requires its installed package identity and rejects pre-1.1 packages. Stable and RC never fall back to the old WPF feeds. An exact owned startup command pointing at `TypeWhisper.WinUI.exe` is migrated to `TypeWhisper.exe` on every installed launch; no registration is created and unrelated commands are left untouched. An existing 1.0 Startup-folder shortcut remains in place without creating a second registration, and the startup settings can disable that owned shortcut.

Both package variants are built and the original-installation package is inspected before publication. Original Daily assets are kept in the `legacy-upgrade` artifact directory until rollout is enabled. A main dispatch with both `publish_daily=true` and `publish_legacy_daily=true` publishes both feeds. Set the repository variable `LEGACY_DAILY_UPGRADE_ENABLED=true` only after installed upgrade acceptance to continue publishing the original feed on scheduled runs. Without that gate, the separate 1.1 Daily continues receiving updates and the old Daily feed is unchanged.

The app uses installed .NET 10 with a bundled Windows App SDK. The build SDK remains 26100; the configured minimum OS is 19041. Changing that minimum is not native compatibility evidence. Validate older Windows releases and ARM64 hardware separately.

## Profile migration

Release profiles remain under `%LOCALAPPDATA%/TypeWhisper-WinUI`. Development profiles remain separate. Before opening profile stores, an absent release profile imports from `%LOCALAPPDATA%/TypeWhisper-UserData`, falling back to `%LOCALAPPDATA%/TypeWhisper` only when the former root is absent. Existing 1.1 profiles are never merged or overwritten automatically.

Migration stages on the destination volume and publishes with a no-overwrite directory rename. It reads the source without modifying it. Linked paths, malformed settings and invalid encrypted credentials stop the migration. Download, copy or cancellation failures leave no visible partial profile and can be retried. Close the previous app first. A killed process can leave an unused `.typewhisper-import-*` staging directory; it is not a completed profile.

The importer carries over:

- Dictionary, snippets, workflows and history text through the validated portable backup format.
- Main and supplementary recording shortcuts, recording mode, output/history choices, retention, text normalization, language hints, microphone priorities, audio options, recorder sources, vocabulary boosting and onboarding completion.
- Live-text visibility, font size, preview timeout, overlay position and widgets, with the previous indicator style mapped to its closest current layout. Profiles containing only license or plugin state are also eligible for migration.
- The previous plugin/model selection, without choosing a replacement cloud service when unavailable.
- Plugin settings and API keys. Keys are decrypted in the current Windows user context and written to the new encrypted secret store; license activation IDs and the unchanged encrypted license format are preserved.
- Compatible portable plugins downloaded from the existing v2 catalog. Legacy assemblies are never loaded. Enabled/disabled state is retained.
- Available `Models` directories, including a configured external model-storage location, copied into the new profile without old runtime executables. File Memory's `memories.json` is also copied.

UI-only preferences without a current equivalent, account sign-ins, archived audio, recordings, recovery audio and legacy automation/sync configuration remain in the previous profile. History is imported without old audio references. Unavailable plugins are listed in `legacy-migration-report.json` and shown after startup. Unchanged settings formats are preserved, but provider-specific compatibility still needs real-profile acceptance; model files may require a provider-specific download if layouts changed.

`legacy-import.json` version 2 records the extended import. Reports contain plugin identifiers and migration notes, never keys or transcript content. Existing early 1.1 profiles keep their own settings and data; a separate reviewed import is needed to bring in additional 1.0 data.

## Acceptance before enabling the original Daily feed

1. Install an actual 1.0 Daily in an isolated Windows VM; configure a test shortcut, dictionary entry, workflow and local model or test provider credentials.
2. Upgrade through that installation's updater using the original-identity 1.1 package. Check restart, desktop/Start menu shortcuts, startup, uninstall identity, retained license and profile, and actual dictation.
3. Install a second 1.1 Daily through the new app to prove subsequent updates still use the correct feed.
4. Upgrade the separate early 1.1 Daily and check the executable rename and owned startup registration.
5. Exercise interrupted/offline migration, existing 1.1 profiles, missing providers and rollback. A downgrade does not copy new 1.1 edits back into the preserved 1.0 profile.
6. Repeat native acceptance for older Windows and ARM64 before expanding the support claim.

Automated tests and a development UI smoke test do not replace the installed 1.0-to-1.1-to-next-1.1 sequence. Stable delivery is a separate rollout.
