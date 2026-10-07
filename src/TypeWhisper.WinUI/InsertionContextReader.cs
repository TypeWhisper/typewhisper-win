using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using TypeWhisper.Presentation;
using TypeWhisper.WinUI.Platform;

namespace TypeWhisper.WinUI;

// Reads the text next to the selection of the focused field right before a paste, as the macOS app does through
// accessibility. A field without a UI Automation text pattern or a single selection yields no context, and the
// dictation is pasted unchanged. The text is only used to format the paste and never stored or logged.
internal static class InsertionContextReader
{
    private const int TextPatternId = 10014;
    // UIA timeouts do not bound every text-range call, so a slow provider must not hold up the paste.
    private static readonly TimeSpan ReadLimit = TimeSpan.FromMilliseconds(500);
    // Callers are serialized by the dictation gate, which the time limit releases before a stalled read ends.
    private static Task<DictationInsertionContext?>? _pending;

    internal static async Task<DictationInsertionContext?> ReadAsync(IntPtr target)
    {
        // A read past its time limit still holds a worker; never queue another one behind a stalled provider.
        if (_pending is { IsCompleted: false }) { AppDiagnostics.Write("insertion.context.skipped-pending"); return null; }
        var read = _pending = Task.Run(() => Read(target));
        // The result holds text from another app's field; do not keep it reachable once the read is over.
        _ = read.ContinueWith(done => Interlocked.CompareExchange(ref _pending, null, done), TaskScheduler.Default);
        if (await Task.WhenAny(read, Task.Delay(ReadLimit)) == read) return await read;
        AppDiagnostics.Write("insertion.context.timeout");
        return null;
    }

    private static DictationInsertionContext? Read(IntPtr target)
    {
        IUIAutomation2? automation = null;
        IUIAutomationElement? element = null;
        object? pattern = null;
        IUIAutomationTextRangeArray? selections = null;
        IUIAutomationTextRange? selection = null, before = null, after = null;
        try
        {
            if (NativeMethods.GetForegroundWindow() != target) return null;
            NativeMethods.GetWindowThreadProcessId(target, out var process);
            if (process == Environment.ProcessId) return null;
            automation = (IUIAutomation2)new CUIAutomation8();
            automation.ConnectionTimeout = 200; automation.TransactionTimeout = 200;
            element = automation.GetFocusedElement();
            // Focus can lag behind a foreground change; never read another window's field.
            if (element is null || element.CurrentIsPassword != 0 || !BelongsTo(automation, element, target)) return null;
            pattern = element.GetCurrentPattern(TextPatternId);
            if (pattern is not IUIAutomationTextPattern text) return null;
            selections = text.GetSelection();
            if (selections is null || selections.Length != 1) return null;
            selection = selections.GetElement(0);
            before = selection.Clone();
            before.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, selection,
                TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);
            before.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, TextUnit.TextUnit_Character,
                -DictationInsertionText.ContextLength);
            after = selection.Clone();
            after.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, selection,
                TextPatternRangeEndpoint.TextPatternRangeEndpoint_End);
            after.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, TextUnit.TextUnit_Character,
                DictationInsertionText.ContextLength);
            var beforeText = before.GetText(-1) ?? "";
            var afterText = after.GetText(-1) ?? "";
            // Providers may count characters differently; never look further than the formatter expects.
            if (beforeText.Length > DictationInsertionText.ContextLength) beforeText = beforeText[^DictationInsertionText.ContextLength..];
            if (afterText.Length > DictationInsertionText.ContextLength) afterText = afterText[..DictationInsertionText.ContextLength];
            // Focus may move to another control during the read. Its text says nothing about the field that
            // receives the paste, so the paste then goes ahead unformatted.
            var focused = automation.GetFocusedElement();
            try { if (focused is null || automation.CompareElements(element, focused) == 0) return null; }
            finally { Release(focused); }
            return new(beforeText, afterText);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppDiagnostics.Write("insertion.context.failed", ex);
            return null;
        }
        finally
        {
            Release(after); Release(before); Release(selection); Release(selections);
            Release(pattern); Release(element); Release(automation);
        }
    }

    private static bool BelongsTo(IUIAutomation2 automation, IUIAutomationElement element, IntPtr target)
    {
        var walker = automation.RawViewWalker;
        IUIAutomationElement? parent = null;
        try
        {
            var current = element;
            for (var depth = 0; depth < 64; depth++)
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

    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
}
