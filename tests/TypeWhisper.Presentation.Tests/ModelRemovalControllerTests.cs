using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class ModelRemovalControllerTests
{
    [Fact]
    public async Task RemovalUsesSharedAdmissionAndPublishesActualOperationKindWithoutProgress()
    {
        var controller = new ModelDownloadController();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var removal = controller.RunRemovalAsync(_ => release.Task);
        Assert.True(controller.State.IsBusy); Assert.True(controller.State.IsRemoval);
        Assert.Equal("Removing model…", controller.State.Message); Assert.Null(controller.State.Progress);
        Assert.Throws<InvalidOperationException>(() => { _ = controller.RunAsync((_, _) => Task.CompletedTask); });
        Assert.Throws<InvalidOperationException>(() => { _ = controller.RunRemovalAsync(_ => Task.CompletedTask); });
        release.SetResult(); await removal;
        Assert.True(controller.State.Succeeded); Assert.Equal("Model files removed.", controller.State.Message);
        await controller.RunAsync((progress, _) => { progress.Report(0.5); return Task.CompletedTask; });
        Assert.False(controller.State.IsRemoval); Assert.Equal(0.5, controller.State.Progress);
    }

    [Fact]
    public async Task ActiveDownloadBlocksRemovalWithoutInvokingIt()
    {
        var controller = new ModelDownloadController();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var download = controller.RunAsync((_, _) => release.Task);
        var called = false;
        Assert.Throws<InvalidOperationException>(() => { _ = controller.RunRemovalAsync(_ => { called = true; return Task.CompletedTask; }); });
        Assert.False(called); release.SetResult(); await download;
    }

    [Fact]
    public async Task CancelDrainsLateRemovalAndDoesNotPromiseThatFilesAreUnchanged()
    {
        var controller = new ModelDownloadController();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var removal = controller.RunRemovalAsync(_ => release.Task);
        var drain = controller.CancelAndDrainAsync();
        Assert.False(drain.IsCompleted); Assert.True(controller.State.IsBusy);
        Assert.Equal("Canceling model removal…", controller.State.Message);
        release.SetResult(); await drain; await removal;
        Assert.False(controller.State.Succeeded); Assert.True(controller.State.IsRemoval);
        Assert.StartsWith("Removal canceled.", controller.State.Message);
        Assert.Contains("some files may have been removed", controller.State.Message);
        await controller.RunRemovalAsync(_ => Task.CompletedTask);
        Assert.True(controller.State.Succeeded);
    }

    [Fact]
    public async Task CancelFromFinishingNotificationRejectsRemovalSuccess()
    {
        var controller = new ModelDownloadController();
        controller.Changed += () =>
        {
            if (controller.State.Message == "Finishing model removal…") _ = controller.CancelAndDrainAsync();
        };
        await controller.RunRemovalAsync(_ => Task.CompletedTask);
        Assert.False(controller.State.Succeeded);
        Assert.StartsWith("Removal canceled.", controller.State.Message);
    }

    [Fact]
    public async Task ShutdownWaitsForActualRemovalAndCancellationCallbacksAndRejectsNewOperations()
    {
        var controller = new ModelDownloadController();
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        var removal = controller.RunRemovalAsync(token =>
        {
            registration = token.Register(() => { callbackStarted.TrySetResult(); releaseCallback.Task.GetAwaiter().GetResult(); });
            return releaseOperation.Task;
        });
        try
        {
            var shutdown = controller.ShutdownAsync();
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            releaseOperation.SetResult();
            Assert.False(shutdown.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => { _ = controller.RunRemovalAsync(_ => Task.CompletedTask); });
            Assert.Throws<InvalidOperationException>(() => { _ = controller.RunAsync((_, _) => Task.CompletedTask); });
            releaseCallback.SetResult(); await shutdown; await removal;
            Assert.True(controller.State.IsClosing); Assert.False(controller.State.Succeeded);
        }
        finally { releaseOperation.TrySetResult(); releaseCallback.TrySetResult(); await removal; registration.Dispose(); }
    }

    [Fact]
    public async Task FailureDoesNotLeakPrivatePluginPathsAndObserverCannotPreventDrain()
    {
        var controller = new ModelDownloadController();
        controller.Changed += () => throw new InvalidOperationException("Observer failed");
        await controller.RunRemovalAsync(_ => throw new IOException("private path"));
        Assert.False(controller.State.Succeeded); Assert.False(controller.State.IsBusy);
        Assert.DoesNotContain("private path", controller.State.Message!);
        Assert.Contains("some files may have been removed", controller.State.Message);
    }
}
