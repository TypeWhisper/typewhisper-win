using TypeWhisper.WinUI;
using Xunit;

public sealed class SerializedBackgroundWorkTests
{
    [Fact]
    public async Task RunsProviderWorkWithoutACallerSynchronizationContextAndSerializesCleanup()
    {
        var work = new SerializedBackgroundWork();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = false;
        var provider = work.RunAsync(async () =>
        {
            Assert.Null(SynchronizationContext.Current);
            Assert.True(Thread.CurrentThread.IsThreadPoolThread);
            if (OperatingSystem.IsWindows()) Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
            entered.SetResult();
            await release.Task;
            Assert.False(cleaned);
            return true;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cleanup = work.RunAsync(() => { cleaned = true; return Task.FromResult(true); });
        Assert.False(cleanup.IsCompleted);
        release.SetResult();
        await Task.WhenAll(provider, cleanup).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cleaned);
    }

    [Fact]
    public async Task CancelingAQueuedProbeDoesNotRunItOrBlockTheNextOperation()
    {
        var work = new SerializedBackgroundWork();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = work.RunAsync(() => { entered.SetResult(); return release.Task; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var ran = false;
        var queued = work.RunAsync(() => { ran = true; return Task.FromResult(true); }, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        release.SetResult(true);
        await first;
        Assert.False(ran);
        Assert.True(await work.RunAsync(() => Task.FromResult(true)));
    }
}
