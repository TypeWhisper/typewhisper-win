using System.Windows.Automation;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Windows.Services;

/// <summary>
/// Describes the focused text element observed in the target application.
/// </summary>
/// <param name="ElementKey">Stable key for the UI Automation element while focus remains in the same text field.</param>
/// <param name="Value">Observed plain text value.</param>
/// <param name="WindowHandle">Owning top-level window handle.</param>
/// <param name="MaxValueLength">Maximum text length this observation may read on recapture.</param>
/// <param name="Element">Captured UI Automation element used for bounded recapture after focus leaves.</param>
/// <param name="AllowElementKeyChangeOnCommit">Whether an explicit commit may accept a same-window recapture with a changed element key.</param>
public sealed record TargetAppTextObservation(
    string ElementKey,
    string Value,
    IntPtr WindowHandle,
    int MaxValueLength = 4096,
    AutomationElement? Element = null,
    bool AllowElementKeyChangeOnCommit = false);

/// <summary>
/// Lists possible focus relationships for a previously observed text element.
/// </summary>
public enum TargetAppTextElementMatch
{
    /// <summary>
    /// The same target text element is still focused.
    /// </summary>
    Same,

    /// <summary>
    /// Focus moved to a different element or window.
    /// </summary>
    Different,

    /// <summary>
    /// Focus moved to another top-level window or process.
    /// </summary>
    DifferentWindow,

    /// <summary>
    /// The current focus state could not be observed safely.
    /// </summary>
    Unavailable
}

/// <summary>
/// Captures text values from the target application without clipboard or selection fallbacks.
/// </summary>
public interface ITargetAppTextObserver
{
    /// <summary>
    /// Captures the currently focused text element in the target window.
    /// </summary>
    TargetAppTextObservation? Capture(IntPtr targetHwnd, int maxTextLength);

    /// <summary>
    /// Captures a text element in the target window, preferring one that contains the supplied text.
    /// </summary>
    TargetAppTextObservation? Capture(IntPtr targetHwnd, int maxTextLength, string preferredText)
        => Capture(targetHwnd, maxTextLength);

    /// <summary>
    /// Best-effort capture that may inspect descendant text elements when focus alone is insufficient.
    /// </summary>
    TargetAppTextObservation? CaptureDeep(
        IntPtr targetHwnd,
        int maxTextLength,
        string preferredText,
        bool allowDescendantScan = false)
        => Capture(targetHwnd, maxTextLength, preferredText);

    /// <summary>
    /// Recaptures the same text element if it is still observable.
    /// </summary>
    TargetAppTextObservation? Recapture(TargetAppTextObservation baseline);

    /// <summary>
    /// Returns whether the focused element still matches the baseline.
    /// </summary>
    TargetAppTextElementMatch GetFocusedElementMatch(TargetAppTextObservation baseline);
}

/// <summary>
/// Provides optional commit signals that indicate the user accepted an edit.
/// </summary>
public interface ITargetAppCorrectionCommitObserver : IDisposable
{
    /// <summary>
    /// Starts observing commit signals.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops observing commit signals.
    /// </summary>
    void Stop()
    {
    }

    /// <summary>
    /// Returns and clears whether a commit gesture has happened.
    /// </summary>
    bool ConsumeCommitSignal();

    /// <summary>
    /// Raises a commit gesture for local automation runs.
    /// </summary>
    void SignalCommitForAutomation()
    {
    }
}

/// <summary>
/// Learns conservative target-app corrections after users directly edit pasted text.
/// </summary>
public sealed class TargetAppCorrectionLearningService
{
    internal const int MinObservedTextLength = 512;
    internal const int MaxObservedTextLength = 4096;
    internal const int MaxInsertedTextLength = 2048;

    private static readonly IReadOnlyList<TimeSpan> DefaultPollSchedule =
        Enumerable.Repeat(TimeSpan.FromSeconds(1), 30).ToArray();

