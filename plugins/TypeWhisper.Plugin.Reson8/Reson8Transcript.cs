using System.Text.Json;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.Reson8;

/// <summary>One word with its time in seconds, as Reson8 returns it with <c>include_words</c>.</summary>
internal sealed record Reson8Word(string Text, double Start, double End);

/// <summary>Reads Reson8 transcripts and splits their words into subtitle segments like the macOS plugin.</summary>
internal static class Reson8Transcript
{
    /// <summary>A silence longer than this starts a new segment.</summary>
    internal const double PauseThreshold = 1.0;
    /// <summary>A sentence end splits only after this many words, so "Yes." joins the next sentence.</summary>
    internal const int MinimumSentenceWordCount = 3;
    /// <summary>Without a sentence end or pause, a segment is split at the next word after this length.</summary>
    internal const double MaximumSegmentDuration = 30;

    private static readonly char[] SentenceEnds = ['.', '!', '?', '…', '。', '！', '？', '؟', '।'];
    private static readonly char[] TrailingClosers = ['"', '\'', '”', '’', '»', '«', ')', ']', '」', '』'];

    internal static PluginTranscriptionResult Parse(string json, string? requestedLanguage, double durationSeconds)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var words = Words(root);
        return new PluginTranscriptionResult(
            (GetString(root, "text") ?? "").Trim(),
            DetectedLanguage(GetString(root, "language") ?? GetString(root, "detected_language"), requestedLanguage),
            durationSeconds,
            NoSpeechProbability: null)
        {
            Segments = SentenceSegments(words),
            TokenTimings = words.Select(word => new VocabularyTokenTiming(word.Text, word.Start, word.End)).ToArray(),
        };
    }

    /// <summary>
    /// The language Reson8 detected, or the language the request pinned. Rumble-2-Flash reports an empty language
    /// for a list of languages, and a list names no single language.
    /// </summary>
    internal static string? DetectedLanguage(string? detected, string? requested)
    {
        if (!string.IsNullOrWhiteSpace(detected))
            return detected.Trim();
        return string.IsNullOrWhiteSpace(requested) || requested.Contains(',') ? null : requested.Trim();
    }

    internal static IReadOnlyList<Reson8Word> Words(JsonElement element)
    {
        if (!element.TryGetProperty("words", out var words) || words.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<Reson8Word>();
        foreach (var word in words.EnumerateArray())
        {
            if (word.ValueKind != JsonValueKind.Object || GetString(word, "text") is not { Length: > 0 } text
                || !TryGetDouble(word, "start_ms", out var start) || !TryGetDouble(word, "duration_ms", out var duration)
                || start < 0 || duration < 0)
                continue;
            result.Add(new Reson8Word(text, start / 1000.0, (start + duration) / 1000.0));
        }

        return result;
    }

    /// <summary>
    /// Splits words into subtitle-sized segments. Reson8 returns words with their punctuation and only for
    /// Resonant-1, whose languages separate words with spaces, so the words are joined with one.
    /// </summary>
    internal static IReadOnlyList<PluginTranscriptionSegment> SentenceSegments(IReadOnlyList<Reson8Word> words)
    {
        var segments = new List<PluginTranscriptionSegment>();
        var current = new List<Reson8Word>();

        void Close()
        {
            if (current.Count == 0)
                return;
            segments.Add(new PluginTranscriptionSegment(
                string.Join(" ", current.Select(word => word.Text.Trim())), current[0].Start, Math.Max(current[0].Start, current[^1].End)));
            current.Clear();
        }

        foreach (var word in words)
        {
            if (current.Count > 0
                && (word.Start - current[^1].End > PauseThreshold || word.End - current[0].Start > MaximumSegmentDuration))
                Close();
            current.Add(word);
            if (current.Count >= MinimumSentenceWordCount && EndsSentence(word.Text))
                Close();
        }

        Close();
        return segments;
    }

    private static bool EndsSentence(string word)
    {
        var trimmed = word.TrimEnd().TrimEnd(TrailingClosers);
        return trimmed.Length > 0 && SentenceEnds.Contains(trimmed[^1]);
    }

    internal static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryGetDouble(JsonElement element, string name, out double value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out value))
            return true;
        value = 0;
        return false;
    }
}
