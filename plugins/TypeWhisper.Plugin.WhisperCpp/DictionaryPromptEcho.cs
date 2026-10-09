namespace TypeWhisper.Plugin.WhisperCpp;

/// <summary>
/// Recognizes transcripts that may only repeat the dictionary prompt. Without speech, Whisper often
/// returns the prompt terms themselves or misspelled pieces of them, such as "Kwek" for "Kwyjibo".
/// </summary>
internal static class DictionaryPromptEcho
{
    /// <summary>Whether every word of the transcript matches a prompt word or starts with the same two letters.</summary>
    internal static bool IsSuspected(string? text, string prompt)
    {
        var words = Words(text);
        if (words.Length == 0) return false;
        var promptWords = Words(prompt);
        return words.All(word => promptWords.Any(promptWord => Resembles(word, promptWord)));
    }

    /// <summary>Whether the transcript contains anything besides punctuation and whitespace.</summary>
    internal static bool HasWords(string? text) => Words(text).Length > 0;

    private static bool Resembles(string word, string promptWord) =>
        string.Equals(word, promptWord, StringComparison.OrdinalIgnoreCase)
        || word.Length >= 2 && promptWord.Length >= 2 && string.Compare(word, 0, promptWord, 0, 2, StringComparison.OrdinalIgnoreCase) == 0;

    private static string[] Words(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var separators = text.Where(character => !char.IsLetterOrDigit(character)).Distinct().ToArray();
        return text.Split(separators, StringSplitOptions.RemoveEmptyEntries);
    }
}
