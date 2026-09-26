namespace TypeWhisper.WinUI;

// Decides whether the live transcript keeps following its end. Only an upward move away from the end stops
// following: text that grows between a scroll request and its ViewChanged event must not count as the user
// scrolling back.
internal sealed class TranscriptScrollFollow
{
    private const double EndTolerance = 12;
    private double _lastOffset;

    internal bool Following { get; private set; } = true;

    internal void Reset()
    {
        Following = true;
        _lastOffset = 0;
    }

    internal void ViewChanged(double offset, double scrollableHeight)
    {
        if (scrollableHeight - offset <= EndTolerance)
            Following = true;
        else if (offset < _lastOffset)
            Following = false;
        _lastOffset = offset;
    }
}
