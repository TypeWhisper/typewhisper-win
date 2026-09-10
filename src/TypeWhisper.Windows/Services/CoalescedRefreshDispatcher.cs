namespace TypeWhisper.Windows.Services;

internal sealed class CoalescedRefreshDispatcher
{
    private readonly Action _refresh;
    private readonly Action<Action> _queue;
    private readonly Action<Exception> _reportFailure;
    private int _requestedGeneration;
    private int _workerRunning;

    internal CoalescedRefreshDispatcher(
        Action refresh,
        Action<Action>? queue = null,
        Action<Exception>? reportFailure = null)
    {
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        _queue = queue ?? QueueOnThreadPool;
        _reportFailure = reportFailure ?? ReportFailure;
    }

    internal void Request()
    {
        Interlocked.Increment(ref _requestedGeneration);
        if (Interlocked.CompareExchange(ref _workerRunning, 1, 0) == 0)
            _queue(Run);
    }

    private void Run()
    {
        var handledGeneration = 0;
        try
        {
            do
            {
                handledGeneration = Volatile.Read(ref _requestedGeneration);
                try
                {
                    _refresh();
                }
                catch (Exception ex) when (NonFatalExceptionFilter.IsNonFatal(ex))
                {
                    _reportFailure(ex);
                }
            }
            while (handledGeneration != Volatile.Read(ref _requestedGeneration));
        }
        finally
        {
            Interlocked.Exchange(ref _workerRunning, 0);

            // A request can arrive after the last generation check but before the
            // worker flag is cleared. Claim another run unless that request already
            // queued a replacement worker.
            if (handledGeneration != Volatile.Read(ref _requestedGeneration) &&
                Interlocked.CompareExchange(ref _workerRunning, 1, 0) == 0)
            {
                _queue(Run);
            }
        }
    }

    private static void QueueOnThreadPool(Action action) =>
        ThreadPool.QueueUserWorkItem(static state => ((Action)state!).Invoke(), action);

    private static void ReportFailure(Exception exception) =>
        System.Diagnostics.Debug.WriteLine(
            $"[Audio] Display/power refresh failed: {exception.Message}");
}
