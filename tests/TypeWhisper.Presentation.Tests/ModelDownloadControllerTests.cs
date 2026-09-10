using TypeWhisper.Presentation;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class ModelDownloadControllerTests
{
    [Fact]
    public async Task CancelAfterProviderSuccessBeforePublicationWinsFinalTransition()
    {
        var controller = new ModelDownloadController();
        var invoked = false;
        var canceled = false;
        controller.Changed += () =>
        {
            if (!canceled && controller.State.Message == "Finishing model download…")
            {
                canceled = true;
                _ = controller.CancelAndDrainAsync();
            }
        };
        await controller.RunAsync((_, _) => { invoked = true; return Task.CompletedTask; });
        Assert.True(invoked); Assert.True(canceled);
        Assert.False(controller.State.Succeeded);
        Assert.StartsWith("Download canceled.", controller.State.Message);
    }

    [Fact]
    public async Task RepeatedCancelAndShutdownRetainFirstCallbackDrain()
    {
        var controller = new ModelDownloadController();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        controller.Changed += () =>
        {
            if (controller.State.IsBusy && releaseProvider.Task.IsCompleted) finishing.TrySetResult();
        };
        var operation = controller.RunAsync(async (_, ct) =>
        {
            registration = ct.Register(() => { callbackEntered.SetResult(); releaseCallback.Task.GetAwaiter().GetResult(); });
            await releaseProvider.Task;
        });
        try
        {
            var first = controller.CancelAndDrainAsync();
            await callbackEntered.Task;
            var second = controller.CancelAndDrainAsync();
            var shutdown = controller.ShutdownAsync();
            releaseProvider.SetResult();
            await finishing.Task;
            Assert.False(operation.IsCompleted); Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted); Assert.False(shutdown.IsCompleted);
            releaseCallback.SetResult();
            await shutdown;
            Assert.False(controller.State.Succeeded); Assert.True(controller.State.IsClosing);
        }
        finally
        {
            releaseProvider.TrySetResult(); releaseCallback.TrySetResult();
            await operation; registration.Dispose();
        }
    }

    [Fact]
    public async Task CancelDrainsIgnoringProviderRejectsLateSuccessAndIgnoresItsProgress()
    {
        var controller = new ModelDownloadController();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<double>? progress = null;
        var operation = controller.RunAsync(async (report, _) =>
        {
            progress = report; report.Report(0.3); entered.SetResult(); await release.Task;
            report.Report(1);
        });
        await entered.Task;
        var snapshot = controller.State;
        var canceled = controller.CancelAndDrainAsync();
        Assert.False(canceled.IsCompleted); Assert.True(controller.State.IsBusy);
        progress!.Report(0.9);
        Assert.Equal(0.3, controller.State.Progress);
        release.SetResult(); await canceled; await operation;
        Assert.False(controller.State.Succeeded); Assert.False(controller.State.IsBusy);
        Assert.True(snapshot.IsBusy); Assert.Equal(0.3, snapshot.Progress);
        progress.Report(0.8); Assert.Equal(0.3, controller.State.Progress);
        await controller.RunAsync((_, _) => Task.CompletedTask);
        Assert.True(controller.State.Succeeded);
        progress.Report(0.7); Assert.Null(controller.State.Progress);
    }

    [Fact]
    public async Task ShutdownRefusesAdmissionAndWaitsForCancellationCallbacks()
    {
        var controller = new ModelDownloadController();
        var cancellationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = controller.RunAsync(async (_, ct) =>
        {
            using var registration = ct.Register(() => { cancellationEntered.SetResult(); release.Task.GetAwaiter().GetResult(); });
            await release.Task;
        });
        var shutdown = controller.ShutdownAsync();
        await cancellationEntered.Task;
        Assert.False(shutdown.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => { _ = controller.RunAsync((_, _) => Task.CompletedTask); });
        release.SetResult(); await shutdown; await operation;
        Assert.True(controller.State.IsClosing); Assert.False(controller.State.Succeeded);
    }

    [Fact]
    public async Task OnlyFiniteProviderProgressIsPublishedAndObserverFailureCannotPreventDrain()
    {
        var controller = new ModelDownloadController();
        controller.Changed += () => throw new InvalidOperationException("listener");
        await controller.RunAsync((progress, _) =>
        {
            progress.Report(0.4); progress.Report(double.NaN); progress.Report(2); progress.Report(-1);
            Assert.Equal(0.4, controller.State.Progress);
            throw new IOException("secret location");
        });
        Assert.False(controller.State.IsBusy); Assert.False(controller.State.Succeeded);
        Assert.DoesNotContain("secret location", controller.State.Message!);
        await controller.ShutdownAsync();
    }

    [Fact]
    public async Task ChangedCanCancelBeforeProviderInvocationAndDoesNotQueueAnotherDownload()
    {
        var controller = new ModelDownloadController();
        var called = false;
        controller.Changed += () => { if (controller.State.IsBusy && controller.State.Message == "Downloading model…") _ = controller.CancelAndDrainAsync(); };
        await controller.RunAsync((_, _) => { called = true; return Task.CompletedTask; });
        Assert.False(called); Assert.False(controller.State.Succeeded);
    }
}
