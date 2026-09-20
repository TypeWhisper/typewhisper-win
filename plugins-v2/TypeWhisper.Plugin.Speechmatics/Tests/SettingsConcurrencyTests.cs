using TypeWhisper.Plugin.Speechmatics;

public sealed partial class ProviderTests
{
    [Fact]
    public async Task ContendedKeyAndModelSavesDoNotCaptureUiContext()
    {
        using var plugin = new SpeechmaticsPlugin(); var host = new Host();
        await plugin.ActivateAsync(host); await Configure(plugin);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.StoreSecretDelay = release.Task;
        var context = new RecordingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        Task keySave, modelSave;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            keySave = plugin.SetApiKeyAsync("replacement-key");
            modelSave = plugin.SaveTextSettingAsync("model", "standard", default);
            Assert.False(keySave.IsCompleted); Assert.False(modelSave.IsCompleted);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); release.TrySetResult(); }
        await Task.WhenAll(keySave, modelSave).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, context.Posts);
        Assert.Equal("standard", plugin.SelectedModelId);
    }

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref Posts);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }

}
