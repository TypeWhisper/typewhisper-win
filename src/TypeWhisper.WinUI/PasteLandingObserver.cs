using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using TypeWhisper.WinUI.Platform;

namespace TypeWhisper.WinUI;

// Confirms that a paste reached the focused field before Enter submits it, as the macOS app verifies the paste through
// accessibility. It records the focused field and the text before its caret right before Ctrl+V, then waits until the
// same field shows changed text there. The text is only compared and never stored or logged.
internal sealed class PasteLandingObserver : IDisposable
{
    private const int TextPatternId = 10014;
    private const int ContextLength = 64;
    // UIA timeouts do not bound every text-range call, so each read has its own limit.
    private static readonly TimeSpan ReadLimit = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(30);
    private static readonly TimeSpan LandingLimit = TimeSpan.FromMilliseconds(1500);
    // A field without readable text cannot confirm the paste; Enter then follows after this delay, as before.
    private static readonly TimeSpan UnreadableDelay = TimeSpan.FromMilliseconds(100);
    private readonly IUIAutomation2 _automation;
    private readonly IUIAutomationElement _field;
    private readonly string? _before;
    private Task? _pending;
    private int _disposed;

    private PasteLandingObserver(IUIAutomation2 automation, IUIAutomationElement field, string? before)
    {
        _automation = automation; _field = field; _before = before;
    }

    /// <summary>Records the focused field of the target window, or returns null when it cannot be identified.</summary>
    internal static async Task<PasteLandingObserver?> CaptureAsync(IntPtr target)
    {
        var capture = Task.Run(() => Capture(target));
        if (await Task.WhenAny(capture, Task.Delay(ReadLimit)) == capture) return await capture;
        AppDiagnostics.Write("submit.landing.capture-timeout");
        _ = capture.ContinueWith(done => done.Result?.Dispose(), TaskScheduler.Default);
        return null;
    }

    /// <summary>Returns true once the paste appeared in the recorded field; false when focus moved or nothing appeared.</summary>
    internal async Task<bool> WaitAsync()
    {
        if (_before is null)
        {
            await Task.Delay(UnreadableDelay);
            var state = await ReadAsync();
            AppDiagnostics.Write($"submit.landing.unreadable decision={(state.SameField ? "same-field" : "field-changed")}");
            return state.SameField;
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var state = await ReadAsync();
            if (state.Known && !state.SameField) { AppDiagnostics.Write("submit.landing.field-changed"); return false; }
            if (state.Text is { } text && text != _before) { AppDiagnostics.Write("submit.landing.confirmed"); return true; }
            if (clock.Elapsed >= LandingLimit) { AppDiagnostics.Write("submit.landing.timeout"); return false; }
            await Task.Delay(PollInterval);
        }
    }

    // A read past its limit still holds a worker; the next poll waits for it instead of starting another.
    private async Task<(bool Known, bool SameField, string? Text)> ReadAsync()
    {
        if (_pending is { IsCompleted: false } stalled && await Task.WhenAny(stalled, Task.Delay(ReadLimit)) != stalled) return default;
        var read = Task.Run(Read);
        _pending = read;
        return await Task.WhenAny(read, Task.Delay(ReadLimit)) == read ? await read : default;
    }

    private (bool Known, bool SameField, string? Text) Read()
    {
        if (Volatile.Read(ref _disposed) != 0) return default;
        IUIAutomationElement? focused = null;
        try
        {
            focused = _automation.GetFocusedElement();
            if (focused is null || _automation.CompareElements(_field, focused) == 0) return (true, false, null);
            return (true, true, BeforeCaret(_field));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return default; }
        finally { Release(focused); }
    }

    private static PasteLandingObserver? Capture(IntPtr target)
    {
        IUIAutomation2? automation = null;
        IUIAutomationElement? element = null;
        try
        {
            if (NativeMethods.GetForegroundWindow() != target) return null;
            automation = (IUIAutomation2)new CUIAutomation8();
            automation.ConnectionTimeout = 200; automation.TransactionTimeout = 200;
            element = automation.GetFocusedElement();
            if (element is null || element.CurrentIsPassword != 0 || !BelongsTo(automation, element, target)) return null;
            var observer = new PasteLandingObserver(automation, element, BeforeCaret(element));
            automation = null; element = null;
            return observer;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppDiagnostics.Write("submit.landing.capture-failed", ex);
            return null;
        }
        finally { Release(element); Release(automation); }
    }

    // The text right before the single caret or selection, or null when the field exposes none.
    private static string? BeforeCaret(IUIAutomationElement element)
    {
        object? pattern = null;
        IUIAutomationTextRangeArray? selections = null;
        IUIAutomationTextRange? selection = null, before = null;
        try
        {
            pattern = element.GetCurrentPattern(TextPatternId);
            if (pattern is not IUIAutomationTextPattern text) return null;
            selections = text.GetSelection();
            if (selections is null || selections.Length != 1) return null;
            selection = selections.GetElement(0);
            before = selection.Clone();
            before.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, selection,
                TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);
            before.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start, TextUnit.TextUnit_Character, -ContextLength);
            var value = before.GetText(-1) ?? "";
            return value.Length > ContextLength ? value[^ContextLength..] : value;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
        finally { Release(before); Release(selection); Release(selections); Release(pattern); }
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

    // A read still running keeps the COM objects until it ends.
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        void Free() { Release(_field); Release(_automation); }
        if (_pending is { IsCompleted: false } pending) _ = pending.ContinueWith(_ => Free(), TaskScheduler.Default);
        else Free();
    }

    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
}
