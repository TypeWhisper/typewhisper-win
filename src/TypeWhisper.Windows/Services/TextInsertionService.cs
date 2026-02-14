using System.Runtime.InteropServices;
using System.Windows;
using GregsStack.InputSimulatorStandard;
using GregsStack.InputSimulatorStandard.Native;

namespace TypeWhisper.Windows.Services;

public sealed class TextInsertionService
{
    private readonly InputSimulator _inputSimulator = new();

    public async Task<InsertionResult> InsertTextAsync(string text, bool autoPaste = true)
    {
        if (string.IsNullOrEmpty(text))
            return InsertionResult.NoText;

        // Save current clipboard
        string? previousClipboard = null;
        try
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (Clipboard.ContainsText())
                    previousClipboard = Clipboard.GetText();
            });
        }
        catch { /* Clipboard might be locked */ }

        // Set text to clipboard (must be on UI thread)
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Clipboard.SetText(text);
                    break;
                }
                catch (COMException) when (attempt < 2)
                {
                    Thread.Sleep(50);
                }
            }
        });

        if (!autoPaste)
            return InsertionResult.CopiedToClipboard;

        // Small delay for focus to return to target window
        await Task.Delay(100);

        // Simulate Ctrl+V using InputSimulator (same as old version)
        _inputSimulator.Keyboard.ModifiedKeyStroke(
            VirtualKeyCode.CONTROL, VirtualKeyCode.VK_V);

        // Restore previous clipboard after a short delay
        await Task.Delay(200);
        if (previousClipboard is not null)
        {
            try
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Clipboard.SetText(previousClipboard);
                });
            }
            catch { /* Best effort restore */ }
        }

        return InsertionResult.Pasted;
    }
}

public enum InsertionResult
{
    Pasted,
    CopiedToClipboard,
    NoText
}