    private readonly IDictionaryService _dictionary;
    private readonly ITargetAppTextObserver _textObserver;
    private readonly ITargetAppCorrectionCommitObserver _commitObserver;
    private readonly IReadOnlyList<TimeSpan> _pollSchedule;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly SemaphoreSlim _trackingGate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the TargetAppCorrectionLearningService class.
    /// </summary>
    public TargetAppCorrectionLearningService(
        IDictionaryService dictionary,
        ITargetAppTextObserver textObserver,
        ITargetAppCorrectionCommitObserver commitObserver,
        IReadOnlyList<TimeSpan>? pollSchedule = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _dictionary = dictionary;
        _textObserver = textObserver;
        _commitObserver = commitObserver;
        _pollSchedule = pollSchedule is { Count: > 0 } ? pollSchedule : DefaultPollSchedule;
        _delayAsync = delayAsync ?? Task.Delay;
    }

    /// <summary>
    /// Gets the bounded UI Automation read size for an inserted text segment.
    /// </summary>
    internal static int GetMaxObservedTextLength(string insertedText)
        => Math.Clamp((insertedText?.Length ?? 0) + MinObservedTextLength, MinObservedTextLength, MaxObservedTextLength);

    /// <summary>
    /// Tracks an inserted text segment until commit, timeout, or cancellation.
    /// </summary>
    public async Task<IReadOnlyList<LearnedDictionaryCorrection>> TrackInsertionAsync(
        string insertedText,
        TargetAppTextObservation baseline,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(insertedText) || string.IsNullOrWhiteSpace(baseline.Value))
            return [];

        try
        {
            await _trackingGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return [];
        }

        try
        {
            return await TrackInsertionCoreAsync(insertedText, baseline, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _trackingGate.Release();
        }
    }

