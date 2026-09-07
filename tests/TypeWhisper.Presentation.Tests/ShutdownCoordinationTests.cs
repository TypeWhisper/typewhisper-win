using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class ShutdownCoordinationTests
{
    [Fact]
    public async Task CancelRequestsImmediatelyButShutdownWaitsForNativeBarrier()
    {
        using var operation = new OperationCancellationScope();
        var token = operation.Begin();
        var native = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdown = new AsyncShutdownCoordinator();
        var released = false;
        var pending = shutdown.Run(async () =>
        {
            operation.Close();
            await native.Task;
            released = true;
        });
        Assert.True(token.IsCancellationRequested);
        Assert.False(pending.IsCompleted);
        Assert.False(released);
        Assert.Throws<ObjectDisposedException>(() => operation.Begin());
        Assert.Same(pending, shutdown.Run(() => throw new Exception("Must run only once")));
        native.SetResult();
        await pending;
        Assert.True(released);
    }

    [Fact]
    public async Task ReentrantShutdownObservesAlreadyPublishedCompletionAndFailuresRemainObservable()
    {
        var shutdown = new AsyncShutdownCoordinator();
        Task? reentrant = null;
        var failure = new IOException("release failed");
        var pending = shutdown.Run(() =>
        {
            reentrant = shutdown.Run(() => throw new Exception("Must not enter twice"));
            throw failure;
        });
        Assert.Same(pending, reentrant);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => pending));
        Assert.Same(pending, shutdown.Run(() => Task.CompletedTask));
    }

    [Fact]
    public void CallerCancellationLinksToFileOperationAndNewOperationsDoNotReuseCanceledToken()
    {
        using var lifetime = new OperationCancellationScope();
        using var caller = new CancellationTokenSource();
        var previous = lifetime.Begin(caller.Token);
        caller.Cancel();
        Assert.True(previous.IsCancellationRequested);
        var next = lifetime.Begin();
        Assert.False(next.IsCancellationRequested);
        lifetime.Cancel();
        Assert.True(next.IsCancellationRequested);
    }
}
