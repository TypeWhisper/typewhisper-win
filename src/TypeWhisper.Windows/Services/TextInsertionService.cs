using System.Runtime.InteropServices;
using System.Windows;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Native;

namespace TypeWhisper.Windows.Services;

public sealed class TextInsertionService
{
    private static readonly TimeSpan ModifierPollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan FocusDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan EnterDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ClipboardRestoreDelay = TimeSpan.FromMilliseconds(200);
    private const int MaxModifierReleaseChecks = 32;
    private const uint ExpectedPasteInputCount = 4;
    private const uint ExpectedEnterInputCount = 2;

    private readonly ITextInsertionPlatform _platform;
    private readonly IErrorLogService? _errorLog;

    public TextInsertionService()
        : this(new WindowsTextInsertionPlatform(), null)
    {
    }

    public TextInsertionService(IErrorLogService errorLog)
        : this(new WindowsTextInsertionPlatform(), errorLog)
    {
    }

    internal TextInsertionService(ITextInsertionPlatform platform, IErrorLogService? errorLog = null)
    {
        _platform = platform;
        _errorLog = errorLog;
    }

    public async Task<InsertionResult> InsertTextAsync(
        string text,
        bool autoPaste = true,
        bool autoEnter = false,
        IntPtr targetHwnd = default)
    {
        if (string.IsNullOrEmpty(text))
            return InsertionResult.NoText;

        var previousClipboard = await _platform.TryGetClipboardTextAsync();
        await _platform.SetClipboardTextAsync(text);

        if (!autoPaste)
            return InsertionResult.CopiedToClipboard;

        if (!await WaitForModifierKeysReleasedAsync())
        {
            LogInsertionFallback("Auto paste fell back to clipboard: modifier keys stayed pressed before paste.");
            return InsertionResult.CopiedToClipboard;
        }

        if (!await FocusTargetWindowAsync(targetHwnd))
        {
            LogInsertionFallback("Auto paste fell back to clipboard: target window could not be focused.");
            return InsertionResult.CopiedToClipboard;
        }

        var pasteInputCount = _platform.SendPasteInput();
        if (pasteInputCount != ExpectedPasteInputCount)
        {
            LogInsertionFallback($"Auto paste fell back to clipboard: Ctrl+V input sent {pasteInputCount}/{ExpectedPasteInputCount} events.");
            return InsertionResult.CopiedToClipboard;
        }

        if (autoEnter)
        {
            await _platform.DelayAsync(EnterDelay);
            var enterInputCount = _platform.SendEnterInput();
            if (enterInputCount != ExpectedEnterInputCount)
            {
                LogInsertionFallback($"Auto paste sent Ctrl+V, but Enter input sent {enterInputCount}/{ExpectedEnterInputCount} events.");
            }
        }

        await RestorePreviousClipboardAsync(previousClipboard);
        return InsertionResult.Pasted;
    }

    private async Task<bool> WaitForModifierKeysReleasedAsync()
    {
        for (var attempt = 0; attempt < MaxModifierReleaseChecks; attempt++)
        {
            if (!_platform.IsAnyModifierKeyDown())
                return true;

            await _platform.DelayAsync(ModifierPollInterval);
        }

        return !_platform.IsAnyModifierKeyDown();
    }

    private async Task<bool> FocusTargetWindowAsync(IntPtr targetHwnd)
    {
        if (targetHwnd == IntPtr.Zero)
        {
            await _platform.DelayAsync(FocusDelay);
            return true;
        }

        if (_platform.GetForegroundWindow() == targetHwnd)
        {
            await _platform.DelayAsync(FocusDelay);
            return true;
        }

        var focusRequested = _platform.SetForegroundWindow(targetHwnd);
        await _platform.DelayAsync(FocusDelay);
        return focusRequested || _platform.GetForegroundWindow() == targetHwnd;
    }

