using System.Threading;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.Windows.Services;

internal sealed class StreamingTranscriptState
{
    private int _sessionVersion;
    private string _confirmedText = "";
    private string _lastDisplayedText = "";

    public int StartSession()
    {
        _confirmedText = "";
        _lastDisplayedText = "";
        return Interlocked.Increment(ref _sessionVersion);
    }

    public string StopSession()
    {
        var hasUncommittedPreview = _lastDisplayedText != _confirmedText;
        var finalText = hasUncommittedPreview ? "" : _confirmedText;
        InvalidateSession();
        _confirmedText = "";
        _lastDisplayedText = "";
        return finalText;
    }

    public bool IsCurrentSession(int sessionVersion) =>
        sessionVersion == Volatile.Read(ref _sessionVersion);

    public void InvalidateSession() => Interlocked.Increment(ref _sessionVersion);

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
            _confirmedText = string.IsNullOrEmpty(_confirmedText)
                ? text
                : _confirmedText + " " + text;
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

        var stable = StreamingHandler.StabilizeText(_confirmedText, text);
        _confirmedText = stable;
        _lastDisplayedText = stable;
        displayText = stable;
        return true;
    }
}
