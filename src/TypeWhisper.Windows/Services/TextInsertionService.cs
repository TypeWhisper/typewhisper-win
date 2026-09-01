using System.Runtime.InteropServices;
using System.Windows.Automation;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Native;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Provides text insertion service behavior.
/// </summary>
public sealed class TextInsertionService : IDisposable
{
    private static readonly TimeSpan ModifierPollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan FocusDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan EnterDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ClipboardRestoreDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ClipboardCapturePollInterval = TimeSpan.FromMilliseconds(50);
    private const int MaxModifierReleaseChecks = 32;
    private const int MaxModifierReleaseChecksAfterNormalization = 8;
    private const int MaxClipboardCaptureReadAttempts = 12;
    private const uint ExpectedCopyInputCount = 4;
    private const uint ExpectedPasteInputCount = 4;
    private const uint ExpectedEnterInputCount = 2;

    private readonly ITextInsertionPlatform _platform;
    private readonly IErrorLogService? _errorLog;
    private readonly bool _ownsPlatform;
    private readonly SemaphoreSlim _clipboardOperationGate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the TextInsertionService class.
    /// </summary>
    public TextInsertionService()
        : this(new WindowsTextInsertionPlatform(), null, ownsPlatform: true)
    {
    }

    /// <summary>
    /// Initializes a new instance of the TextInsertionService class.
    /// </summary>
    public TextInsertionService(IErrorLogService errorLog)
        : this(new WindowsTextInsertionPlatform(), errorLog, ownsPlatform: true)
    {
    }

    internal TextInsertionService(ITextInsertionPlatform platform, IErrorLogService? errorLog = null)
        : this(platform, errorLog, ownsPlatform: false)
    {
    }

    private TextInsertionService(
        ITextInsertionPlatform platform,
        IErrorLogService? errorLog,
        bool ownsPlatform)
    {
        _platform = platform;
        _errorLog = errorLog;
        _ownsPlatform = ownsPlatform;
    }

    /// <summary>
    /// Performs insert text asynchronously.
    /// </summary>
    public async Task<InsertionResult> InsertTextAsync(
        string text,
        bool autoPaste = true,
        bool autoEnter = false,
        IntPtr targetHwnd = default,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
            return InsertionResult.NoText;

        await _clipboardOperationGate.WaitAsync(cancellationToken);
        try
        {
            return await InsertTextCoreAsync(
                text,
                autoPaste,
                autoEnter,
                targetHwnd,
                exactFocusTarget: null,
                requireExactFocus: false,
                cancellationToken);
        }
        finally
        {
            _clipboardOperationGate.Release();
        }
    }

    /// <summary>
    /// Captures the focused text field that belongs to the supplied target window.
    /// A target is returned even when UI Automation cannot identify the field so
    /// locked insertion can fail safely instead of falling back to another field.
    /// </summary>
    internal TextInsertionTarget CaptureTarget(IntPtr targetHwnd) =>
        new(targetHwnd, _platform.CaptureFocusedTextInput(targetHwnd));

    /// <summary>
    /// Inserts text using normal window targeting or, when supplied, only after
    /// the exact field captured for the locked target has focus.
    /// </summary>
    internal async Task<InsertionResult> InsertTextAsync(
        string text,
        bool autoPaste,
        bool autoEnter,
        IntPtr targetHwnd,
        TextInsertionTarget? lockedTarget,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
            return InsertionResult.NoText;

        await _clipboardOperationGate.WaitAsync(cancellationToken);
        try
        {
            return await InsertTextCoreAsync(
                text,
                autoPaste,
                autoEnter,
                lockedTarget?.WindowHandle ?? targetHwnd,
                lockedTarget?.FocusTarget,
                requireExactFocus: lockedTarget is not null,
                cancellationToken: cancellationToken);
        }
        finally
        {
            _clipboardOperationGate.Release();
        }
    }

