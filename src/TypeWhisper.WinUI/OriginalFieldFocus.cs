namespace TypeWhisper.WinUI;

internal static class OriginalFieldFocus
{
    internal static async Task<bool> RestoreWindowAsync(Func<bool> isCurrent, Func<bool> isValid,
        Action activate, Action activateAttached, Func<CancellationToken, Task> wait, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!isValid()) return false;
        if (isCurrent()) return true;
        activate();
        for (var attempt = 0; attempt < 16; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!isValid()) return false;
            if (isCurrent()) return true;
            // Only retry once, after giving the normal activation time to complete.
            if (attempt == 4) activateAttached();
            await wait(cancellation);
        }
        cancellation.ThrowIfCancellationRequested();
        return isValid() && isCurrent();
    }

    // Some providers complete SetFocus after returning. Keep verifying the captured
    // element; never replace it with whichever field happens to become focused.
    internal static async Task<bool> RestoreAsync(Func<bool> isCurrent, Func<bool> canRestore,
        Action setFocus, Func<CancellationToken, Task> wait, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (isCurrent()) return true;
        if (!canRestore()) return false;
        setFocus();
        for (var attempt = 0; attempt < 8; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!canRestore()) return false;
            if (isCurrent()) return true;
            await wait(cancellation);
        }
        cancellation.ThrowIfCancellationRequested();
        return canRestore() && isCurrent();
    }
}
