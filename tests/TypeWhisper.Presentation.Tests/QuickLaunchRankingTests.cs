using TypeWhisper.Presentation;
using Xunit;

public sealed class QuickLaunchRankingTests
{
    private readonly Dictionary<string, QuickLaunchUsage> _usage = new()
    {
        ["recent"] = new(4, DateTimeOffset.Parse("2026-09-08T12:00:00Z")),
        ["frequent"] = new(8, DateTimeOffset.Parse("2026-09-01T12:00:00Z")),
        ["older"] = new(4, DateTimeOffset.Parse("2026-09-01T12:00:00Z"))
    };

    [Fact]
    public void BrowsingKeepsPinsFirstThenFrequencyThenRecencyThenStableUnusedCommands()
    {
        var result = QuickLaunchRanking.Order(["unused", "older", "pin", "recent", "frequent", "another"], new HashSet<string> { "pin" }, _usage, false);
        Assert.Equal(["pin", "frequent", "recent", "older", "unused", "another"], result);
    }

    [Fact]
    public void SearchingPreservesRelevanceWithinEachGroup()
    {
        var result = QuickLaunchRanking.Order(["recent", "pin", "unused", "frequent"], new HashSet<string> { "pin" }, _usage, true);
        Assert.Equal(["pin", "recent", "unused", "frequent"], result);
    }

    [Fact]
    public void PinOrderDoesNotJumpWithUsage()
    {
        var result = QuickLaunchRanking.Order(["older", "recent", "frequent"], new HashSet<string> { "older", "frequent" }, _usage, false);
        Assert.Equal(["older", "frequent", "recent"], result);
    }
}
