# Opt-in native clipboard tests

The current tests require a private Windows window station and desktop with an isolated native clipboard. Creation must succeed before any clipboard operation; there is no fallback to the interactive clipboard. No files are opened/executed and no input is injected into other applications. WPF supplies a message-only owner window, STA dispatcher and paste-target controls; the transaction is compiled with `TYPEWHISPER_WINUI`, matching the strict WinUI policy.

```powershell
$env:TYPEWHISPER_RUN_LIVE_CLIPBOARD_TESTS = '1'
dotnet test tests/TypeWhisper.Clipboard.LiveTests/TypeWhisper.Clipboard.LiveTests.csproj --logger 'console;verbosity=detailed'
```

One serialized scenario contains the previously exercised ten cases:

- Empty clipboard and Unicode text.
- Native CF_BITMAP pixel equality and CF_DIB byte equality.
- Combined HTML-format bytes, RTF, opaque binary data and text.
- CF_HDROP with Unicode, emoji, spaces and unusual extensions plus Preferred DropEffect. Paths are fixture metadata, not actual files.
- Unrenderable System.Drawing.Bitmap, FileContents and custom formats: strict capture must fail without changing the clipboard sequence or existing text.
- A newer simulated user copy must survive restoration of an older lease.

Additional prepared cases paste Unicode/multiline text into WPF TextBox and RichTextBox, then exercise a real WPF OLE DataObject with stream-backed FileGroupDescriptorW/FileContents representations. These are control-level tests, not verification of Notepad, browser editors, Outlook or arbitrary indexed OLE providers.

The private clipboard is captured with strict preservation before seeding fixtures. Restoration uses the last known test sequence, not an arbitrary current sequence; newer changes are preserved. Unsupported original contents abort before replacement. STA dispatcher pumping is required for asynchronous clipboard-busy retries.

## Observed result and limitations

Before isolation was added, the corrected run passed the original ten cases and restored the clipboard present at the start of that run. The first run exposed a missing synchronization context in the test harness: clipboard-busy retry switched threads and final restoration failed. Its in-memory backup was lost when the test process ended. Do not describe that first run as successfully restoring user contents.

The latest isolated run was blocked by Win32 error 5 (`Access is denied`) at CreateWindowStation, before accessing any clipboard. The new OLE and paste-target cases have compiled but have NOT run. Use an appropriately provisioned disposable Windows test session; do not silently remove isolation or elevate the user's application.

This validates native snapshot/replacement/restoration, not full target-app Ctrl+V consumption, rendered HTML validity, actual file operations, or arbitrary OLE virtual-file providers. A process crash or exhausted restore retries can still lose an in-memory backup: use a disposable Windows session for unattended testing.