    private async Task<InsertionResult> InsertTextCoreAsync(
        string text,
        bool autoPaste,
        bool autoEnter,
        IntPtr targetHwnd,
        ITextInsertionFocusTarget? exactFocusTarget,
        bool requireExactFocus,
        CancellationToken cancellationToken)
    {
        if (!autoPaste)
        {
            await _platform.SetClipboardTextAsync(text, cancellationToken);
            return InsertionResult.CopiedToClipboard;
        }

        IClipboardLease? clipboardLease = null;
        var leaseFinalized = false;
        var pasteQueued = false;
        try
        {
            clipboardLease = await _platform.BeginTemporaryClipboardTextAsync(text, cancellationToken);

            if (!await WaitForModifierKeysReleasedAsync(cancellationToken))
            {
                var fallback = await CommitClipboardFallbackAsync(
                    clipboardLease,
                    text,
                    "Auto paste fell back to clipboard: modifier keys stayed pressed before paste.",
                    cancellationToken);
                leaseFinalized = true;
                return fallback;
            }

            if (!await FocusTargetWindowAsync(targetHwnd, cancellationToken))
            {
                var fallback = await CommitClipboardFallbackAsync(
                    clipboardLease,
                    text,
                    "Auto paste fell back to clipboard: target window could not be focused.",
                    cancellationToken);
                leaseFinalized = true;
                return fallback;
            }

            if (requireExactFocus &&
                !await FocusExactTargetAsync(exactFocusTarget, cancellationToken))
            {
                var fallback = await CommitClipboardFallbackAsync(
                    clipboardLease,
                    text,
                    "Auto paste fell back to clipboard: the field focused at recording start could not be restored.",
                    cancellationToken);
                leaseFinalized = true;
                return fallback;
            }

            var pasteInputCount = _platform.SendPasteInput();
            if (pasteInputCount != ExpectedPasteInputCount)
            {
                var fallback = await CommitClipboardFallbackAsync(
                    clipboardLease,
                    text,
                    $"Auto paste fell back to clipboard: Ctrl+V input sent {pasteInputCount}/{ExpectedPasteInputCount} events.",
                    cancellationToken);
                leaseFinalized = true;
                return fallback;
            }

            pasteQueued = true;
            if (autoEnter)
            {
                await _platform.DelayAsync(EnterDelay, cancellationToken);
                var enterInputCount = _platform.SendEnterInput();
                if (enterInputCount != ExpectedEnterInputCount)
                {
                    LogInsertionDiagnostic($"Auto paste sent Ctrl+V, but Enter input sent {enterInputCount}/{ExpectedEnterInputCount} events.");
                }
            }

            await _platform.DelayAsync(ClipboardRestoreDelay, CancellationToken.None);
            await TryRestoreClipboardAsync(clipboardLease);
            leaseFinalized = true;
            cancellationToken.ThrowIfCancellationRequested();
            return InsertionResult.Pasted;
        }
        catch
        {
            if (clipboardLease is not null && !leaseFinalized)
            {
                if (pasteQueued)
                    await _platform.DelayAsync(ClipboardRestoreDelay, CancellationToken.None);
                await TryRestoreClipboardAsync(clipboardLease);
            }

            throw;
        }
        finally
        {
            clipboardLease?.Dispose();
        }
    }

    /// <summary>
    /// Performs try get clipboard text asynchronously.
    /// </summary>
    public async Task<string?> TryGetClipboardTextAsync()
    {
        await _clipboardOperationGate.WaitAsync();
        try
        {
            return (await _platform.TryGetClipboardTextStateAsync(CancellationToken.None)).Text;
        }
        finally
        {
            _clipboardOperationGate.Release();
        }
    }

    /// <summary>
    /// Performs try capture selected text asynchronously.
    /// </summary>
    public async Task<string?> TryCaptureSelectedTextAsync(IntPtr targetHwnd = default)
    {
        await _clipboardOperationGate.WaitAsync();
        try
        {
            return await TryCaptureSelectedTextCoreAsync(targetHwnd);
        }
        finally
        {
            _clipboardOperationGate.Release();
        }
    }

