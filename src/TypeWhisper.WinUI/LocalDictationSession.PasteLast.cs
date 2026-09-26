using System.Runtime.InteropServices;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal sealed partial class LocalDictationSession
{
    // Uses the dictation paste path, so the previous clipboard is restored afterwards.
    // activate brings back a window that lost the foreground to the tray menu.
    internal async Task<LastDictationPasteResult> PasteLastCompletedAsync(IntPtr target, bool activate, bool blocked, bool busy)
    {
        if (_disposed || blocked) return LastDictationPasteResult.Ignored;
        if (busy || !await _gate.WaitAsync(0)) return LastDictationPasteResult.Busy;
        try
        {
            return await LastDictationPaste.RunAsync(LastCompletedDictation, _disposed, _audio.IsRecording, target != IntPtr.Zero, async text =>
            {
                if (activate && !await ActivateAsync(target)) return false;
                // A global shortcut fires while its modifiers are still held; Ctrl+V must not combine with them.
                for (var attempt = 0; attempt < 80 && ModifiersHeld(); attempt++) await Task.Delay(25);
                return await _inserter.InsertAsync(text, target);
            });
        }
        finally { _gate.Release(); }
    }

    private static async Task<bool> ActivateAsync(IntPtr target)
    {
        if (IsIconic(target)) ShowWindow(target, 9);
        SetForegroundWindow(target);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (GetForegroundWindow() == target) return true;
            await Task.Delay(50);
        }
        return false;
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
}
