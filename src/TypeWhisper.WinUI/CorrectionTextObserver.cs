using System.Diagnostics;
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace TypeWhisper.WinUI;

// Only the captured, focused editor is read. Never scan descendants or use clipboard/selection fallbacks.
internal sealed class CorrectionTextObserver : ITargetAppTextObserver, IDisposable
{
    private readonly IUIAutomation2 _automation = (IUIAutomation2)new CUIAutomation8();
    private IUIAutomationElement? _element;
    private int _process;
    internal CorrectionTextObserver() { _automation.ConnectionTimeout = 200; _automation.TransactionTimeout = 200; }
    public TargetAppTextObservation? Capture(IntPtr targetHwnd, int maxTextLength)
    {
        if (GetForegroundWindow() != targetHwnd) return null;
        GetWindowThreadProcessId(targetHwnd, out var process);
        if (process == Environment.ProcessId) return null;
        using var app = Process.GetProcessById((int)process);
        if (new[] { "cmd", "powershell", "pwsh", "WindowsTerminal", "conhost", "mintty" }.Contains(app.ProcessName, StringComparer.OrdinalIgnoreCase)) return null;
        _process = (int)process;
        _element = _automation.GetFocusedElement();
        if (_element.CurrentHasKeyboardFocus == 0 || !BelongsToTarget(_element, targetHwnd)) return null;
        return Read(_element, targetHwnd, maxTextLength);
    }
    public TargetAppTextObservation? Recapture(TargetAppTextObservation baseline) =>
        _element is null ? null : Read(_element, baseline.WindowHandle, baseline.MaxValueLength);
    public TargetAppTextElementMatch GetFocusedElementMatch(TargetAppTextObservation baseline)
    {
        if (GetForegroundWindow() != baseline.WindowHandle) return TargetAppTextElementMatch.DifferentWindow;
        var focused = _automation.GetFocusedElement();
        try { return _element is not null && _automation.CompareElements(_element, focused) != 0 ? TargetAppTextElementMatch.Same : TargetAppTextElementMatch.Different; }
        finally { Release(focused); }
    }
    private TargetAppTextObservation? Read(IUIAutomationElement element, IntPtr target, int limit)
    {
        if (!BelongsToTarget(element, target) || element.CurrentIsPassword != 0 || element.CurrentIsEnabled == 0 ||
            element.CurrentControlType is not (50004 or 50030) || element.CurrentIsOffscreen != 0) return null;
        var shortcut = element.CurrentAcceleratorKey ?? "";
        if (shortcut.Contains("Ctrl+L", StringComparison.OrdinalIgnoreCase) || shortcut.Contains("Alt+D", StringComparison.OrdinalIgnoreCase) ||
            element.CurrentAutomationId == "view_1012") return null;
        object? pattern = null; IUIAutomationTextRange? range = null;
        try
        {
            string? text = null;
            pattern = element.GetCurrentPattern(10014);
            if (pattern is IUIAutomationTextPattern textPattern)
            { range = textPattern.DocumentRange; text = range.GetText(limit + 1); }
            else
            {
                Release(pattern); pattern = element.GetCurrentPattern(10002);
                if (pattern is IUIAutomationValuePattern value && value.CurrentIsReadOnly == 0) text = value.CurrentValue;
            }
            if (text is null || text.Length > limit) return null;
            return new(string.Join(".", element.GetRuntimeId().Cast<int>()), text, target, limit);
        }
        finally { Release(range); Release(pattern); }
    }
    private bool BelongsToTarget(IUIAutomationElement element, IntPtr target)
    {
        if (element.CurrentProcessId != _process) return false;
        var walker = _automation.RawViewWalker;
        IUIAutomationElement? parent = null;
        try
        {
            var current = element;
            for (var depth = 0; depth < 32; depth++)
            {
                if ((IntPtr)current.CurrentNativeWindowHandle == target) return true;
                var next = walker.GetParentElement(current); Release(parent); parent = next;
                if (next is null) return false;
                current = next;
            }
            return false;
        }
        finally { Release(parent); Release(walker); }
    }
    public void Dispose() { Release(_element); Release(_automation); }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
}
