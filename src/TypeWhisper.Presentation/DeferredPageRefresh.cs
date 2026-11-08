namespace TypeWhisper.Presentation;

/// <summary>
/// Decides when a page that outlives its visibility refreshes for a change. A shown page refreshes at once; a
/// hidden page only remembers that something changed and catches up once when it is shown again.
/// </summary>
public sealed class DeferredPageRefresh
{
    /// <summary>Whether a change arrived while the page was hidden and the page has not refreshed since.</summary>
    public bool IsStale { get; private set; }

    /// <summary>
    /// Returns whether the page refreshes for a change now: it is shown, or <paramref name="always"/> marks a change
    /// that matters while it is hidden too. Otherwise the change is remembered for <see cref="OnShown"/>.
    /// </summary>
    public bool OnChange(bool shown, bool always = false)
    {
        if (shown || always)
        {
            IsStale = false;
            return true;
        }
        IsStale = true;
        return false;
    }

    /// <summary>Returns once whether a page that is shown now missed a change while it was hidden.</summary>
    public bool OnShown()
    {
        if (!IsStale) return false;
        IsStale = false;
        return true;
    }

    /// <summary>Records a refresh that ran for another reason; it leaves nothing to catch up on.</summary>
    public void OnRefreshed() => IsStale = false;
}
