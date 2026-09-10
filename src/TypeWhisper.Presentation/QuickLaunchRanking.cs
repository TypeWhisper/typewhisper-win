namespace TypeWhisper.Presentation;

/// <summary>Persisted local command usage for suggestion ordering.</summary>
/// <param name="Count">Number of command invocations.</param>
/// <param name="LastUsed">Most recent invocation time.</param>
public sealed record QuickLaunchUsage(long Count, DateTimeOffset LastUsed);

/// <summary>Orders launcher commands while keeping pins and search matches stable.</summary>
public static class QuickLaunchRanking
{
    // Keep pins stable and preserve search relevance; rank suggestions by usage only when browsing.
    /// <summary>Returns pins first and ranks browsing suggestions by frequency and recency.</summary>
    public static IEnumerable<string> Order(IEnumerable<string> candidates, ISet<string> pins,
        IReadOnlyDictionary<string, QuickLaunchUsage> usage, bool searching, IReadOnlyList<string>? pinOrder = null) =>
        candidates.OrderByDescending(pins.Contains)
            .ThenBy(id => pins.Contains(id) && pinOrder is not null ? PinPosition(pinOrder, id) : 0)
            .ThenByDescending(id => searching || pins.Contains(id) ? 0 : usage.GetValueOrDefault(id)?.Count ?? 0)
            .ThenByDescending(id => searching || pins.Contains(id) ? DateTimeOffset.MinValue : usage.GetValueOrDefault(id)?.LastUsed ?? DateTimeOffset.MinValue);
    private static int PinPosition(IReadOnlyList<string> order, string id)
    {
        for (var i = 0; i < order.Count; i++) if (order[i] == id) return i;
        return int.MaxValue;
    }
}
