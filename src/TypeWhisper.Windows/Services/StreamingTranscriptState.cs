using System.Threading;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Windows.Services;

internal sealed class StreamingTranscriptState
{
    private int _sessionVersion;
    private string _confirmedText = "";
    private string _lastDisplayedText = "";
    private string _lastFinalSegment = "";

    /// <summary>
    /// Starts a new streaming transcript session and returns its version token.
    /// </summary>
    public int StartSession()
    {
        _confirmedText = "";
        _lastDisplayedText = "";
        _lastFinalSegment = "";
        return Interlocked.Increment(ref _sessionVersion);
    }

    /// <summary>
    /// Stops the current session and returns the most recent display text.
    /// </summary>
    public string StopSession()
    {
        var finalText = !string.IsNullOrWhiteSpace(_lastDisplayedText)
            ? _lastDisplayedText
            : _confirmedText;
        InvalidateSession();
        _confirmedText = "";
        _lastDisplayedText = "";
        _lastFinalSegment = "";
        return finalText;
    }

    /// <summary>
    /// Returns whether the supplied version still belongs to the active session.
    /// </summary>
    public bool IsCurrentSession(int sessionVersion) =>
        sessionVersion == Volatile.Read(ref _sessionVersion);

    /// <summary>
    /// Invalidates in-flight transcript updates from older sessions.
    /// </summary>
    public void InvalidateSession() => Interlocked.Increment(ref _sessionVersion);

    /// <summary>
    /// Applies a real-time transcript update and returns false for stale or empty updates.
    /// </summary>
    public bool TryApplyRealtime(
        int sessionVersion,
        StreamingTranscriptEvent evt,
        Func<string, string> corrector,
        out string displayText)
    {
        displayText = "";
        if (!IsCurrentSession(sessionVersion))
            return false;

        var text = evt.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(text))
            return false;

        text = corrector(text);
        if (string.IsNullOrEmpty(text))
            return false;

        if (evt.IsFinal)
        {
            // Some providers send final events as cumulative text while others send only the
            // newest segment, so final updates are merged with duplicate protection.
            _confirmedText = MergeFinalSegment(_confirmedText, _lastFinalSegment, text);
            _lastFinalSegment = text;
            _lastDisplayedText = _confirmedText;
            displayText = _confirmedText;
            return true;
        }

        displayText = string.IsNullOrEmpty(_confirmedText)
            ? text
            : _confirmedText + " " + text;
        _lastDisplayedText = displayText;
        return true;
    }

    /// <summary>
    /// Applies a polled transcript snapshot and stabilizes it against confirmed text.
    /// </summary>
    public bool TryApplyPolling(
        int sessionVersion,
        string rawText,
        Func<string, string> corrector,
        out string displayText)
    {
        displayText = "";
        if (!IsCurrentSession(sessionVersion))
            return false;

        var text = rawText.Trim();
        if (string.IsNullOrEmpty(text))
            return false;

        text = corrector(text);
        if (string.IsNullOrEmpty(text))
            return false;

        // Polling sources can rewrite their full hypothesis on every read; keep the longest
        // stable prefix so the preview does not visibly regress while speech continues.
        var stable = StreamingHandler.StabilizeText(_confirmedText, text);
        _confirmedText = stable;
        _lastDisplayedText = stable;
        displayText = stable;
        return true;
    }

    private static string MergeFinalSegment(string confirmedText, string lastFinalSegment, string newText)
    {
        if (string.IsNullOrEmpty(confirmedText))
            return newText;

        if (string.Equals(newText, confirmedText, StringComparison.Ordinal)
            || string.Equals(newText, lastFinalSegment, StringComparison.Ordinal))
        {
            return confirmedText;
        }

        if (newText.StartsWith(confirmedText, StringComparison.Ordinal))
            return newText;

        return confirmedText + " " + newText;
    }
}
