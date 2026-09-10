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
    private OriginalDictationField(IntPtr window, uint process)
    {
        _window = window; _process = process;
        // A cold provider may need to initialize its accessibility tree. The old
        // 200 ms capture budget could fail the first dictation but succeed later.
        _automation.ConnectionTimeout = 2000; _automation.TransactionTimeout = 2000;
    }

    internal static OriginalDictationField? Capture(IntPtr window, uint process)
    {
        OriginalDictationField? target = null;
        try
        {
            target = new(window, process);
            target._element = target._automation.GetFocusedElement();
            if (target.IsCurrent())
            {
                target._automation.ConnectionTimeout = 200;
                target._automation.TransactionTimeout = 200;
                return target;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        target?.Dispose(); return null;
    }

    private bool IsValid()
    {
        GetWindowThreadProcessId(_window, out var process);
        if (process != _process || _element is null || _element.CurrentProcessId != _process ||
            _element.CurrentIsEnabled == 0 || _element.CurrentIsPassword != 0 ||
            _element.CurrentControlType is not (50004 or 50030)) return false;
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

    internal bool IsCurrent()
    {
        try
        {
            if (GetForegroundWindow() != _window || !IsValid()) return false;
            var focused = _automation.GetFocusedElement();
            try { return _automation.CompareElements(_element, focused) != 0; }
            finally { Release(focused); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    internal async Task<bool> RestoreAsync(CancellationToken cancellation)
    {
        try
        {
            cancellation.ThrowIfCancellationRequested();
            // Avoid disturbing the caret when the user stayed in the original field.
            if (IsCurrent()) return true;
            if (!IsValid()) return false;
            if (IsIconic(_window)) ShowWindowAsync(_window, 9);
            SetForegroundWindow(_window);
            for (var attempt = 0; attempt < 8 && GetForegroundWindow() != _window; attempt++)
                await Task.Delay(25, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (GetForegroundWindow() != _window || !IsValid()) return false;
            return await OriginalFieldFocus.RestoreAsync(IsCurrent,
                () => GetForegroundWindow() == _window && IsValid(),
                () => _element!.SetFocus(), ct => Task.Delay(25, ct), cancellation);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not OperationCanceledException) { return false; }
    }

    public void Dispose() { Release(_element); _element = null; Release(_automation); }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
}
