using TypeWhisper.WinUI;
using Xunit;

public class ClipboardPasteOperationTests
{
    private sealed class Fake : IClipboardPastePlatform, IDisposable
    {
        public bool CanPaste { get; set; } = true;
        public bool ClipboardIsOwned { get; set; } = true;
        public bool FailCapture, LoseFocusAfterCapture, ThrowOnSend;
        public uint Sent = 4;
        public List<string> Calls = [];
        public string? Text;
        public Task<IDisposable> BeginAsync(string text)
        {
            Calls.Add("capture"); Text = text;
            if (FailCapture) throw new InvalidOperationException("Unmaterializable bitmap");
            if (LoseFocusAfterCapture) CanPaste = false;
            return Task.FromResult<IDisposable>(this);
        }
        public uint SendPaste() { Calls.Add("paste"); if (ThrowOnSend) throw new InvalidOperationException(); return Sent; }
        public TaskCompletionSource? PendingWait;
        public Task WaitForPasteAsync() { Calls.Add("wait"); return PendingWait?.Task ?? Task.CompletedTask; }
        public Task RestoreAsync(IDisposable lease) { Calls.Add("restore"); return Task.CompletedTask; }
        public void Dispose() => Calls.Add("dispose");
    }
    [Fact]
    public async Task PastesWholeTextOnceThenRestores()
    {
        var platform = new Fake();
        var text = "Grüße 👋\n" + new string('x', 10000);
        var result = await ClipboardPasteOperation.RunAsync(platform, text);
        Assert.True(result.Inserted);
        await result.Restored;
        Assert.Equal(text, platform.Text);
        Assert.Equal(new[] { "capture", "paste", "wait", "restore", "dispose" }, platform.Calls);
    }
    [Fact]
    public async Task ReportsSentPasteBeforeDeferredRestore()
    {
        var consumed = new TaskCompletionSource();
        var platform = new Fake { PendingWait = consumed };
        var result = await ClipboardPasteOperation.RunAsync(platform, "text");
        Assert.True(result.Inserted);
        Assert.False(result.Restored.IsCompleted);
        Assert.Equal(new[] { "capture", "paste", "wait" }, platform.Calls);
        consumed.SetResult();
        await result.Restored;
        Assert.Equal(new[] { "capture", "paste", "wait", "restore", "dispose" }, platform.Calls);
    }
    [Fact]
    public async Task UnsafeSnapshotDoesNotPaste()
    {
        var platform = new Fake { FailCapture = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ClipboardPasteOperation.RunAsync(platform, "text"));
        Assert.Equal(new[] { "capture" }, platform.Calls);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangedFocusOrClipboardDoesNotPaste(bool focus)
    {
        var platform = new Fake { LoseFocusAfterCapture = focus, ClipboardIsOwned = focus };
        var result = await ClipboardPasteOperation.RunAsync(platform, "text");
        Assert.False(result.Inserted);
        Assert.True(result.Restored.IsCompleted);
        Assert.Equal(new[] { "capture", "restore", "dispose" }, platform.Calls);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task PartialInputDoesNotRetry(int sent)
    {
        var platform = new Fake { Sent = (uint)sent };
        Assert.False((await ClipboardPasteOperation.RunAsync(platform, "text")).Inserted);
        Assert.Equal(1, platform.Calls.Count(c => c == "paste"));
        Assert.Contains("restore", platform.Calls);
    }
    [Fact]
    public async Task SendExceptionStillRestores()
    {
        var platform = new Fake { ThrowOnSend = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ClipboardPasteOperation.RunAsync(platform, "text"));
        Assert.Contains("restore", platform.Calls);
    }
}
