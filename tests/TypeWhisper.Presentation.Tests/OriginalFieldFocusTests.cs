using TypeWhisper.WinUI;
using Xunit;

public class OriginalFieldFocusTests
{
    [Theory]
    [InlineData(50025)]
    [InlineData(50026)]
    public void CustomEditorsRequireFocusAndWritableTextMetadata(int controlType)
    {
        Assert.True(OriginalFieldFocus.IsEditableControl(controlType,true,()=>true));
        Assert.False(OriginalFieldFocus.IsEditableControl(controlType,true,()=>false));
        Assert.False(OriginalFieldFocus.IsEditableControl(controlType,false,()=>throw new Exception("Non-focusable groups must not be queried.")));
    }

    [Theory]
    [InlineData(50000)]
    [InlineData(50020)]
    [InlineData(50032)]
    [InlineData(50033)]
    public void OtherControlsCannotBecomeEditorsThroughAPattern(int controlType) =>
        Assert.False(OriginalFieldFocus.IsEditableControl(controlType,true,()=>throw new Exception("Unexpected pattern query.")));

    [Theory]
    [InlineData(50004)]
    [InlineData(50030)]
    public void ExistingEditAndDocumentProvidersKeepTheirCompatibility(int controlType) =>
        Assert.True(OriginalFieldFocus.IsEditableControl(controlType,true,()=>throw new Exception("Existing providers do not require new patterns.")));

    [Fact]
    public async Task ColdProviderReportingPaneFirstIsCapturedOnRetry()
    {
        // #513: the first UIA query to Chromium returns the render host Pane (50033).
        var queries = 0; var waits = 0;
        Assert.True(await OriginalFieldFocus.CaptureAsync(() => ++queries >= 3, () => true,
            _ => { waits++; return Task.CompletedTask; }, default));
        Assert.Equal(3, queries);
        Assert.Equal(2, waits);
    }

    [Fact]
    public async Task CaptureStopsRetryingWhenTargetLosesForeground()
    {
        var queries = 0;
        Assert.False(await OriginalFieldFocus.CaptureAsync(() => { queries++; return false; }, () => false,
            _ => throw new Exception("Must not wait for a target in the background"), default));
        Assert.Equal(1, queries);
    }

    [Fact]
    public async Task CaptureRetriesAreBounded()
    {
        var queries = 0;
        Assert.False(await OriginalFieldFocus.CaptureAsync(() => { queries++; return false; }, () => true,
            _ => Task.CompletedTask, default, attempts: 4));
        Assert.Equal(4, queries);
    }

    [Fact]
    public async Task CaptureStopsRetryingWhenTimeBudgetExpires()
    {
        // Slow providers must not hold recording startup (and a pending Stop) for all attempts.
        var queries = 0;
        Assert.False(await OriginalFieldFocus.CaptureAsync(() => { queries++; return false; }, () => true,
            _ => Task.CompletedTask, default, attempts: 20, expired: () => queries >= 2));
        Assert.Equal(2, queries);
    }

    [Fact]
    public async Task CanceledCaptureDoesNotQueryAgain()
    {
        using var cancellation = new CancellationTokenSource();
        var queries = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OriginalFieldFocus.CaptureAsync(
            () => { queries++; return false; }, () => true, _ => { cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token));
        Assert.Equal(1, queries);
    }

    [Fact]
    public async Task ExpiredWindowDeadlineDoesNotActivate()
    {
        Assert.False(await OriginalFieldFocus.RestoreWindowAsync(() => false, () => true,
            () => throw new Exception("Must not activate after the deadline"),
            () => throw new Exception("Must not attach and activate after the deadline"),
            _ => throw new Exception("Must not wait after the deadline"), default, () => true));
    }

    [Fact]
    public async Task SlowWindowActivationDoesNotAttemptAttachedActivationAfterDeadline()
    {
        var expired = false;
        Assert.False(await OriginalFieldFocus.RestoreWindowAsync(() => false, () => true,
            () => expired = true,
            () => throw new Exception("Must not retry activation after the deadline"),
            _ => throw new Exception("Must not wait after the deadline"), default, () => expired));
    }

