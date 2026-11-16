using TypeWhisper.WinUI;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class HistoryAudioLoadsTests
{
    private static readonly TimeSpan Early = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Late = TimeSpan.FromSeconds(42);

    [Fact]
    public void OnlyTheLatestLoadIsCurrentAndAppliesItsTime()
    {
        var loads = new HistoryAudioLoads();
        var first = loads.Begin("a", Early);
        var second = loads.Begin("a", Late);

        Assert.False(loads.IsCurrent(first));
        Assert.True(loads.IsCurrent(second));
        // The earlier load finishing last neither applies its time nor takes the pending one.
        Assert.Null(loads.Opened(first));
        Assert.Equal(Late, loads.Opened(second));
        Assert.Null(loads.Opened(second));
    }

    [Fact]
    public void SeeksWhileTheAudioLoadsKeepOnlyTheLatestTime()
    {
        var loads = new HistoryAudioLoads();
        var load = loads.Begin("a", Early);

        Assert.True(loads.TrySeekPending("a", TimeSpan.FromSeconds(9)));
        Assert.True(loads.TrySeekPending("a", Late));
        Assert.False(loads.TrySeekPending("b", Early));

        Assert.Equal(Late, loads.Opened(load));
        // Once opened, a seek goes to the player directly.
        Assert.False(loads.TrySeekPending("a", Early));
    }

    [Fact]
    public void ALoadWithoutATimeTakesALaterSeek()
    {
        var loads = new HistoryAudioLoads();
        var load = loads.Begin("a", null);

        Assert.True(loads.TrySeekPending("a", Late));

        Assert.Equal(Late, loads.Opened(load));
    }

    [Fact]
    public void StoppingMakesEveryLoadStaleAndDropsThePendingTime()
    {
        var loads = new HistoryAudioLoads();
        var load = loads.Begin("a", Early);

        loads.Reset();

        Assert.False(loads.IsCurrent(load));
        Assert.Null(loads.LoadingId);
        Assert.Null(loads.PendingPosition);
        Assert.Null(loads.Opened(load));
        Assert.False(loads.TrySeekPending("a", Late));
    }
}
