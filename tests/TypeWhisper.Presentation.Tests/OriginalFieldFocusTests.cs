using TypeWhisper.WinUI;
using Xunit;

public class OriginalFieldFocusTests
{
    [Fact]
    public async Task AlreadyFocusedFieldDoesNotResetCaret()
    {
        Assert.True(await OriginalFieldFocus.RestoreAsync(() => true, () => true,
            () => throw new Exception("Must not refocus"), _ => throw new Exception("Must not wait"), default));
    }

    [Fact]
    public async Task DelayedProviderFocusSucceedsWithoutSendingFocusTwice()
    {
        var waits = 0;
        var focusRequests = 0;
        Assert.True(await OriginalFieldFocus.RestoreAsync(() => waits >= 3, () => true,
            () => focusRequests++, _ => { waits++; return Task.CompletedTask; }, default));
        Assert.Equal(1, focusRequests);
        Assert.Equal(3, waits);
    }

    [Fact]
    public async Task LeavingTargetWindowStopsWaiting()
    {
        var waits = 0;
        Assert.False(await OriginalFieldFocus.RestoreAsync(() => false, () => waits == 0,
            () => { }, _ => { waits++; return Task.CompletedTask; }, default));
        Assert.Equal(1, waits);
    }

    [Fact]
    public async Task ProviderThatNeverFocusesFailsWithinBudget()
    {
        var waits = 0;
        Assert.False(await OriginalFieldFocus.RestoreAsync(() => false, () => true,
            () => { }, _ => { waits++; return Task.CompletedTask; }, default));
        Assert.Equal(8, waits);
    }

    [Fact]
    public async Task InvalidTargetIsNeverFocused()
    {
        Assert.False(await OriginalFieldFocus.RestoreAsync(() => false, () => false,
            () => throw new Exception("Must not refocus"), _ => throw new Exception("Must not wait"), default));
    }

    [Fact]
    public async Task CancellationDuringFocusWaitAborts()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OriginalFieldFocus.RestoreAsync(
            () => false, () => true, () => { }, _ => { cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token));
    }
}