    private async Task<string?> TryCaptureSelectedTextCoreAsync(IntPtr targetHwnd)
    {
        var marker = $"__typewhisper-selection-{Guid.NewGuid():N}__";
        IClipboardLease? clipboardLease;

        try
        {
            clipboardLease = await _platform.BeginTemporaryClipboardTextAsync(
                marker,
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is COMException or ExternalException or InvalidOperationException)
        {
            return null;
        }

        using (clipboardLease)
        {
            try
            {
                if (!await WaitForModifierKeysReleasedAsync(CancellationToken.None))
                    return null;

                if (!await FocusTargetWindowAsync(targetHwnd, CancellationToken.None))
                    return null;

                if (_platform.SendCopyInput() != ExpectedCopyInputCount)
                    return null;

                var capturedState = await WaitForClipboardTextChangeAsync(marker, CancellationToken.None);
                if (capturedState is { } state)
                    _platform.AcceptClipboardSequence(clipboardLease, state.SequenceNumber);

                if (capturedState is not { } captured
                    || string.IsNullOrWhiteSpace(captured.Text)
                    || string.Equals(captured.Text, marker, StringComparison.Ordinal))
                {
                    return null;
                }

                return captured.Text;
            }
            catch (Exception ex) when (ex is COMException or ExternalException or InvalidOperationException)
            {
                return null;
            }
            finally
            {
                await TryRestoreClipboardAsync(clipboardLease);
            }
        }
    }

    private async Task<bool> WaitForModifierKeysReleasedAsync(CancellationToken cancellationToken)
    {
        if (await WaitForModifierKeysReleasedAsync(MaxModifierReleaseChecks, cancellationToken))
            return true;

        var releaseInputCount = _platform.SendModifierKeyUpInputs();
        if (releaseInputCount > 0)
        {
            await _platform.DelayAsync(ModifierPollInterval, cancellationToken);
            if (await WaitForModifierKeysReleasedAsync(
                    MaxModifierReleaseChecksAfterNormalization,
                    cancellationToken))
                return true;
        }

        return !_platform.IsAnyModifierKeyDown();
    }

    private async Task<bool> WaitForModifierKeysReleasedAsync(
        int maxChecks,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < maxChecks; attempt++)
        {
            if (!_platform.IsAnyModifierKeyDown())
                return true;

            await _platform.DelayAsync(ModifierPollInterval, cancellationToken);
        }

        return false;
    }

    private async Task<bool> FocusTargetWindowAsync(
        IntPtr targetHwnd,
        CancellationToken cancellationToken)
    {
        if (targetHwnd == IntPtr.Zero)
        {
            await _platform.DelayAsync(FocusDelay, cancellationToken);
            return true;
        }

        if (IsTargetForeground(targetHwnd))
        {
            await _platform.DelayAsync(FocusDelay, cancellationToken);
            return true;
        }

        _platform.SetForegroundWindow(targetHwnd);
        await _platform.DelayAsync(FocusDelay, cancellationToken);
        if (IsTargetForeground(targetHwnd))
            return true;

        var activationInputCount = _platform.SendForegroundActivationInput();
        if (activationInputCount > 0)
        {
            await _platform.DelayAsync(ModifierPollInterval, cancellationToken);
            _platform.SetForegroundWindow(targetHwnd);
            await _platform.DelayAsync(FocusDelay, cancellationToken);
        }

        return IsTargetForeground(targetHwnd);
    }

    private bool IsTargetForeground(IntPtr targetHwnd)
    {
        var foregroundHwnd = _platform.GetForegroundWindow();
        if (foregroundHwnd == targetHwnd)
            return true;

        if (foregroundHwnd == IntPtr.Zero || targetHwnd == IntPtr.Zero)
            return false;

        var targetRoot = _platform.GetRootWindow(targetHwnd);
        return targetRoot != IntPtr.Zero && targetRoot == _platform.GetRootWindow(foregroundHwnd);
    }

    private async Task<bool> FocusExactTargetAsync(
        ITextInsertionFocusTarget? target,
        CancellationToken cancellationToken)
    {
        if (target is null)
            return false;

        if (target.IsFocused())
            return true;

        if (!target.TryFocus())
            return false;

        await _platform.DelayAsync(FocusDelay, cancellationToken);
        return target.IsFocused();
    }

    private async Task<ClipboardTextState?> WaitForClipboardTextChangeAsync(
        string marker,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxClipboardCaptureReadAttempts; attempt++)
        {
            await _platform.DelayAsync(ClipboardCapturePollInterval, cancellationToken);
            var clipboardState = await _platform.TryGetClipboardTextStateAsync(cancellationToken);
            if (clipboardState.SequenceNumber != 0
                && clipboardState.Text is not null
                && !string.Equals(clipboardState.Text, marker, StringComparison.Ordinal))
            {
                return clipboardState;
            }
        }

