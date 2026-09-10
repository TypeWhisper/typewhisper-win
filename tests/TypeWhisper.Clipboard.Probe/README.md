# Manual clipboard diagnostic probe

Windows-only interactive helper for investigating native clipboard snapshots and file-list/text restoration. It compiles the existing clipboard transaction implementation directly with the WinUI conditional symbol. It is not an automated regression test or part of the shipping app.

## Safety

- Run only with disposable clipboard contents. `backup`, `files`, `stage`, `restore` and `finish` can modify the **real desktop clipboard**.
- Backups exist only in process memory. A crash, closed terminal or disconnected input can lose them; restoration is not guaranteed. Always finish the sequence before closing the process.
- Do not copy other data or run another clipboard-writing tool during the sequence. Sequence guards may refuse restoration when another owner changes the clipboard; do not bypass those guards.
- No microphone recording or keyboard input is sent. Perform the paste manually in an empty test document, never in a shell or a document with unsaved work. Fixture files are referenced, not opened or executed.
- Diagnostic exceptions are printed to the terminal. Review output before sharing it.

## Build and run

From the repository root with the .NET 10 SDK on Windows:

```powershell
dotnet build tests/TypeWhisper.Clipboard.Probe
dotnet run --project tests/TypeWhisper.Clipboard.Probe --no-build
```

Enter commands one at a time and inspect each result:

1. `inspect` attempts a snapshot without replacing clipboard contents. Check `SNAPSHOT_OK` and `CLIPBOARD_SEQUENCE_UNCHANGED`. This uses reflection against a private method and may need updating if the implementation changes.
2. `backup` captures the starting clipboard and replaces it with a baseline string.
3. `files` places references to this probe's two source/project files on the clipboard. Running from the repository root is required.
4. `stage` temporarily replaces the file list with `TypeWhisper Notepad probe: Grüße 123.`
5. Manually paste into an empty Notepad document and check that the entire text appears.
6. `restore` attempts to restore the file-list clipboard. Check the reported result.
7. `finish` attempts to restore the original backup and exits. Check `BACKUP_RESTORE` rather than assuming success.

If a command reports an error, inspect it before continuing; do not blindly replay the sequence. This probe does not reproduce the complete microphone/hotkey/dictation pipeline and does not establish clipboard compatibility across all applications or formats.