    private async Task RestorePreviousClipboardAsync(string? previousClipboard)
    {
        await _platform.DelayAsync(ClipboardRestoreDelay);
        if (previousClipboard is null)
            return;

        try
        {
            await _platform.SetClipboardTextAsync(previousClipboard);
        }
        catch
        {
            // Best effort restore.
        }
    }

    private void LogInsertionFallback(string message)
    {
        try
        {
            _errorLog?.AddEntry(message, ErrorCategory.Insertion);
        }
        catch
        {
            // Diagnostics must never block dictation output.
        }
    }
}

internal interface ITextInsertionPlatform
{
    Task<string?> TryGetClipboardTextAsync();
    Task SetClipboardTextAsync(string text);
    Task DelayAsync(TimeSpan delay);
    bool IsAnyModifierKeyDown();
    IntPtr GetForegroundWindow();
    bool SetForegroundWindow(IntPtr hwnd);
    uint SendPasteInput();
    uint SendEnterInput();
}

internal sealed class WindowsTextInsertionPlatform : ITextInsertionPlatform
{
    private const uint ExpectedPasteInputCount = 4;
    private const uint ExpectedEnterInputCount = 2;

    private static readonly int[] ModifierKeys =
    [
        NativeMethods.VK_SHIFT,
        NativeMethods.VK_LSHIFT,
        NativeMethods.VK_RSHIFT,
        NativeMethods.VK_CONTROL,
        NativeMethods.VK_LCONTROL,
        NativeMethods.VK_RCONTROL,
        NativeMethods.VK_MENU,
        NativeMethods.VK_LMENU,
        NativeMethods.VK_RMENU,
        NativeMethods.VK_LWIN,
        NativeMethods.VK_RWIN
    ];

    public async Task<string?> TryGetClipboardTextAsync()
    {
        try
        {
            return await Application.Current.Dispatcher.InvokeAsync(() =>
                Clipboard.ContainsText() ? Clipboard.GetText() : null);
        }
        catch
        {
            return null;
        }
    }

    public Task SetClipboardTextAsync(string text) =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Clipboard.SetText(text);
                    return;
                }
                catch (COMException) when (attempt < 2)
                {
                    Thread.Sleep(50);
                }
            }
        }).Task;

    public Task DelayAsync(TimeSpan delay) => Task.Delay(delay);

    public bool IsAnyModifierKeyDown() =>
        ModifierKeys.Any(key => (NativeMethods.GetAsyncKeyState(key) & unchecked((short)0x8000)) != 0);

    public IntPtr GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    public bool SetForegroundWindow(IntPtr hwnd) => NativeMethods.SetForegroundWindow(hwnd);

    public uint SendPasteInput() =>
        NativeMethods.SendInput(
            ExpectedPasteInputCount,
            [
                KeyInput(NativeMethods.VK_CONTROL, keyUp: false),
                KeyInput(NativeMethods.VK_V, keyUp: false),
                KeyInput(NativeMethods.VK_V, keyUp: true),
                KeyInput(NativeMethods.VK_CONTROL, keyUp: true)
            ],
            Marshal.SizeOf<NativeMethods.INPUT>());

    public uint SendEnterInput() =>
        NativeMethods.SendInput(
            ExpectedEnterInputCount,
            [
                KeyInput(NativeMethods.VK_RETURN, keyUp: false),
                KeyInput(NativeMethods.VK_RETURN, keyUp: true)
            ],
            Marshal.SizeOf<NativeMethods.INPUT>());

    private static NativeMethods.INPUT KeyInput(int virtualKey, bool keyUp) =>
        new()
        {
            type = NativeMethods.INPUT_KEYBOARD,
            u = new NativeMethods.INPUTUNION
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = (ushort)virtualKey,
                    dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0
                }
            }
        };
}

public enum InsertionResult
{
    Pasted,
    CopiedToClipboard,
    NoText,
    ActionHandled
}
