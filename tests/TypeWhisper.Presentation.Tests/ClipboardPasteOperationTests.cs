using TypeWhisper.WinUI;
using Xunit;

public class ClipboardPasteOperationTests
{
    private sealed class Fake : IClipboardPastePlatform, IDisposable
    {
        public bool CanPaste { get; set; } = true;
        public Func<Task<bool>>? VerifyField;
        public Task<bool> VerifyFieldAsync() => VerifyField?.Invoke() ?? Task.FromResult(true);
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
        public bool LoseFocusBeforeSubmit;
        public uint SubmitSent = 2;
        public Task WaitForSubmitAsync() { Calls.Add("submit-wait"); if (LoseFocusBeforeSubmit) CanPaste = false; return Task.CompletedTask; }
        public uint SendSubmit() { Calls.Add("enter"); return SubmitSent; }
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
    public async Task SubmitPressesEnterAfterPasteBeforeRestore()
    {
        var consumed = new TaskCompletionSource();
        var platform = new Fake { PendingWait = consumed };
        var result = await ClipboardPasteOperation.RunAsync(platform, "text", submit: true);
        Assert.True(result.Inserted);
        Assert.True(result.Submitted);
        Assert.Equal(new[] { "capture", "paste", "wait", "submit-wait", "enter" }, platform.Calls);
        consumed.SetResult();
        await result.Restored;
        Assert.Equal("restore", platform.Calls[^2]);
    }

    [Fact]
    public async Task PasteWithoutSubmitNeverPressesEnter()
    {
        var platform = new Fake();
        var result = await ClipboardPasteOperation.RunAsync(platform, "text");
        Assert.False(result.Submitted);
        Assert.DoesNotContain("enter", platform.Calls);
    }

    [Fact]
    public async Task ChangedFocusAfterPasteKeepsTextButSkipsEnter()
    {
        var platform = new Fake { LoseFocusBeforeSubmit = true };
        var result = await ClipboardPasteOperation.RunAsync(platform, "text", submit: true);
        Assert.True(result.Inserted);
        Assert.False(result.Submitted);
        Assert.DoesNotContain("enter", platform.Calls);
    }

    [Fact]
    public async Task ChangedFieldAfterPasteSkipsEnter()
    {
        var checks = 0;
        var platform = new Fake { VerifyField = () => Task.FromResult(++checks <= 2) };
        var result = await ClipboardPasteOperation.RunAsync(platform, "text", submit: true);
        Assert.True(result.Inserted);
        Assert.False(result.Submitted);
        Assert.DoesNotContain("enter", platform.Calls);
    }

    [Fact]
    public async Task FailedFieldCheckBeforeEnterDoesNotFailThePaste()
    {
        var checks = 0;
        var platform = new Fake { VerifyField = () => ++checks <= 2 ? Task.FromResult(true) : throw new InvalidOperationException("UI Automation") };
        var result = await ClipboardPasteOperation.RunAsync(platform, "text", submit: true);
        Assert.True(result.Inserted);
        Assert.False(result.Submitted);
        await result.Restored;
        Assert.Contains("restore", platform.Calls);
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, true)]
    public async Task SentEnterKeyDownCountsAsSubmitted(uint sent, bool submitted)
    {
        var platform = new Fake { SubmitSent = sent };
        Assert.Equal(submitted, (await ClipboardPasteOperation.RunAsync(platform, "text", submit: true)).Submitted);
    }

    [Fact]
    public async Task RejectedPasteNeverPressesEnter()
    {
        var platform = new Fake { LoseFocusAfterCapture = true };
        var result = await ClipboardPasteOperation.RunAsync(platform, "text", submit: true);
        Assert.False(result.Inserted);
        Assert.False(result.Submitted);
        Assert.DoesNotContain("submit-wait", platform.Calls);
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
    public async Task LosingPermissionWhileFieldVerificationIsPendingRestoresWithoutPasting()
    {
        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verified = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var checks = 0;
        var platform = new Fake
        {
            VerifyField = () =>
            {
                if (++checks == 1) return Task.FromResult(true);
                checking.SetResult();
                return verified.Task;
            }
        };
        var paste = ClipboardPasteOperation.RunAsync(platform, "text");
        await checking.Task.WaitAsync(TimeSpan.FromSeconds(5));
        platform.CanPaste = false;
        verified.SetResult(true);

        Assert.False((await paste).Inserted);
        Assert.Equal(new[] { "capture", "restore", "dispose" }, platform.Calls);
    }

    [Fact]
    public async Task FailedAsyncFieldCheckAfterClipboardCaptureRestoresIt()
    {
        var checks = 0;
        var platform = new Fake { VerifyField = () => Task.FromResult(++checks == 1) };
        Assert.False((await ClipboardPasteOperation.RunAsync(platform, "text")).Inserted);
        Assert.Equal(new[] { "capture", "restore", "dispose" }, platform.Calls);
    }

    [Fact]
    public async Task SendExceptionStillRestores()
    {
        var platform = new Fake { ThrowOnSend = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ClipboardPasteOperation.RunAsync(platform, "text"));
        Assert.Contains("restore", platform.Calls);
    }
}
