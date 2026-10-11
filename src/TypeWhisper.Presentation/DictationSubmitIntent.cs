namespace TypeWhisper.Presentation;

/// <summary>Whether a physical Enter during the current recording stops it and submits the text.</summary>
public enum DictationSubmitArming
{
    /// <summary>Enter reaches the focused app unchanged.</summary>
    Off,
    /// <summary>The automatic workflow is still being matched and a matching rule could submit. Enter stops the
    /// recording; the text is submitted only if the matched rule does.</summary>
    Pending,
    /// <summary>The recording's workflow submits with Enter.</summary>
    Armed
}

/// <summary>
/// The submit decision of one recording. Only a physical Enter during that recording requests it, and a
/// request never carries over to the next recording. Used on the UI thread only.
/// </summary>
public sealed class DictationSubmitIntent
{
    private DictationSubmitArming _arming;
    private bool _requested, _closed;

    /// <summary>The current arming, for diagnostics.</summary>
    public DictationSubmitArming Arming => _arming;

    /// <summary>Whether a physical Enter is taken from the focused app now.</summary>
    public bool CanPress => !_closed && !_requested && _arming != DictationSubmitArming.Off;

    /// <summary>Whether the user pressed Enter and the recording's workflow submits.</summary>
    public bool Requested => _requested && _arming == DictationSubmitArming.Armed;

    /// <summary>Starts a recording without any request.</summary>
    public void Begin(DictationSubmitArming arming) { _arming = arming; _requested = false; _closed = false; }

    /// <summary>Re-arms a recording that is already running, keeping its request unless it can no longer submit.</summary>
    public void Continue(DictationSubmitArming arming)
    {
        if (_closed) return;
        _arming = arming;
        if (arming == DictationSubmitArming.Off) _requested = false;
    }

    /// <summary>Records a press; returns whether it requested submission and must stop the recording.</summary>
    public bool Press()
    {
        if (!CanPress) return false;
        _requested = true;
        return true;
    }

    /// <summary>Applies the matched workflow. A pending request is dropped when the workflow does not submit.</summary>
    public void Resolve(bool submitsDuringDictation)
    {
        if (_closed) return;
        _arming = submitsDuringDictation ? DictationSubmitArming.Armed : DictationSubmitArming.Off;
        if (!submitsDuringDictation) _requested = false;
    }

    /// <summary>Ends the recording: later presses reach the app, and the decision stays for its delivery.</summary>
    public void Close() => _closed = true;

    /// <summary>Forgets the recording, e.g. after cancellation or delivery.</summary>
    public void Reset() { _arming = DictationSubmitArming.Off; _requested = false; _closed = false; }
}
