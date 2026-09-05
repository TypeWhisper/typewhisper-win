# Sync and backup interaction study

Question: can one settings page clearly separate device synchronization from a selective backup/restore flow?

Direction: retain the approved Inter typography, blue source-owned icons, dark surfaces and native switches. Use a sync status card, optional History/Audio rows, example devices, and a separate selective backup flow. No new visual variants.

Run: build `TypeWhisper.WinUIPrototype.csproj -c Debug -r win-x64`, then launch its isolated executable with `--sync-backup`. Also available under Settings > Sync & backup.

Artifact: `PrototypeSyncBackupView.cs`. Integration changes: `PrototypeSettingsCatalog.cs`, `PrototypeSettingsWindow.xaml.cs`, `MainWindow.xaml.cs`, `App.xaml.cs`.

References inspected: Mac `HistorySettingsView.swift`, `BackupRestoreSheets.swift`, and `SettingsBackupExporter.swift`. The prototype represents a subset of backup categories, not a compatible serialization contract. Audio and secrets are excluded in the UI. Production migration still needs schema/platform mapping, conflict semantics and actual service wiring.

Safety: state is session-only in the existing settings dictionary. The real native folder picker returns only a path; no folder scanning, sync, export, restore, credentials or networking occurs. Backup categories and devices are fictional. Confirmation only updates a demo result message.

Validation: Debug win-x64 build passes with zero warnings/errors. Native UI inspected at 1040×780: off-state disables sharing and sync actions, device and backup sections render; backup selection opens, scrolls and reaches the review screen. No new runtime error-log entries. Narrow-row wrapping is implemented but not yet visually verified. Folder-picker cancellation, empty category selection, audio dependency, offline/retry and restore completion still need manual interaction coverage. Existing catalog title scrolls with content.

Decision: ready for Marco's visual feedback; not approved for production integration.