        return null;
    }

    private async Task<InsertionResult> CommitClipboardFallbackAsync(
        IClipboardLease clipboardLease,
        string text,
        string diagnostic,
        CancellationToken cancellationToken)
    {
        var committed = await _platform.CommitTemporaryClipboardTextAsync(
            clipboardLease,
            text,
            cancellationToken);
        if (!committed)
        {
            LogInsertionDiagnostic(
                "Auto paste fallback was skipped because the clipboard changed.");
            throw new InvalidOperationException(
                "The clipboard changed before TypeWhisper could complete text insertion.");
        }

        LogInsertionDiagnostic(diagnostic);
        return InsertionResult.CopiedToClipboard;
    }

    private async Task TryRestoreClipboardAsync(IClipboardLease clipboardLease)
    {
        try
        {
            var result = await _platform.RestoreClipboardAsync(
                clipboardLease,
                CancellationToken.None);
            if (result == ClipboardRestoreResult.ClipboardChanged)
            {
                LogInsertionDiagnostic(
                    "Clipboard restore skipped because the clipboard changed after the temporary write.");
            }
        }
        catch (Exception ex)
        {
            LogInsertionDiagnostic($"Clipboard restore failed: {ex.Message}");
        }
    }

    private void LogInsertionDiagnostic(string message)
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

    /// <summary>
    /// Releases native clipboard resources owned by this service.
    /// </summary>
    public void Dispose()
    {
        if (_ownsPlatform && _platform is IDisposable disposablePlatform)
            disposablePlatform.Dispose();

        _clipboardOperationGate.Dispose();
    }
}

internal sealed record TextInsertionTarget(
    IntPtr WindowHandle,
    ITextInsertionFocusTarget? FocusTarget);

internal interface ITextInsertionFocusTarget
{
    bool IsFocused();
    bool TryFocus();
}

internal interface ITextInsertionPlatform
{
    Task<ClipboardTextState> TryGetClipboardTextStateAsync(CancellationToken cancellationToken);
    Task<IClipboardLease> BeginTemporaryClipboardTextAsync(
        string text,
        CancellationToken cancellationToken);
    Task SetClipboardTextAsync(string text, CancellationToken cancellationToken);
    Task<bool> CommitTemporaryClipboardTextAsync(
        IClipboardLease lease,
        string text,
        CancellationToken cancellationToken);
    Task<ClipboardRestoreResult> RestoreClipboardAsync(
        IClipboardLease lease,
        CancellationToken cancellationToken);
    void AcceptClipboardSequence(IClipboardLease lease, uint sequenceNumber);
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
    bool IsAnyModifierKeyDown();
    ITextInsertionFocusTarget? CaptureFocusedTextInput(IntPtr targetHwnd);
    IntPtr GetForegroundWindow();
    bool SetForegroundWindow(IntPtr hwnd);
    uint GetWindowProcessId(IntPtr hwnd);
    IntPtr GetRootWindow(IntPtr hwnd);
    uint SendModifierKeyUpInputs();
    uint SendForegroundActivationInput();
    uint SendCopyInput();
    uint SendPasteInput();
    uint SendEnterInput();
}

