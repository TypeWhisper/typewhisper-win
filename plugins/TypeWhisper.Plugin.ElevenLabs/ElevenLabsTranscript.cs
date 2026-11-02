using System.Globalization;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.ElevenLabs;

internal enum ElevenLabsEntryKind
{
    Word,
    Spacing,
    AudioEvent
}

/// <summary>One entry of a Scribe <c>words</c> array. Spacing entries carry no usable times.</summary>
internal sealed record ElevenLabsTranscriptEntry(ElevenLabsEntryKind Kind, string Text, double Start, double End);

/// <summary>
/// Groups Scribe word entries into subtitle-sized segments, like the macOS plugin: a segment ends
/// at a sentence end once it has a few words, at a pause longer than a second, or after 30 seconds.
/// </summary>
internal static class ElevenLabsSegmentBuilder
{
    /// <summary>A silence longer than this starts a new segment.</summary>
    internal const double PauseThreshold = 1.0;
    /// <summary>A sentence end splits only after this many words, so "Yes." joins the next sentence.</summary>
    internal const int MinimumSentenceWordCount = 3;
    /// <summary>Without a sentence end or pause, a segment is split at the next word after this length.</summary>
    internal const double MaximumSegmentDuration = 30;

    private static readonly char[] SentenceEnds = ['.', '!', '?', '…', '。', '！', '？', '؟', '।'];
    private static readonly char[] TrailingClosers = ['"', '\'', '”', '’', '»', '«', ')', ']', '」', '』'];

    internal static IReadOnlyList<PluginTranscriptionSegment> Build(IReadOnlyList<ElevenLabsTranscriptEntry> entries)
    {
        var segments = new List<PluginTranscriptionSegment>();
        var text = new System.Text.StringBuilder();
        double? start = null;
        double? end = null;
        var wordCount = 0;
        var lastWordEndsSentence = false;

        void Close()
        {
            var trimmed = text.ToString().Trim();
            if (trimmed.Length > 0 && start is { } segmentStart && end is { } segmentEnd)
                segments.Add(new PluginTranscriptionSegment(trimmed, segmentStart, Math.Max(segmentStart, segmentEnd)));
            text.Clear();
            start = null;
            end = null;
            wordCount = 0;
            lastWordEndsSentence = false;
        }

        foreach (var entry in entries)
        {
            if (entry.Kind == ElevenLabsEntryKind.Spacing)
            {
                text.Append(entry.Text);
                continue;
            }

            if (start is { } segmentStart && end is { } segmentEnd)
            {
                var pause = entry.Start - segmentEnd;
                var isLong = entry.Start - segmentStart >= MaximumSegmentDuration;
                var isSentenceDone = lastWordEndsSentence && wordCount >= MinimumSentenceWordCount;
                if (pause > PauseThreshold || isSentenceDone || isLong)
                    Close();
            }

            text.Append(entry.Text);
            start ??= entry.Start;
            end = Math.Max(end ?? entry.End, entry.End);
            if (entry.Kind == ElevenLabsEntryKind.Word)
            {
                wordCount++;
                lastWordEndsSentence = EndsSentence(entry.Text);
            }
        }

        Close();
        return segments;
    }

    private static bool EndsSentence(string word)
    {
        var trimmed = word.Trim().TrimEnd(TrailingClosers);
        return trimmed.Length > 0 && SentenceEnds.Contains(trimmed[^1]);
    }
}

/// <summary>
/// Scribe's batch endpoint names the detected language in ISO 639-3 ("deu"), the realtime endpoint
/// in ISO 639-1 ("de"). TypeWhisper uses the two-letter code where one exists.
/// </summary>
internal static class ElevenLabsLanguageCode
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> ThreeToTwo = new(() =>
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
        {
            var three = culture.ThreeLetterISOLanguageName;
            var two = culture.TwoLetterISOLanguageName;
            if (three.Length == 3 && two.Length == 2)
                map.TryAdd(three, two);
        }
        return map;
    });

    internal static string? AppCode(string? code)
    {
        var trimmed = code?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        if (trimmed.Length != 3)
            return trimmed;
        // Mandarin appears as "cmn", which has no two-letter code of its own.
        if (trimmed == "cmn")
            return "zh";
        return ThreeToTwo.Value.TryGetValue(trimmed, out var two) ? two : trimmed;
    }
}
