using TypeWhisper.Presentation;
using Xunit;

public sealed class DeferredPageRefreshTests
{
    [Fact]
    public void NewPageHasNothingToCatchUpOn()
    {
        var refresh = new DeferredPageRefresh();
        Assert.False(refresh.IsStale);
        Assert.False(refresh.OnShown());
    }

    [Fact]
    public void ShownPageRefreshesForEveryChange()
    {
        var refresh = new DeferredPageRefresh();
        Assert.True(refresh.OnChange(shown: true));
        Assert.True(refresh.OnChange(shown: true));
        Assert.False(refresh.IsStale);
        Assert.False(refresh.OnShown());
    }

    [Fact]
    public void HiddenPageCatchesUpOnceForAllMissedChanges()
    {
        var refresh = new DeferredPageRefresh();
        // Several status changes of one dictation while Settings shows another page.
        for (var change = 0; change < 6; change++) Assert.False(refresh.OnChange(shown: false));
        Assert.True(refresh.IsStale);
        Assert.True(refresh.OnShown());
        Assert.False(refresh.IsStale);
        Assert.False(refresh.OnShown());
    }

    [Fact]
    public void ChangeThatMattersWhileHiddenRefreshesAtOnce()
    {
        var refresh = new DeferredPageRefresh();
        Assert.False(refresh.OnChange(shown: false));
        // An installed or removed package: the hidden page's list also fills the sidebar.
        Assert.True(refresh.OnChange(shown: false, always: true));
        Assert.False(refresh.IsStale);
        Assert.False(refresh.OnShown());
    }

    [Fact]
    public void RefreshForAnotherReasonLeavesNothingToCatchUpOn()
    {
        var refresh = new DeferredPageRefresh();
        Assert.False(refresh.OnChange(shown: false));
        refresh.OnRefreshed();
        Assert.False(refresh.IsStale);
        Assert.False(refresh.OnShown());
    }

    [Fact]
    public void ChangeAfterCatchingUpIsRememberedAgain()
    {
        var refresh = new DeferredPageRefresh();
        refresh.OnChange(shown: false);
        Assert.True(refresh.OnShown());
        Assert.False(refresh.OnChange(shown: false));
        Assert.True(refresh.OnShown());
    }
}
