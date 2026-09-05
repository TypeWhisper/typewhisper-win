# Prototype expert walkthrough — 2026-09-05

Scope: safe local settings tasks, not a participant study or complete accessibility certification. Preserve the approved visual system. No production integration.

## Observed paths

- 1040×780: search `license`, press Enter → Account & about opens. Pass.
- Open license choice, press Escape → only popup closes, visible focus returns to field. Pass. Earlier immediate captures were stale; fresh captures show the popup correctly.
- 740×560 (`--sync-backup --settings-small`): folder and backup actions stack below text, sections remain vertically scrollable, footer stays visible. Pass.
- Open backup selection → first category gets keyboard focus. Escape → backup home, launch button receives focus, settings stays open. Pass after correction.

## Changes

- Moderate / high confidence: backup review offered only cancel/confirm, with no way to revise the chosen categories. Added Back preserving the selection (`PrototypeSyncBackupView.cs`). Data retention verified in code; full Back interaction still pending.
- Moderate / high confidence: settings-level Escape previously closed the window while a backup preview was open. Added preview-first dismissal and deferred focus transfer (`PrototypeSyncBackupView.cs`, `PrototypeSettingsWindow.xaml.cs`); retested live.
- Added isolated `--settings-small` logical viewport fixture (740×560). No system DPI or privacy setting is changed.

## Build and regression checks

Debug win-x64 build: zero warnings/errors. All seven existing check projects completed successfully: file queue, history model, lexicon, models/wizard, settings search, shortcuts, usage statistics. These are model tests, not proof of real audio, sync, licensing, or complete UI correctness.

## Remaining coverage

Actual 200% DPI and multi-monitor changes remain untested in this pass; a small logical window does not replace them. Full keyboard walkthroughs of Recorder, Workflow editor, Wizard, date-range picker and every settings category remain open. Automation remains a placeholder. No claim that the entire prototype is ready for production.

Normal launch omits `--settings-small`; use existing page flags such as `--account`, `--sync-backup`, `--statistics`.
