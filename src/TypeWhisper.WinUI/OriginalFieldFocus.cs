using System.Diagnostics;

namespace TypeWhisper.WinUI;

internal static class OriginalFieldFocus
{
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
        activate();
        if (!isValid()) return false;
        // React to unconfirmed activation, not a fixed delay before requesting focus.
        if (!isCurrent()) activateAttached();
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
