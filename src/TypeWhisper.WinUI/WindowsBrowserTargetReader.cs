using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using TypeWhisper.Presentation;

namespace TypeWhisper.WinUI;

internal static class WindowsBrowserTargetReader
{
    // Chromium defines VIEW_ID_TOOLBAR=1000 and VIEW_ID_OMNIBOX=1012 and exposes
    // view_<id> as AutomationId; OmniboxViewViews publishes Ctrl+L explicitly.
    // https://github.com/chromium/chromium/blob/main/chrome/browser/ui/view_ids.h
    // https://github.com/chromium/chromium/blob/main/chrome/browser/ui/views/omnibox/omnibox_view_views.cc
    // Firefox's browser chrome uses nav-bar/urlbar-input. Never traverse a Document,
    // even when page-authored controls imitate these identities.
    private const int Edit = 50004, Toolbar = 50021;
    private const int ValuePattern = 10002;
    private static readonly BrowserTargetCapture Capture = new(Read, TimeSpan.FromMilliseconds(350));
    internal static Task<string?> CaptureAsync(nint window, int processId, string processName, CancellationToken ct) =>
        Capture.CaptureAsync(new(window, processId, processName), ct);

    private static string? Read(BrowserCaptureTarget target, CancellationToken ct)
    {
        if (!StillOriginalTarget(target)) return null;
        IUIAutomation2? automation = null;
        IUIAutomationElement? root = null;
        IUIAutomationTreeWalker? walker = null;
        IUIAutomationElement? candidate = null;
        string? capturedValue = null;
        string? capturedToolbar = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            automation = (IUIAutomation2)new CUIAutomation8();
            automation.ConnectionTimeout = 200;
            automation.TransactionTimeout = 200;
            root = automation.ElementFromHandle(target.WindowHandle);
            if (root.CurrentProcessId != target.ProcessId) return null;
            walker = automation.RawViewWalker;
            var remaining = 128;
            var incomplete = false;
            var values = new List<string>();
            Visit(root, "", 0);
            ct.ThrowIfCancellationRequested();
            if (incomplete || values.Count != 1 || candidate is null || !StillOriginalTarget(target)) return null;
            if (!BelongsToOriginalRoot(candidate) || candidate.CurrentProcessId != target.ProcessId ||
                candidate.CurrentControlType != Edit ||
                !BrowserWorkflowContext.IsAddressBar(target.ProcessName, candidate.CurrentAutomationId ?? "", capturedToolbar ?? "",
                    candidate.CurrentAcceleratorKey ?? "", candidate.CurrentHasKeyboardFocus != 0,
                    candidate.CurrentIsPassword != 0, candidate.CurrentIsOffscreen != 0)) return null;
            object? finalPattern = null;
            try
            {
                finalPattern = candidate.GetCurrentPattern(ValuePattern);
                if (finalPattern is not IUIAutomationValuePattern finalValue || finalValue.CurrentValue != capturedValue ||
                    candidate.CurrentHasKeyboardFocus != 0 || !StillOriginalTarget(target)) return null;
                ct.ThrowIfCancellationRequested();
                // This is a start-context snapshot, not a tab or text-field lock. A later tab
                // change before audio starts cannot be prevented by a read-only UIA probe.
                return values[0];
            }
            finally { Release(finalPattern); }

            bool BelongsToOriginalRoot(IUIAutomationElement element)
            {
                var verifiedToolbar = false;
                var parent = walker.GetParentElement(element);
                for (var depth = 0; parent is not null; depth++)
                {
                    IUIAutomationElement? next = null;
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        if (depth > 10 || parent.CurrentProcessId != target.ProcessId ||
                            !BrowserWorkflowContext.CanInspectSubtree(parent.CurrentControlType, parent.CurrentAutomationId ?? "")) return false;
                        if (parent.CurrentControlType == Toolbar && !verifiedToolbar)
                        {
                            if (parent.CurrentAutomationId != capturedToolbar) return false;
                            verifiedToolbar = true;
                        }
                        if (automation.CompareElements(parent, root) != 0) return verifiedToolbar;
                        next = walker.GetParentElement(parent);
                    }
                    finally { Release(parent); }
                    parent = next;
                }
                return false;
            }

            void Visit(IUIAutomationElement element, string toolbarId, int depth)
            {
                ct.ThrowIfCancellationRequested();
                if (depth > 10 || --remaining < 0) { incomplete = true; return; }
                if (element.CurrentProcessId != target.ProcessId) return;
                var type = element.CurrentControlType;
                var id = element.CurrentAutomationId ?? "";
                if (!BrowserWorkflowContext.CanInspectSubtree(type, id)) return;
                if (type == Toolbar) toolbarId = id;
                if (type == Edit)
                {
                    if (!BrowserWorkflowContext.IsAddressBar(target.ProcessName, id, toolbarId,
                        element.CurrentAcceleratorKey ?? "", element.CurrentHasKeyboardFocus != 0,
                        element.CurrentIsPassword != 0, element.CurrentIsOffscreen != 0)) return;
                    object? pattern = null;
                    try
                    {
                        pattern = element.GetCurrentPattern(ValuePattern);
                        if (pattern is IUIAutomationValuePattern value)
                        {
                            var first = value.CurrentValue;
                            ct.ThrowIfCancellationRequested();
                            // Do not accept a value while navigation/editing changes the address bar.
                            if (first == value.CurrentValue && element.CurrentHasKeyboardFocus == 0 &&
                                BrowserWorkflowContext.NormalizeHost(first) is { } host)
                            {
                                values.Add(host);
                                if (candidate is null)
                                {
                                    // Transfer this traversal reference to the outer finally;
                                    // its child loop must not release the same acquisition.
                                    candidate = element;
                                    capturedValue = first;
                                    capturedToolbar = toolbarId;
                                }
                            }
                        }
                    }
                    finally { Release(pattern); }
                    return;
                }
                var child = walker.GetFirstChildElement(element);
                while (child is not null)
                {
                    IUIAutomationElement? next = null;
                    try
                    {
                        Visit(child, toolbarId, depth + 1);
                        ct.ThrowIfCancellationRequested();
                        if (remaining > 0) next = walker.GetNextSiblingElement(child);
                        else incomplete = true;
                    }
                    finally { if (!ReferenceEquals(child, candidate)) Release(child); }
                    child = next;
                }
            }
        }
        finally { if (!ReferenceEquals(candidate, root)) Release(candidate); Release(walker); Release(root); Release(automation); }
    }

    private static void Release(object? value)
    { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    private static bool StillOriginalTarget(BrowserCaptureTarget target)
    {
        if (GetForegroundWindow() != target.WindowHandle) return false;
        GetWindowThreadProcessId(target.WindowHandle, out var processId);
        return processId == target.ProcessId;
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
