namespace TypeWhisper.WinUI;

internal static class OriginalFieldFocus
{
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
