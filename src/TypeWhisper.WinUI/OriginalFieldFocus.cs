using System.Diagnostics;

namespace TypeWhisper.WinUI;

internal static class OriginalFieldFocus
{
    // Chromium/Electron contenteditable controls may expose Group or Custom, not Edit.
    // Keep the exact captured element; accept those roles only with writable text metadata.
    internal static bool IsEditableControl(int controlType, bool keyboardFocusable, Func<bool> writableTextPattern) =>
        controlType is 50004 or 50030 ||
        (controlType is 50025 or 50026 && keyboardFocusable && writableTextPattern());

    // Chromium/Electron build their accessibility tree on the first focus query and can
    // report the render host Pane until it exists (#513). Retry briefly while the target stays
    // in front; the microphone is already capturing. The time budget bounds how long a Stop
    // pressed meanwhile waits for startup, however slowly each provider query answers.
    internal static async Task<bool> CaptureAsync(Func<bool> capture, Func<bool> targetIsForeground,
        Func<CancellationToken, Task> wait, CancellationToken cancellation, int attempts = 20, Func<bool>? expired = null)
    {
        var clock = Stopwatch.StartNew();
        expired ??= () => clock.Elapsed >= TimeSpan.FromSeconds(1);
        for (var attempt = 1; ; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (capture()) return true;
            if (attempt >= attempts || expired() || !targetIsForeground()) return false;
            await wait(cancellation);
        }
    }

    internal static Func<bool> Deadline()
    {
        var clock = Stopwatch.StartNew();
        return () => clock.Elapsed >= TimeSpan.FromSeconds(5);
    }

    internal static async Task<bool> RestoreWindowAsync(Func<bool> isCurrent, Func<bool> isValid,
        Action activate, Action activateAttached, Func<CancellationToken, Task> wait, CancellationToken cancellation,
        Func<bool>? expired = null)
    {
        expired ??= Deadline();
        cancellation.ThrowIfCancellationRequested();
        if (!isValid()) return false;
        if (isCurrent()) return true;
        if (expired()) return false;
        activate();
        if (!isValid()) return false;
        // React to unconfirmed activation, not a fixed delay before requesting focus.
        if (!isCurrent())
        {
            if (expired()) return false;
            activateAttached();
        }
        return await WaitForStateAsync(isCurrent, isValid, wait, cancellation, expired);
    }

    // Focus requests can complete asynchronously. Readiness alone permits delivery;
    // elapsed time can only abort it, never make a paste eligible.
    internal static async Task<bool> RestoreAsync(Func<bool> isCurrent, Func<bool> canRestore,
        Action setFocus, Func<CancellationToken, Task> wait, CancellationToken cancellation,
        Func<bool>? expired = null)
    {
        expired ??= Deadline();
        cancellation.ThrowIfCancellationRequested();
        if (isCurrent()) return true;
        if (!canRestore()) return false;
        if (expired()) return false;
        setFocus();
        return await WaitForStateAsync(isCurrent, canRestore, wait, cancellation, expired);
    }

    private static async Task<bool> WaitForStateAsync(Func<bool> isCurrent, Func<bool> isValid,
        Func<CancellationToken, Task> wait, CancellationToken cancellation, Func<bool> expired)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!isValid()) return false;
            if (isCurrent()) return true;
            if (expired()) return false;
            // This is only the observation interval; it never determines success.
            await wait(cancellation);
        }
    }
}
