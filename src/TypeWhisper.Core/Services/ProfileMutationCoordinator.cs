namespace TypeWhisper.Core.Services;

/// <summary>
/// Serializes profile collection mutations so multi-file restore operations cannot overwrite concurrent writes.
/// </summary>
internal static class ProfileMutationCoordinator
{
    private static readonly ReaderWriterLockSlim Gate = new(LockRecursionPolicy.SupportsRecursion);
    [ThreadStatic] private static List<Action>? _notifications;

    // Nested backup/restore scopes publish only after the outermost mutation releases the gate.
    internal static void Notify(Action? handlers)
    {
        if (handlers is null) return;
        AfterMutation(() =>
        {
            foreach (Action handler in handlers.GetInvocationList())
            {
                try { handler(); }
                catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException)
                { System.Diagnostics.Trace.TraceError("Profile change subscriber failed: {0}", ex); }
            }
        });
    }

    internal static void Notify<T>(Action<T>? handlers, T value)
    {
        if (handlers is null) return;
        Notify(() =>
        {
            foreach (Action<T> handler in handlers.GetInvocationList())
            {
                try { handler(value); }
                catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException)
                { System.Diagnostics.Trace.TraceError("Profile change subscriber failed: {0}", ex); }
            }
        });
    }

    private static void AfterMutation(Action notification)
    {
        if (Gate.IsWriteLockHeld) (_notifications ??= []).Add(notification);
        else notification();
    }

    /// <summary>Enters the process-wide profile mutation scope.</summary>
    public static IDisposable Enter()
    {
        Gate.EnterWriteLock();
        return new Releaser();
    }

    private sealed class Releaser : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            Gate.ExitWriteLock();
            if (!Gate.IsWriteLockHeld && _notifications is { } notifications)
            {
                _notifications = null;
                foreach (var notification in notifications) notification();
            }
        }
    }
}
