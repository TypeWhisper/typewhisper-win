using TypeWhisper.WinUI;
using Xunit;

public sealed class LocalLivePreviewTests
{
    [Fact]
    public void DefaultIntervalIsOneAndAHalfSeconds()
        => Assert.Equal(TimeSpan.FromMilliseconds(1500), LocalLivePreview.DefaultInterval);

    [Fact]
    public async Task PublishesActualDecodedSnapshot()
    {
        using var preview = new LocalLivePreview(TimeSpan.FromMilliseconds(1));
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        preview.Start(() => new float[16000], _ => Task.FromResult("Real words"), text => received.TrySetResult(text), error => received.TrySetException(new Exception(error)));
        Assert.Equal("Real words", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await preview.StopAsync();
    }

    [Fact]
    public async Task StopDrainsNativeDecodeAndSuppressesStaleResult()
    {
        using var preview = new LocalLivePreview(TimeSpan.FromMilliseconds(1));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decode = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = new List<string>();
        preview.Start(() => new float[16000], _ => { entered.TrySetResult(); return decode.Task; }, results.Add, results.Add);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = preview.StopAsync();
        Assert.False(stopped.IsCompleted);
        Assert.Throws<InvalidOperationException>(() => preview.Start(() => null, _ => Task.FromResult(""), _ => { }, _ => { }));
        decode.SetResult("Stale words");
        await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(results);
    }

    [Fact]
    public async Task PreviewFailureIsReportedWithoutEscapingIntoDictation()
    {
        using var preview = new LocalLivePreview(TimeSpan.FromMilliseconds(1));
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        preview.Start(() => new float[16000], _ => throw new InvalidOperationException("decode failed"), _ => { }, error => failure.TrySetResult(error));
        Assert.Equal("decode failed", await failure.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await preview.StopAsync();
    }
}
