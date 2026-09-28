using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace TypeWhisper.WinUI;

// Capture identity only: never read the user's field contents or select its text.
internal sealed class OriginalDictationField : IDisposable
{
    private readonly IUIAutomation2 _automation = (IUIAutomation2)new CUIAutomation8();
    private IUIAutomationElement? _element;
    private readonly IntPtr _window;
    private readonly uint _process;
    private int? _windowHostProcess;
    private OriginalDictationField(IntPtr window, uint process)
    {
        _window = window; _process = process;
        // A cold provider may need to initialize its accessibility tree. The old
        // 200 ms capture budget could fail the first dictation but succeed later.
        _automation.ConnectionTimeout = 2000; _automation.TransactionTimeout = 2000;
    }

    internal static async Task<OriginalDictationField?> CaptureAsync(IntPtr window, uint process, CancellationToken cancellation)
    {
        OriginalDictationField? target = null;
        try
        {
            target = new(window, process);
            var field = target;
            if (await OriginalFieldFocus.CaptureAsync(field.CaptureFocused, () => GetForegroundWindow() == window,
                ct => Task.Delay(50, ct), cancellation))
            {
                target._automation.ConnectionTimeout = 200;
                target._automation.TransactionTimeout = 200;
                AppDiagnostics.Write("field.capture.success");
                return target;
            }
        }
        catch (OperationCanceledException) { target?.Dispose(); throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("field.capture.exception", ex); }
        AppDiagnostics.Write("field.capture.failed");
        target?.Dispose(); return null;
    }

    private bool CaptureFocused()
    {
        Release(_element); _element = null;
        try { _element = _automation.GetFocusedElement(); }
        // UIA_E_ELEMENTNOTAVAILABLE and similar races are transient while focus settles: retry.
        catch (COMException ex) { AppDiagnostics.Write("field.capture.transient", ex); return false; }
        finally
        {
            // The first query may wait for a cold provider; retries must stay short.
            _automation.ConnectionTimeout = 250; _automation.TransactionTimeout = 250;
        }
        DiagnoseCapture();
        return IsCurrent();
    }

