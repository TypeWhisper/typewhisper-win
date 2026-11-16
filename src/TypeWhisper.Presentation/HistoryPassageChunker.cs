using System.Globalization;

namespace TypeWhisper.Presentation;

/// <summary>What a passage of the History search index holds.</summary>
public enum HistoryPassageKind
{
    /// <summary>What was said.</summary>
    Transcript,
    /// <summary>A text made from the recording, such as an email, a translation or notes.</summary>
    Result
}

/// <summary>A stretch of a recording that the search index stores, finds and cites as one unit.</summary>
/// <param name="Kind">What the passage holds.</param>
/// <param name="Text">The passage's text.</param>
/// <param name="Speakers">The speakers in the passage, in order of appearance; empty without speaker detection.</param>
/// <param name="StartSeconds">Where the passage starts in the audio, or null when unknown.</param>
/// <param name="EndSeconds">Where the passage ends in the audio, or null when unknown.</param>
/// <param name="TextStart">The passage's first UTF-16 unit in its source text, or null when unknown.</param>
/// <param name="TextEnd">The UTF-16 unit after the passage in its source text, or null when unknown.</param>
public sealed record HistoryPassage(
    HistoryPassageKind Kind,
    string Text,
    IReadOnlyList<string> Speakers,
    double? StartSeconds = null,
    double? EndSeconds = null,
    int? TextStart = null,
    int? TextEnd = null)
{
    /// <inheritdoc />
    public bool Equals(HistoryPassage? other) => other is not null && Kind == other.Kind && Text == other.Text
        && Speakers.SequenceEqual(other.Speakers, StringComparer.Ordinal) && StartSeconds == other.StartSeconds
        && EndSeconds == other.EndSeconds && TextStart == other.TextStart && TextEnd == other.TextEnd;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Kind, Text, Speakers.Count, StartSeconds, EndSeconds, TextStart, TextEnd);
}

/// <summary>Cuts History records into passages for the search index. Lengths are UTF-16 units.</summary>
public static class HistoryPassageChunker
{
    /// <summary>
    /// Stored with every indexed record. Raise it when an unchanged record would get other passages, so the index is
    /// built again.
    /// </summary>
    public const int Version = 1;

    /// <summary>Full-text ranking prefers short passages, so a last passage shorter than this takes text from the one before it.</summary>
    public const int MinimumLength = 500;

    /// <summary>The longest passage.</summary>
    public const int MaximumLength = 900;

    // Characters that end a sentence once whitespace follows; the last three end one at once.
    private const string SentenceEnds = ".!?…。！？";
    private const string ImmediateSentenceEnds = "。！？";
    // Closing quotes and brackets that belong to the sentence they end.
    private const string Closers = "\"'”’)]»›」』";

    /// <summary>Passages of a text, cut at sentence ends where possible.</summary>
    public static IReadOnlyList<HistoryPassage> Passages(string text, HistoryPassageKind kind = HistoryPassageKind.Transcript)
    {
        ArgumentNullException.ThrowIfNull(text);
        var passages = new List<HistoryPassage>();
        foreach (var (start, end) in Ranges(text, MaximumLength))
        {
            var (first, last) = Trimmed(text, start, end);
            if (last > first) passages.Add(new(kind, text[first..last], [], TextStart: first, TextEnd: last));
        }
        return passages;
    }

    // Ranges of at most `limit` units that together cover the text, each ending at a sentence end where possible.
    private static List<(int Start, int End)> Ranges(string text, int limit)
    {
        var pieces = new List<(int Start, int End)>();
        foreach (var (start, end) in Sentences(text)) pieces.AddRange(Split(text, start, end, limit));
        return Groups(pieces.Select(piece => piece.End - piece.Start).ToArray(), 0, limit)
            .Select(group => (pieces[group.Start].Start, pieces[group.End - 1].End)).ToList();
    }

    // Sentences with the whitespace that follows them, covering the whole text. A line break also ends a sentence.
    internal static IEnumerable<(int Start, int End)> Sentences(string text)
    {
        var start = 0;
        var index = 0;
        while (index < text.Length)
        {
            var character = text[index];
            var ends = false;
            if (character is '\n' or '\r') ends = true;
            else if (SentenceEnds.Contains(character))
            {
                var after = index + 1;
                while (after < text.Length && (SentenceEnds.Contains(text[after]) || Closers.Contains(text[after]))) after++;
                if (ImmediateSentenceEnds.Contains(character) || after == text.Length || char.IsWhiteSpace(text[after]))
                {
                    index = after - 1;
                    ends = true;
                }
            }
            index++;
            if (!ends) continue;
            while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
            yield return (start, index);
            start = index;
        }
        if (start < text.Length) yield return (start, text.Length);
    }

    // Breaks a sentence longer than the limit after whitespace, or inside a word that is longer than the limit itself.
    private static List<(int Start, int End)> Split(string text, int start, int end, int limit)
    {
        var pieces = new List<(int Start, int End)>();
        var location = start;
        while (end - location > limit)
        {
            var cut = -1;
            for (var index = location + limit - 1; index >= location; index--)
                if (char.IsWhiteSpace(text[index])) { cut = index + 1; break; }
            if (cut <= location) cut = TextElementStart(text, location, location + limit);
            pieces.Add((location, cut));
            location = cut;
        }
        if (end > location) pieces.Add((location, end));
        return pieces;
    }

    // The start of the text element (such as an emoji or a letter with its accents) that holds `position`, so a cut
    // never splits one; `position` itself when that element starts at `from`.
    private static int TextElementStart(string text, int from, int position)
    {
        var element = from;
        while (element < text.Length)
        {
            var next = element + StringInfo.GetNextTextElementLength(text, element);
            if (next > position) break;
            element = next;
        }
        return element <= from ? position : element;
    }

    /// <summary>Groups consecutive units into passages of at most <paramref name="limit"/>, as index ranges.</summary>
    internal static List<(int Start, int End)> Groups(IReadOnlyList<int> lengths, int separatorLength, int limit)
    {
        int Length(int start, int end)
        {
            var sum = 0;
            for (var index = start; index < end; index++) sum += lengths[index];
            return sum + separatorLength * Math.Max(end - start - 1, 0);
        }

        var groups = new List<(int Start, int End)>();
        var first = 0;
        var current = 0;
        for (var index = 0; index < lengths.Count; index++)
        {
            var joined = index == first ? lengths[index] : current + separatorLength + lengths[index];
            if (index > first && joined > limit)
            {
                groups.Add((first, index));
                first = index;
                current = lengths[index];
            }
            else current = joined;
        }
        if (first < lengths.Count) groups.Add((first, lengths.Count));

        if (groups.Count < 2 || Length(groups[^1].Start, groups[^1].End) >= MinimumLength) return groups;
        // Move the border between the last two groups back while that makes them more equal.
        var previous = groups[^2];
        var last = groups[^1];
        var border = last.Start;
        while (border - 1 > previous.Start)
        {
            var moved = Length(border - 1, last.End);
            var difference = Math.Abs(Length(previous.Start, border) - Length(border, last.End));
            var movedDifference = Math.Abs(Length(previous.Start, border - 1) - moved);
            if (moved > limit || movedDifference >= difference) break;
            border--;
        }
        groups[^2] = (previous.Start, border);
        groups[^1] = (border, last.End);
        return groups;
    }

    private static (int Start, int End) Trimmed(string text, int start, int end)
    {
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        return (start, end);
    }
}