    [Fact]
    public async Task WindowAndFieldRestorationShareOneDeadline()
    {
        var elapsed = TimeSpan.Zero;
        Func<bool> expired = () => elapsed >= TimeSpan.FromSeconds(5);
        Task Wait(CancellationToken _) { elapsed += TimeSpan.FromMilliseconds(25); return Task.CompletedTask; }

        Assert.True(await OriginalFieldFocus.RestoreWindowAsync(
            () => elapsed >= TimeSpan.FromSeconds(4), () => true,
            () => { }, () => { }, Wait, default, expired));

        // The element would become ready after the overall budget. Window activation
        // must not give the second phase another five seconds to deliver text.
        Assert.False(await OriginalFieldFocus.RestoreAsync(
            () => elapsed >= TimeSpan.FromSeconds(6), () => true,
            () => { }, Wait, default, expired));
        Assert.Equal(TimeSpan.FromSeconds(5), elapsed);
    }

    [Fact]
    public async Task SlowProviderIsAcceptedOnlyAfterReadinessBeyondOldRetryBudget()
    {
        var observations = 0;
        var focusRequests = 0;
        Assert.True(await OriginalFieldFocus.RestoreAsync(() => observations >= 40, () => true,
            () => focusRequests++, _ => { observations++; return Task.CompletedTask; }, default,
            () => observations >= 100));
        Assert.Equal(40, observations);
        Assert.Equal(1, focusRequests);
    }

    [Fact]
    public async Task WindowReadyAtDeadlineDoesNotRequestElementFocus()
    {
        var elapsed = TimeSpan.Zero;
        Func<bool> expired = () => elapsed >= TimeSpan.FromSeconds(5);
        Assert.True(await OriginalFieldFocus.RestoreWindowAsync(
            () => expired(), () => true, () => { }, () => { },
            _ => { elapsed = TimeSpan.FromSeconds(5); return Task.CompletedTask; }, default, expired));

        Assert.False(await OriginalFieldFocus.RestoreAsync(() => false, () => true,
            () => throw new Exception("Must not request focus after the shared deadline"),
            _ => throw new Exception("Must not wait after the shared deadline"), default, expired));
    }

    [Fact]
    public async Task ExpiredDeadlineDoesNotPretendFieldIsReady()
    {
        Assert.False(await OriginalFieldFocus.RestoreAsync(() => false, () => true,
            () => { }, _ => throw new Exception("Must stop"), default, () => true));
    }

    [Fact]
    public async Task ProviderCanActivateWindowWhenOriginalElementReceivesFocus()
    {
        var foreground = false;
        var requested = false;
        var waits = 0;
        Assert.True(await OriginalFieldFocus.RestoreWindowAsync(() => foreground, () => true,
            () => { }, () => requested = true, _ =>
            {
                if (requested && ++waits == 3) foreground = true;
                return Task.CompletedTask;
            }, default));
        Assert.True(requested);
        Assert.Equal(3, waits);
    }

    [Fact]
    public async Task DeniedWindowActivationRetriesBeforeRestoringOriginalField()
    {
        var focused = false;
        var normal = 0;
        var fallback = 0;
        Assert.True(await OriginalFieldFocus.RestoreWindowAsync(() => focused, () => true,
            () => normal++, () => { fallback++; focused = true; }, _ => Task.CompletedTask, default));
        Assert.Equal(1, normal);
        Assert.Equal(1, fallback);
    }

    [Fact]
    public async Task ClosedOriginalWindowNeverReceivesActivationRetry()
    {
        var valid = true;
        Assert.False(await OriginalFieldFocus.RestoreWindowAsync(() => false, () => valid,
            () => valid = false, () => throw new Exception("Must not reactivate stale target"),
            _ => Task.CompletedTask, default));
    }

    [Fact]
    public async Task WindowActivationFailureRemainsBounded()
    {
        var waits = 0;
        var fallback = 0;
        Assert.False(await OriginalFieldFocus.RestoreWindowAsync(() => false, () => true,
            () => { }, () => fallback++, _ => { waits++; return Task.CompletedTask; }, default, () => waits >= 16));
        Assert.Equal(16, waits);
        Assert.Equal(1, fallback);
    }

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
            () => { }, _ => { waits++; return Task.CompletedTask; }, default, () => waits >= 8));
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
