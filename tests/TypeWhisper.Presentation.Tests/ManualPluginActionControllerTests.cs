using TypeWhisper.Presentation;
using Xunit;

public sealed class ManualPluginActionControllerTests
{
    [Fact]
    public async Task DoubleClickDoesNotQueueAndShutdownDrainsConfirmedSuccess()
    {
        var controller = new ManualPluginActionController();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var running = controller.ExecuteAsync("captured", async (text, ct) =>
        {
            Assert.Equal("captured", text); calls++; entered.SetResult(); await release.Task;
            return new(ManualPluginActionStatus.Succeeded, "Saved");
        });
        await entered.Task;
        var refused = await controller.ExecuteAsync("other", (_, _) => throw new Exception("Must not invoke"));
        Assert.Equal(ManualPluginActionStatus.Failed, refused.Status);
        var shutdown = controller.ShutdownAsync();
        Assert.False(shutdown.IsCompleted);
        release.SetResult(); await shutdown;
        Assert.Equal(ManualPluginActionStatus.Succeeded, (await running).Status);
        Assert.Equal(1, calls);
        Assert.False(controller.IsRunning);
        Assert.Equal(ManualPluginActionStatus.Failed, (await controller.ExecuteAsync("later", (_, _) => throw new Exception())).Status);
    }

    [Fact]
    public async Task InitialObserverCanCancelBeforeInvocationAndThrowWithoutHangingCompletion()
    {
        var controller = new ManualPluginActionController();
        var calls = 0;
        controller.Changed += () => { if (controller.IsRunning) controller.RequestCancel(); };
        controller.Changed += () => throw new InvalidOperationException("observer failure");
        var result = await controller.ExecuteAsync("text", (_, _) => { calls++; return Task.FromResult(new ManualPluginActionOutcome(ManualPluginActionStatus.Succeeded, "Saved")); });
        Assert.Equal(ManualPluginActionStatus.Canceled, result.Status);
        Assert.Equal(0, calls);
        await controller.ShutdownAsync();
    }

    [Fact]
    public async Task ThrowAfterInvocationIsUncertainAndOriginalInputIsNotRetried()
    {
        var controller = new ManualPluginActionController(); var calls = 0;
        var outcome = await controller.ExecuteAsync("review text", (_, _) => { calls++; throw new OperationCanceledException("private detail"); });
        Assert.Equal(ManualPluginActionStatus.CompletionUnknown, outcome.Status);
        Assert.DoesNotContain("private detail", outcome.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ShutdownAlsoDrainsCancellationCallbacks()
    {
        var controller = new ManualPluginActionController();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operationRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        var run = controller.ExecuteAsync("text", async (_, ct) =>
        {
            registration = ct.Register(() => { callbackEntered.SetResult(); callbackRelease.Task.GetAwaiter().GetResult(); });
            entered.SetResult(); await operationRelease.Task;
            return new(ManualPluginActionStatus.Succeeded, "Saved");
        });
        await entered.Task;
        var shutdown = controller.ShutdownAsync();
        await callbackEntered.Task;
        operationRelease.SetResult();
        Assert.False(shutdown.IsCompleted);
        callbackRelease.SetResult();
        await shutdown;
        registration.Dispose();
        Assert.Equal(ManualPluginActionStatus.Succeeded, (await run).Status);
    }
}
