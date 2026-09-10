using TypeWhisper.Windows.Services;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class CoalescedRefreshDispatcherTests
{
    [Fact]
    public void Request_DuringRefreshRunsTheNewestGeneration()
    {
        Action? queuedWorker = null;
        CoalescedRefreshDispatcher? dispatcher = null;
        var refreshCount = 0;
        dispatcher = new CoalescedRefreshDispatcher(
            () =>
            {
                if (Interlocked.Increment(ref refreshCount) == 1)
                    dispatcher!.Request();
            },
            worker =>
            {
                Assert.Null(queuedWorker);
                queuedWorker = worker;
            });

        dispatcher.Request();
        Assert.NotNull(queuedWorker);

        queuedWorker();

        Assert.Equal(2, refreshCount);
    }

    [Fact]
    public void RefreshFailure_IsReportedAndDoesNotBlockLaterRequests()
    {
        var queuedWorkers = new Queue<Action>();
        var refreshCount = 0;
        Exception? reportedFailure = null;
        var dispatcher = new CoalescedRefreshDispatcher(
            () =>
            {
                if (Interlocked.Increment(ref refreshCount) == 1)
                    throw new InvalidOperationException("capture disposal failed");
            },
            queuedWorkers.Enqueue,
            exception => reportedFailure = exception);

        dispatcher.Request();
        Assert.Single(queuedWorkers);
        queuedWorkers.Dequeue()();

        dispatcher.Request();
        Assert.Single(queuedWorkers);
        queuedWorkers.Dequeue()();

        Assert.Equal(2, refreshCount);
        Assert.IsType<InvalidOperationException>(reportedFailure);
    }
}