    // These probes query the same provider as IsCurrent. A transient provider failure must leave
    // the verdict to IsCurrent, which lets the capture retry, instead of ending the capture here.
    private void DiagnoseCapture()
    {
        try
        {
            AppDiagnostics.Write(_element is null ? "field.capture.no-element" : $"field.capture.element control={_element.CurrentControlType}");
            if (_element?.CurrentControlType is 50025 or 50026)
                AppDiagnostics.Write($"field.capture.custom focusable={_element.CurrentIsKeyboardFocusable != 0} writable={HasWritableTextPattern()}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("field.capture.probe-failed", ex); }
    }

    private bool IsValid()
    {
        GetWindowThreadProcessId(_window, out var process);
        if (process != _process || _element is null ||
            !OriginalFieldFocus.BelongsToWindowProcess(_element.CurrentProcessId, _process, WindowHostProcess) ||
            _element.CurrentIsEnabled == 0 || _element.CurrentIsPassword != 0 ||
            !OriginalFieldFocus.IsEditableControl(_element.CurrentControlType,
                _element.CurrentIsKeyboardFocusable != 0, HasWritableTextPattern, _element.CurrentClassName)) return false;
        var walker = _automation.RawViewWalker;
        IUIAutomationElement? parent = null;
        try
        {
            var current = _element;
            for (var depth = 0; depth < 64; depth++)
            {
                if ((IntPtr)current.CurrentNativeWindowHandle == _window) return true;
                var next = walker.GetParentElement(current); Release(parent); parent = next;
                if (next is null) return false;
                current = next;
            }
            return false;
        }
        finally { Release(parent); Release(walker); }
    }

    private int WindowHostProcess()
    {
        if (_windowHostProcess is { } known) return known;
        IUIAutomationElement? window = null;
        try { window = _automation.ElementFromHandle(_window); _windowHostProcess = window.CurrentProcessId; }
        catch (COMException) { return 0; }
        finally { Release(window); }
        return _windowHostProcess.Value;
    }

    internal bool IsCurrent()
    {
        try
        {
            if (GetForegroundWindow() != _window) { AppDiagnostics.Write("field.verify.other-window"); return false; }
            if (!IsValid()) { AppDiagnostics.Write("field.verify.invalid-element"); return false; }
            var focused = _automation.GetFocusedElement();
            try
            {
                var matches = _automation.CompareElements(_element, focused) != 0;
                if (!matches) AppDiagnostics.Write("field.verify.different-element");
                return matches;
            }
            finally { Release(focused); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { AppDiagnostics.Write("field.verify.exception", ex); return false; }
    }

    private bool HasWritableTextPattern()
    {
        object? pattern = null;
        IUIAutomationTextRange? range = null;
        try
        {
            try { pattern = _element!.GetCurrentPattern(10002); } // UIA_ValuePatternId
            catch (COMException) { }
            if (pattern is IUIAutomationValuePattern value) return value.CurrentIsReadOnly == 0;
            Release(pattern); pattern = null;
            try { pattern = _element!.GetCurrentPattern(10014); } // UIA_TextPatternId
            catch (COMException) { }
            if (pattern is not IUIAutomationTextPattern text) return false;
            range = text.DocumentRange;
            // UIA_IsReadOnlyAttributeId. Query editability only, never the text itself.
            return range.GetAttributeValue(40015) is false;
        }
        finally { Release(range); Release(pattern); }
    }

    internal async Task<bool> RestoreAsync(CancellationToken cancellation)
    {
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var expired = OriginalFieldFocus.Deadline();
            // Avoid disturbing the caret when the user stayed in the original field.
            if (IsCurrent()) return true;
            if (!IsValid()) return false;
            if (expired()) return false;
            if (IsIconic(_window)) ShowWindowAsync(_window, 9);
            if (!await OriginalFieldFocus.RestoreWindowAsync(() => GetForegroundWindow() == _window, IsValid,
                () => SetForegroundWindow(_window), () =>
                {
                    ActivateWithInputThread(expired);
                    // UIA providers (including Chromium/Electron) may activate the
                    // host only when the captured editable element receives focus.
                    // Do not require foreground ownership before requesting it.
                    if (IsValid() && !expired())
                    {
                        AppDiagnostics.Write("field.restore.request-element-focus");
                        _element!.SetFocus();
                    }
                },
                ct => Task.Delay(25, ct), cancellation, expired))
            {
                AppDiagnostics.Write("field.restore.window-activation-failed");
                DiagnoseFocus();
                return false;
            }
            return await OriginalFieldFocus.RestoreAsync(IsCurrent,
                () => GetForegroundWindow() == _window && IsValid(),
                () => _element!.SetFocus(), ct => Task.Delay(25, ct), cancellation, expired);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException) { AppDiagnostics.Write("field.restore.exception", ex); return false; }
    }

    private void ActivateWithInputThread(Func<bool> expired)
    {
        // Input attachment must remain synchronous and be undone on the same thread.
        // No synthetic keystrokes and no replacement of the captured UIA element.
        var currentThread = GetCurrentThreadId();
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var targetThread = GetWindowThreadProcessId(_window, out var process);
        if (process != _process || targetThread == 0) return;
        var foregroundAttached = foregroundThread != 0 && foregroundThread != currentThread &&
            AttachThreadInput(currentThread, foregroundThread, true);
        var targetAttached = false;
        try
        {
            // Activation and focus belong to the target's input queue too. Connecting
            // only to the window being left does not activate the original queue.
            targetAttached = targetThread != currentThread && targetThread != foregroundThread &&
                AttachThreadInput(currentThread, targetThread, true);
            AppDiagnostics.Write($"field.restore.queues foreground={foregroundAttached} target={targetAttached}");
            if (expired()) return;
            var requested = SetForegroundWindow(_window);
            if (!expired() && (targetAttached || targetThread == currentThread || (targetThread == foregroundThread && foregroundAttached)))
                SetActiveWindow(_window);
            AppDiagnostics.Write($"field.restore.activation accepted={requested} current={GetForegroundWindow() == _window}");
        }
        finally
        {
            if (targetAttached) AttachThreadInput(currentThread, targetThread, false);
            if (foregroundAttached) AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    private void DiagnoseFocus()
    {
        var foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out var process);
        AppDiagnostics.Write($"field.restore.observed targetWindow={foreground == _window} targetProcess={process == _process} ownProcess={process == Environment.ProcessId}");
        var focused = _automation.GetFocusedElement();
        try { AppDiagnostics.Write($"field.restore.observed originalElement={_automation.CompareElements(_element, focused) != 0}"); }
        finally { Release(focused); }
    }

    [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr window);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AttachThreadInput(uint source, uint target, bool attach);

    public void Dispose() { Release(_element); _element = null; Release(_automation); }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
}
