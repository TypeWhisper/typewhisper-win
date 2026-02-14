namespace TypeWhisper.Core.Services;

public static class TextDiffService
{
    /// <summary>
    /// Returns true if the raw and final text differ (corrections/snippets were applied).
    /// </summary>
    public static bool HasChanges(string rawText, string finalText)
        => !string.Equals(rawText, finalText, StringComparison.Ordinal);

    /// <summary>
    /// Returns a simple list of word-level changes between raw and final text.
    /// </summary>
    public static IReadOnlyList<TextChange> GetChanges(string rawText, string finalText)
    {
        if (!HasChanges(rawText, finalText))
            return [];

        var rawWords = rawText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var finalWords = finalText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var changes = new List<TextChange>();
        var maxLen = Math.Max(rawWords.Length, finalWords.Length);

        for (var i = 0; i < maxLen; i++)
        {
            var raw = i < rawWords.Length ? rawWords[i] : null;
            var final_ = i < finalWords.Length ? finalWords[i] : null;

            if (raw != final_)
            {
                changes.Add(new TextChange(i, raw, final_));
            }
        }

        return changes;
    }
}

public sealed record TextChange(int Position, string? Original, string? Replacement);
