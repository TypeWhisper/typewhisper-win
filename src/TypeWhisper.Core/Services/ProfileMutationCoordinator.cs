namespace TypeWhisper.Core.Services;

/// <summary>
/// Serializes profile collection mutations so multi-file restore operations cannot overwrite concurrent writes.
/// </summary>
internal static class ProfileMutationCoordinator
{
    private static readonly ReaderWriterLockSlim Gate = new(LockRecursionPolicy.SupportsRecursion);

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
        }
    }
}
