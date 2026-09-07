using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class SelectedTextCaptureOperationTests
{
    private static readonly SelectedTextCaptureOptions Fast = new()
    { CopyTimeout = TimeSpan.FromMilliseconds(100), PollInterval = TimeSpan.FromMilliseconds(25) };

    [Fact]
    public async Task CopiesExactlyOnceAndRestoresAfterVerifiedSelection()
    {
        var platform = new Platform();
        Assert.Equal("selected\ntext", await SelectedTextCaptureOperation.RunAsync(platform));
        Assert.Equal(1, platform.Copies);
        Assert.Equal(1, platform.Accepted);
        Assert.Equal(1, platform.Restored);
        Assert.True(platform.Lease.Disposed);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ModifierOrTargetFailureDoesNotTouchClipboard(bool released, bool target)
    {
        var platform = new Platform { Released = released, TargetStillCurrent = target };
        await Assert.ThrowsAsync<InvalidOperationException>(() => SelectedTextCaptureOperation.RunAsync(platform));
        Assert.Equal(0, platform.Begun);
        Assert.Equal(0, platform.Copies);
    }

    [Fact]
    public async Task ForeignClipboardSequenceIsNeitherAcceptedNorRestoredOver()
    {
        var platform = new Platform { State = new("foreign secret", 3, false) };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SelectedTextCaptureOperation.RunAsync(platform));
        Assert.DoesNotContain("foreign secret", error.Message);
        Assert.Equal(0, platform.Accepted);
        Assert.Equal(1, platform.Restored); // Platform performs its own sequence comparison.
        Assert.False(platform.OriginalRestored);
    }

    [Fact]
    public async Task ClipboardChangeBetweenReadAndAcceptanceIsRejected()
    {
        var platform = new Platform { AcceptResult = false };
        await Assert.ThrowsAsync<InvalidOperationException>(() => SelectedTextCaptureOperation.RunAsync(platform));
        Assert.False(platform.OriginalRestored);
        Assert.True(platform.Lease.Disposed);
    }

    [Fact]
    public async Task UnchangedMarkerTimesOutWithoutFallbackOrRepeatedCopy()
    {
        var platform = new Platform { State = new("old clipboard text", 1, false) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => SelectedTextCaptureOperation.RunAsync(platform, options: Fast));
        Assert.Equal(1, platform.Copies);
        Assert.InRange(platform.Reads, 1, 5);
        Assert.Equal(0, platform.Accepted);
        Assert.True(platform.OriginalRestored);
    }

    [Fact]
    public async Task OversizedSelectionIsRestoredButNeverTruncatedOrReturned()
    {
        var platform = new Platform { State = new(new string('x', SelectedTextCaptureOperation.MaxInputCharacters + 1), 2, true) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => SelectedTextCaptureOperation.RunAsync(platform));
        Assert.Equal(1, platform.Accepted);
        Assert.True(platform.OriginalRestored);
    }

    [Fact]
    public async Task ExactLengthBoundaryIsPreserved()
    {
        var text = new string('x', SelectedTextCaptureOperation.MaxInputCharacters);
        Assert.Equal(text, await SelectedTextCaptureOperation.RunAsync(new Platform { State = new(text, 2, true) }));
    }

    [Fact]
    public async Task CancellationAfterLeaseCreationStillRestoresAndDisposes()
    {
        using var cancellation = new CancellationTokenSource();
        var platform = new Platform { AfterBegin = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SelectedTextCaptureOperation.RunAsync(platform, cancellation.Token));
        Assert.Equal(0, platform.Copies);
        Assert.Equal(1, platform.Restored);
        Assert.True(platform.Lease.Disposed);
    }

    [Fact]
    public async Task CancellationDuringReadDrainsRestoreBeforeCompletion()
    {
        using var cancellation = new CancellationTokenSource();
        var restore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var platform = new Platform { AfterRead = cancellation.Cancel, RestoreWork = () => restore.Task };
        var operation = SelectedTextCaptureOperation.RunAsync(platform, cancellation.Token);
        Assert.False(operation.IsCompleted);
        Assert.Equal(1, platform.Restored);
        Assert.False(platform.Lease.Disposed);
        restore.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.True(platform.Lease.Disposed);
        Assert.True(platform.OriginalRestored);
    }

    [Fact]
    public async Task TargetChangesDuringReadAndPartialCopyCannotReturnText()
    {
        var platform = new Platform();
        platform.AfterRead = () => platform.TargetStillCurrent = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => SelectedTextCaptureOperation.RunAsync(platform));
        Assert.Equal(0, platform.Accepted);
        var partial = new Platform { CopyCount = 2 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => SelectedTextCaptureOperation.RunAsync(partial));
        Assert.Equal(1, partial.Copies);
        Assert.Equal(0, partial.Reads);
        Assert.True(partial.Lease.Disposed);
    }

    [Fact]
    public async Task RestoreFailureStillDisposesLeaseAndDoesNotReturnSelection()
    {
        var platform = new Platform { RestoreWork = () => throw new IOException("restore failed") };
        await Assert.ThrowsAsync<IOException>(() => SelectedTextCaptureOperation.RunAsync(platform));
        Assert.True(platform.Lease.Disposed);
    }

    private sealed class Lease : ISelectedTextCaptureLease
    {
        public uint MarkerSequenceNumber => 1;
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }
    private sealed class Platform : ISelectedTextCapturePlatform
    {
        public Lease Lease { get; } = new();
        public bool TargetStillCurrent { get; set; } = true;
        public bool Released = true;
        public bool AcceptResult = true;
        public SelectedClipboardState State = new("selected\ntext", 2, true);
        public uint CopyCount = 4;
        public int Begun, Copies, Reads, Accepted, Restored;
        public bool OriginalRestored;
        public Action? AfterBegin, AfterRead;
        public Func<Task> RestoreWork = () => Task.CompletedTask;
        public Task<bool> WaitModifiersReleasedAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(Released);
        public Task<ISelectedTextCaptureLease> BeginTemporaryAsync(string marker, CancellationToken ct)
        { Begun++; AfterBegin?.Invoke(); return Task.FromResult<ISelectedTextCaptureLease>(Lease); }
        public bool OwnsMarker(ISelectedTextCaptureLease lease) => true;
        public uint SendCopy() { Copies++; return CopyCount; }
        public Task<SelectedClipboardState> ReadClipboardAsync(CancellationToken ct)
        { Reads++; AfterRead?.Invoke(); return Task.FromResult(State); }
        public bool AcceptCopiedSequence(ISelectedTextCaptureLease lease, SelectedClipboardState state)
        { if (AcceptResult) Accepted++; return AcceptResult; }
        public async Task RestoreAsync(ISelectedTextCaptureLease lease)
        { Restored++; await RestoreWork(); OriginalRestored = State.SequenceNumber == 1 || Accepted == 1; }
        public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.CompletedTask;
    }
}