internal sealed class WindowsTextInsertionPlatform : ITextInsertionPlatform, IDisposable
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

    private readonly WindowsClipboardTransaction _clipboard = new();

    /// <summary>
    /// Performs try get clipboard text asynchronously.
    /// </summary>
    public async Task<ClipboardTextState> TryGetClipboardTextStateAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await _clipboard.ReadTextStateAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is COMException or ExternalException or InvalidOperationException)
        {
            return new ClipboardTextState(null, NativeMethods.GetClipboardSequenceNumber());
        }
    }

    /// <summary>
    /// Begins a temporary clipboard text operation asynchronously.
    /// </summary>
    public Task<IClipboardLease> BeginTemporaryClipboardTextAsync(
        string text,
        CancellationToken cancellationToken) =>
        _clipboard.BeginTemporaryTextAsync(text, cancellationToken);

    /// <summary>
    /// Sets persistent clipboard text asynchronously.
    /// </summary>
    public Task SetClipboardTextAsync(string text, CancellationToken cancellationToken) =>
        _clipboard.SetPersistentTextAsync(text, cancellationToken);

    /// <summary>
    /// Commits temporary clipboard text as a persistent copy fallback.
    /// </summary>
    public Task<bool> CommitTemporaryClipboardTextAsync(
        IClipboardLease lease,
        string text,
        CancellationToken cancellationToken) =>
        _clipboard.CommitTemporaryTextAsync(lease, text, cancellationToken);

    /// <summary>
    /// Restores the clipboard represented by a temporary lease.
    /// </summary>
    public Task<ClipboardRestoreResult> RestoreClipboardAsync(
        IClipboardLease lease,
        CancellationToken cancellationToken) =>
        _clipboard.RestoreAsync(lease, cancellationToken);

    /// <summary>
    /// Accepts an expected clipboard sequence for a temporary lease.
    /// </summary>
    public void AcceptClipboardSequence(IClipboardLease lease, uint sequenceNumber) =>
        _clipboard.AcceptSequence(lease, sequenceNumber);

    /// <summary>
    /// Performs delay asynchronously.
    /// </summary>
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);

    /// <summary>
    /// Returns whether any modifier key down.
    /// </summary>
    public bool IsAnyModifierKeyDown() =>
        ModifierKeys.Any(key => (NativeMethods.GetAsyncKeyState(key) & unchecked((short)0x8000)) != 0);

    /// <summary>
    /// Captures an editable UI Automation element without reading its text value.
    /// </summary>
    public ITextInsertionFocusTarget? CaptureFocusedTextInput(IntPtr targetHwnd)
    {
        if (targetHwnd == IntPtr.Zero)
            return null;

        try
        {
            var element = AutomationElement.FocusedElement;
            if (!IsElementInWindow(element, targetHwnd) || !IsSupportedTextInput(element))
                return null;

            var runtimeId = element.GetRuntimeId();
            return runtimeId is { Length: > 0 }
                ? new WindowsTextInsertionFocusTarget(element, runtimeId)
                : null;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (COMException)
        {
            return null;
        }
    }

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
    /// Returns the root ancestor for the supplied window handle.
    /// </summary>
    public IntPtr GetRootWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return IntPtr.Zero;

        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        return root == IntPtr.Zero ? hwnd : root;
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

    /// <summary>
    /// Releases native clipboard resources.
    /// </summary>
    public void Dispose() => _clipboard.Dispose();

    private static bool IsSupportedTextInput(AutomationElement element)
    {
        var current = element.Current;
        if (!current.IsEnabled || !current.IsKeyboardFocusable)
            return false;

        if (element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, ignoreDefaultValue: true) is true)
            return false;

        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern) &&
            valuePattern is ValuePattern value)
        {
            return !value.Current.IsReadOnly;
        }

        if ((current.ControlType != ControlType.Edit && current.ControlType != ControlType.Document) ||
            !element.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern) ||
            textPattern is not TextPattern text)
        {
            return false;
        }

        return text.DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is false;
    }

    private static bool IsElementInWindow(AutomationElement element, IntPtr targetHwnd)
    {
        var targetRoot = NativeMethods.GetAncestor(targetHwnd, NativeMethods.GA_ROOT);
        if (targetRoot == IntPtr.Zero)
            targetRoot = targetHwnd;

        var walker = TreeWalker.ControlViewWalker;
        var current = element;
        while (current is not null && current != AutomationElement.RootElement)
        {
            var currentHwnd = (IntPtr)current.Current.NativeWindowHandle;
            if (currentHwnd != IntPtr.Zero)
            {
                var currentRoot = NativeMethods.GetAncestor(currentHwnd, NativeMethods.GA_ROOT);
                if ((currentRoot == IntPtr.Zero ? currentHwnd : currentRoot) == targetRoot)
                    return true;
            }

            current = walker.GetParent(current);
        }

        return false;
    }

    private sealed class WindowsTextInsertionFocusTarget(
        AutomationElement element,
        int[] runtimeId) : ITextInsertionFocusTarget
    {
        public bool IsFocused()
        {
            try
            {
                var focusedRuntimeId = AutomationElement.FocusedElement.GetRuntimeId();
                return focusedRuntimeId is { Length: > 0 } && runtimeId.SequenceEqual(focusedRuntimeId);
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (COMException)
            {
                return false;
            }
        }

        public bool TryFocus()
        {
            try
            {
                element.SetFocus();
                return true;
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (COMException)
            {
                return false;
            }
        }
    }

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