    private async Task<IReadOnlyList<LearnedDictionaryCorrection>> TrackInsertionCoreAsync(
        string insertedText,
        TargetAppTextObservation baseline,
        CancellationToken cancellationToken)
    {
        _commitObserver.Start();

        try
        {
            var lastObserved = baseline;
            foreach (var pollDelay in _pollSchedule)
            {
                await _delayAsync(pollDelay, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                if (_commitObserver.ConsumeCommitSignal())
                    return LearnFromCurrentObservation(insertedText, baseline, lastObserved);

                var focusMatch = _textObserver.GetFocusedElementMatch(baseline);
                if (focusMatch == TargetAppTextElementMatch.Same)
                {
                    var current = TryRecaptureSameElement(baseline);
                    if (current is not null)
                    {
                        var commitSuggestions = ExtractSameElementLineBreakCommitSuggestions(
                            insertedText,
                            baseline.Value,
                            lastObserved.Value,
                            current.Value);
                        if (commitSuggestions.Count > 0)
                            return Learn(commitSuggestions);

                        lastObserved = current;
                    }

                    continue;
                }

                if (focusMatch == TargetAppTextElementMatch.Unavailable)
                    continue;

                if (focusMatch == TargetAppTextElementMatch.Different)
                {
                    if (baseline.AllowElementKeyChangeOnCommit)
                    {
                        if (TryRecaptureCommittedElement(baseline) is { } current)
                            lastObserved = current;

                        continue;
                    }

                    return LearnFromCurrentObservation(insertedText, baseline, lastObserved);
                }

                var learned = LearnFromCurrentObservation(insertedText, baseline, lastObserved);
                if (learned.Count > 0 ||
                    !baseline.AllowElementKeyChangeOnCommit ||
                    focusMatch == TargetAppTextElementMatch.DifferentWindow)
                {
                    return learned;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        finally
        {
            _commitObserver.Stop();
        }

        return [];
    }

    /// <summary>
    /// Raises the same commit signal that an observed Enter or Tab key press would produce.
    /// </summary>
    public void SignalCommitForAutomation()
    {
        _commitObserver.SignalCommitForAutomation();
    }

    private IReadOnlyList<LearnedDictionaryCorrection> Learn(IReadOnlyList<CorrectionSuggestion> suggestions)
        => suggestions.Count == 0 ? [] : _dictionary.LearnCorrections(suggestions);

    private IReadOnlyList<LearnedDictionaryCorrection> LearnFromCurrentObservation(
        string insertedText,
        TargetAppTextObservation baseline,
        TargetAppTextObservation fallbackObservation)
    {
        var current = TryRecaptureCommittedElement(baseline);
        if (current is null ||
            (string.Equals(current.Value, baseline.Value, StringComparison.Ordinal) &&
             !string.Equals(fallbackObservation.Value, baseline.Value, StringComparison.Ordinal)))
            current = fallbackObservation;

        return Learn(ExtractSuggestions(insertedText, baseline.Value, current.Value));
    }

    private TargetAppTextObservation? TryRecaptureSameElement(TargetAppTextObservation baseline)
    {
        var current = _textObserver.Recapture(baseline);
        return current is not null &&
            string.Equals(current.ElementKey, baseline.ElementKey, StringComparison.Ordinal)
            ? current
            : null;
    }

    private TargetAppTextObservation? TryRecaptureCommittedElement(TargetAppTextObservation baseline)
    {
        var current = _textObserver.Recapture(baseline);
        if (current is null)
            return null;

        if (string.Equals(current.ElementKey, baseline.ElementKey, StringComparison.Ordinal))
            return current;

        return baseline.AllowElementKeyChangeOnCommit &&
            current.WindowHandle == baseline.WindowHandle
                ? current
                : null;
    }

    private static List<CorrectionSuggestion> ExtractSuggestions(
        string insertedText,
        string baselineValue,
        string currentValue)
    {
        var editedInsertedText = ExtractEditedInsertedText(insertedText, baselineValue, currentValue);
        if (editedInsertedText is null ||
            string.Equals(insertedText, editedInsertedText, StringComparison.Ordinal))
        {
            return [];
        }

        return TextDiffService.ExtractHighConfidenceCorrections(insertedText, editedInsertedText);
    }

    private static List<CorrectionSuggestion> ExtractSameElementLineBreakCommitSuggestions(
        string insertedText,
        string baselineValue,
        string previousValue,
        string currentValue)
    {
        if (CountLineBreaks(currentValue) <= CountLineBreaks(previousValue))
            return [];

        var candidates = new Dictionary<string, List<CorrectionSuggestion>>(StringComparer.Ordinal);
        foreach (var candidateValue in RemoveOneLineBreakCandidates(currentValue))
        {
            var suggestions = ExtractSuggestions(insertedText, baselineValue, candidateValue);
            if (suggestions.Count == 0)
                continue;

            var key = string.Join(
                '\u001f',
                suggestions.Select(static suggestion =>
                    string.Concat(suggestion.Original, '\u001e', suggestion.Replacement)));
            candidates.TryAdd(key, suggestions);
        }

        return candidates.Count == 1 ? candidates.Values.Single() : [];
    }

    private static int CountLineBreaks(string value)
    {
        var count = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\r')
            {
                count++;
                if (i + 1 < value.Length && value[i + 1] == '\n')
                    i++;
            }
            else if (value[i] == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private static IEnumerable<string> RemoveOneLineBreakCandidates(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var length = value[i] switch
            {
                '\r' when i + 1 < value.Length && value[i + 1] == '\n' => 2,
                '\r' or '\n' => 1,
                _ => 0
            };

            if (length == 0)
                continue;

            yield return string.Concat(value.AsSpan(0, i), value.AsSpan(i + length));
            if (length == 2)
                i++;
        }
    }

    private static string? ExtractEditedInsertedText(
        string insertedText,
        string baselineValue,
        string currentValue)
    {
        if (insertedText.Length == 0)
            return null;

        var matches = new List<string>();
        var start = baselineValue.IndexOf(insertedText, StringComparison.Ordinal);
        while (start >= 0)
        {
            var prefix = baselineValue[..start];
            var suffix = baselineValue[(start + insertedText.Length)..];
            if (currentValue.StartsWith(prefix, StringComparison.Ordinal) &&
                currentValue.EndsWith(suffix, StringComparison.Ordinal) &&
                currentValue.Length >= prefix.Length + suffix.Length)
            {
                var editedSegment = suffix.Length == 0
                    ? currentValue[prefix.Length..]
                    : currentValue[prefix.Length..^suffix.Length];
                matches.Add(editedSegment);
            }

            start = baselineValue.IndexOf(
                insertedText,
                start + insertedText.Length,
                StringComparison.Ordinal);
        }

        return matches.Count == 1 ? matches[0] : null;
    }
}
