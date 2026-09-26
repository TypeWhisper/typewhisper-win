using TypeWhisper.WinUI;
using Xunit;

public sealed class TranscriptScrollFollowTests
{
    [Fact]
    public void TextGrowingBeforeTheScrollLandsKeepsFollowing()
    {
        var follow = new TranscriptScrollFollow();
        follow.ViewChanged(100, 100);

        // The scroll to 118 lands after another line has already grown the extent.
        follow.ViewChanged(118, 136);

        Assert.True(follow.Following);
    }

    [Fact]
    public void ScrollingUpAwayFromTheEndStopsFollowing()
    {
        var follow = new TranscriptScrollFollow();
        follow.ViewChanged(118, 118);

        follow.ViewChanged(60, 118);

        Assert.False(follow.Following);
    }

    [Fact]
    public void ScrollingUpWithinTheEndToleranceKeepsFollowing()
    {
        var follow = new TranscriptScrollFollow();
        follow.ViewChanged(118, 118);

        follow.ViewChanged(110, 118);

        Assert.True(follow.Following);
    }

    [Fact]
    public void ScrollingDownWithoutReachingTheEndStaysDetached()
    {
        var follow = new TranscriptScrollFollow();
        follow.ViewChanged(118, 118);
        follow.ViewChanged(20, 118);

        follow.ViewChanged(60, 136);

        Assert.False(follow.Following);
    }

    [Fact]
    public void ReturningToTheEndFollowsAgain()
    {
        var follow = new TranscriptScrollFollow();
        follow.ViewChanged(118, 118);
        follow.ViewChanged(20, 118);

        follow.ViewChanged(136, 136);

        Assert.True(follow.Following);
    }

    [Fact]
    public void ShrinkingTextThatClampsTheOffsetKeepsFollowing()
    {
        var follow = new TranscriptScrollFollow();
        follow.ViewChanged(136, 136);

        follow.ViewChanged(54, 54);

        Assert.True(follow.Following);
    }

    [Fact]
    public void ResetFollowsANewDictation()
    {
        var follow = new TranscriptScrollFollow();
        follow.ViewChanged(118, 118);
        follow.ViewChanged(20, 118);

        follow.Reset();

        Assert.True(follow.Following);
    }
}
