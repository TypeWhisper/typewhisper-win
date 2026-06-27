using System.Runtime.InteropServices;
using System.Windows;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Native;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Provides text insertion service behavior.
/// </summary>
public sealed class TextInsertionService
{
    private static readonly TimeSpan ModifierPollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan FocusDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan EnterDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ClipboardRestoreDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ClipboardCapturePollInterval = TimeSpan.FromMilliseconds(50);
    private const int MaxModifierReleaseChecks = 32;
    private const int MaxModifierReleaseChecksAfterNormalization = 8;
    private const int MaxClipboardCaptureReadAttempts = 12;
    private const uint ExpectedCopyInputCount = 4;
    private const uint ExpectedPasteInputCount = 4;
    private const uint ExpectedEnterInputCount = 2;

    private readonly ITextInsertionPlatform _platform;
    private readonly IErrorLogService? _errorLog;

    /// <summary>
    /// Initializes a new instance of the TextInsertionService class.
    /// </summary>
    public TextInsertionService()
        : this(new WindowsTextInsertionPlatform(), null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the TextInsertionService class.
    /// </summary>
    public TextInsertionService(IErrorLogService errorLog)
        : this(new WindowsTextInsertionPlatform(), errorLog)
    {
    }

    internal TextInsertionService(ITextInsertionPlatform platform, IErrorLogService? errorLog = null)
    {
        _platform = platform;
        _errorLog = errorLog;
    }

    /// <summary>
    /// Performs insert text asynchronously.
    /// </summary>
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

    /// <summary>
    /// Performs try get clipboard text asynchronously.
    /// </summary>
    public Task<string?> TryGetClipboardTextAsync() => _platform.TryGetClipboardTextAsync();

    /// <summary>
    /// Performs try capture selected text asynchronously.
    /// </summary>
    public async Task<string?> TryCaptureSelectedTextAsync(IntPtr targetHwnd = default)
    {
        var previousClipboard = await _platform.TryGetClipboardTextAsync();
        var marker = $"__typewhisper-selection-{Guid.NewGuid():N}__";

        try
        {
            await _platform.SetClipboardTextAsync(marker);
        }
        catch (COMException)
        {
            return null;
        }
        catch (ExternalException)
        {
            return null;
        }

        if (!await WaitForModifierKeysReleasedAsync())
        {
            await RestorePreviousClipboardAsync(previousClipboard, clearWhenMissing: true);
            return null;
        }

        if (!await FocusTargetWindowAsync(targetHwnd))
        {
            await RestorePreviousClipboardAsync(previousClipboard, clearWhenMissing: true);
            return null;
        }

        if (_platform.SendCopyInput() != ExpectedCopyInputCount)
        {
            await RestorePreviousClipboardAsync(previousClipboard, clearWhenMissing: true);
            return null;
        }

        var capturedText = await WaitForClipboardTextChangeAsync(marker);
        await RestorePreviousClipboardAsync(previousClipboard, clearWhenMissing: true);

        return string.IsNullOrWhiteSpace(capturedText) || string.Equals(capturedText, marker, StringComparison.Ordinal)
            ? null
            : capturedText;
    }

    private async Task<bool> WaitForModifierKeysReleasedAsync()
    {
        if (await WaitForModifierKeysReleasedAsync(MaxModifierReleaseChecks))
            return true;

        var releaseInputCount = _platform.SendModifierKeyUpInputs();
        if (releaseInputCount > 0)
        {
            await _platform.DelayAsync(ModifierPollInterval);
            if (await WaitForModifierKeysReleasedAsync(MaxModifierReleaseChecksAfterNormalization))
                return true;
        }

        return !_platform.IsAnyModifierKeyDown();
    }

    private async Task<bool> WaitForModifierKeysReleasedAsync(int maxChecks)
    {
        for (var attempt = 0; attempt < maxChecks; attempt++)
        {
            if (!_platform.IsAnyModifierKeyDown())
                return true;

            await _platform.DelayAsync(ModifierPollInterval);
        }

        return false;
    }

    private async Task<bool> FocusTargetWindowAsync(IntPtr targetHwnd)
    {
        if (targetHwnd == IntPtr.Zero)
        {
            await _platform.DelayAsync(FocusDelay);
            return true;
        }

        var targetProcessId = _platform.GetWindowProcessId(targetHwnd);
        if (IsTargetForeground(targetHwnd, targetProcessId))
        {
            await _platform.DelayAsync(FocusDelay);
            return true;
        }

        var focusRequested = _platform.SetForegroundWindow(targetHwnd);
        await _platform.DelayAsync(FocusDelay);
        if (focusRequested || IsTargetForeground(targetHwnd, targetProcessId))
            return true;

        var activationInputCount = _platform.SendForegroundActivationInput();
        if (activationInputCount > 0)
        {
            await _platform.DelayAsync(ModifierPollInterval);
            focusRequested = _platform.SetForegroundWindow(targetHwnd);
            await _platform.DelayAsync(FocusDelay);
        }

        return focusRequested || IsTargetForeground(targetHwnd, targetProcessId);
    }

    private bool IsTargetForeground(IntPtr targetHwnd, uint targetProcessId)
    {
        var foregroundHwnd = _platform.GetForegroundWindow();
        if (foregroundHwnd == targetHwnd)
            return true;

        return foregroundHwnd != IntPtr.Zero
            && targetProcessId != 0
            && _platform.GetWindowProcessId(foregroundHwnd) == targetProcessId;
    }

    private async Task<string?> WaitForClipboardTextChangeAsync(string marker)
    {
        for (var attempt = 0; attempt < MaxClipboardCaptureReadAttempts; attempt++)
        {
            await _platform.DelayAsync(ClipboardCapturePollInterval);
            var clipboardText = await _platform.TryGetClipboardTextAsync();
            if (!string.Equals(clipboardText, marker, StringComparison.Ordinal))
                return clipboardText;
        }

        return null;
    }

    private async Task RestorePreviousClipboardAsync(string? previousClipboard, bool clearWhenMissing = false)
    {
        await _platform.DelayAsync(ClipboardRestoreDelay);
        if (previousClipboard is null)
        {
            if (clearWhenMissing)
            {
                try
                {
                    await _platform.ClearClipboardTextAsync();
                }
                catch (COMException)
                {
                    // Best effort restore.
                }
                catch (ExternalException)
                {
                    // Best effort restore.
                }
            }
            return;
        }

        try
        {
            await _platform.SetClipboardTextAsync(previousClipboard);
        }
        catch (COMException)
        {
            // Best effort restore.
        }
        catch (ExternalException)
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
    Task ClearClipboardTextAsync();
    Task DelayAsync(TimeSpan delay);
    bool IsAnyModifierKeyDown();
    IntPtr GetForegroundWindow();
    bool SetForegroundWindow(IntPtr hwnd);
    uint GetWindowProcessId(IntPtr hwnd);
    uint SendModifierKeyUpInputs();
    uint SendForegroundActivationInput();
    uint SendCopyInput();
    uint SendPasteInput();
    uint SendEnterInput();
}

internal sealed class WindowsTextInsertionPlatform : ITextInsertionPlatform
{
    private const uint ExpectedCopyInputCount = 4;
    private const uint ExpectedPasteInputCount = 4;
    private const uint ExpectedEnterInputCount = 2;
    private const uint ExpectedForegroundActivationInputCount = 2;

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

    private static readonly int[] ModifierReleaseKeys =
    [
        NativeMethods.VK_LSHIFT,
        NativeMethods.VK_RSHIFT,
        NativeMethods.VK_SHIFT,
        NativeMethods.VK_LCONTROL,
        NativeMethods.VK_RCONTROL,
        NativeMethods.VK_CONTROL,
        NativeMethods.VK_LMENU,
        NativeMethods.VK_RMENU,
        NativeMethods.VK_MENU,
        NativeMethods.VK_LWIN,
        NativeMethods.VK_RWIN
    ];

    /// <summary>
    /// Performs try get clipboard text asynchronously.
    /// </summary>
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

    /// <summary>
    /// Sets clipboard text asynchronously.
    /// </summary>
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

    /// <summary>
    /// Clears clipboard text asynchronously.
    /// </summary>
    public Task ClearClipboardTextAsync() =>
        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Clipboard.Clear();
                    return;
                }
                catch (COMException) when (attempt < 2)
                {
                    Thread.Sleep(50);
                }
            }
        }).Task;

    /// <summary>
    /// Performs delay asynchronously.
    /// </summary>
    public Task DelayAsync(TimeSpan delay) => Task.Delay(delay);

    /// <summary>
    /// Returns whether any modifier key down.
    /// </summary>
    public bool IsAnyModifierKeyDown() =>
        ModifierKeys.Any(key => (NativeMethods.GetAsyncKeyState(key) & unchecked((short)0x8000)) != 0);

    /// <summary>
    /// Returns foreground window.
    /// </summary>
    public IntPtr GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    /// <summary>
    /// Sets foreground window.
    /// </summary>
    public bool SetForegroundWindow(IntPtr hwnd) => NativeMethods.SetForegroundWindow(hwnd);

    /// <summary>
    /// Returns the process id that owns the supplied window handle.
    /// </summary>
    public uint GetWindowProcessId(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return 0;

        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        return processId;
    }

    /// <summary>
    /// Sends key-up events for modifier keys that Windows still reports as pressed.
    /// </summary>
    public uint SendModifierKeyUpInputs()
    {
        var inputs = ModifierReleaseKeys
            .Where(IsKeyDown)
            .Select(key => KeyInput(key, keyUp: true))
            .ToArray();

        return inputs.Length == 0
            ? 0
            : NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    /// <summary>
    /// Sends a neutral Alt tap so Windows allows a foreground retry.
    /// </summary>
    public uint SendForegroundActivationInput() =>
        NativeMethods.SendInput(
            ExpectedForegroundActivationInputCount,
            [
                KeyInput(NativeMethods.VK_MENU, keyUp: false),
                KeyInput(NativeMethods.VK_MENU, keyUp: true)
            ],
            Marshal.SizeOf<NativeMethods.INPUT>());

    /// <summary>
    /// Sends copy input.
    /// </summary>
    public uint SendCopyInput() =>
        NativeMethods.SendInput(
            ExpectedCopyInputCount,
            [
                KeyInput(NativeMethods.VK_CONTROL, keyUp: false),
                KeyInput(NativeMethods.VK_C, keyUp: false),
                KeyInput(NativeMethods.VK_C, keyUp: true),
                KeyInput(NativeMethods.VK_CONTROL, keyUp: true)
            ],
            Marshal.SizeOf<NativeMethods.INPUT>());

    /// <summary>
    /// Sends paste input.
    /// </summary>
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

    /// <summary>
    /// Sends enter input.
    /// </summary>
    public uint SendEnterInput() =>
        NativeMethods.SendInput(
            ExpectedEnterInputCount,
            [
                KeyInput(NativeMethods.VK_RETURN, keyUp: false),
                KeyInput(NativeMethods.VK_RETURN, keyUp: true)
            ],
            Marshal.SizeOf<NativeMethods.INPUT>());

    private static bool IsKeyDown(int virtualKey) =>
        (NativeMethods.GetAsyncKeyState(virtualKey) & unchecked((short)0x8000)) != 0;

    internal static NativeMethods.INPUT KeyInput(int virtualKey, bool keyUp) =>
        new()
        {
            type = NativeMethods.INPUT_KEYBOARD,
            u = new NativeMethods.INPUTUNION
            {
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = (ushort)virtualKey,
                    dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0,
                    dwExtraInfo = NativeMethods.SelfInjectedInputMarker
                }
            }
        };
}

/// <summary>
/// Lists the supported insertion result values.
/// </summary>
public enum InsertionResult
{
    /// <summary>
    /// Represents the pasted option.
    /// </summary>
    Pasted,
    /// <summary>
    /// Represents the copied to clipboard option.
    /// </summary>
    CopiedToClipboard,
    /// <summary>
    /// Represents the no text option.
    /// </summary>
    NoText,
    /// <summary>
    /// Represents the action handled option.
    /// </summary>
    ActionHandled
}
