using System.Text.RegularExpressions;

namespace TypeWhisper.WinUI;

internal static class DictionaryTrainingPlan
{
    internal static bool IsWord(string word) => word.Length is > 0 and <= 160 &&
        Regex.IsMatch(word, @"^[\p{L}\p{N}][\p{L}\p{M}\p{N}'’-]*$") &&
        char.IsLetterOrDigit(word[^1]);

    internal static string[] Sentences(string word, bool german) => german
        ? [$"Heute möchte ich {word} deutlich sagen.", $"Bitte schreibe {word} in diesen Satz.", $"Das wichtige Wort ist heute {word}."]
        : [$"Today I want to say {word} clearly.", $"Please write {word} in this sentence.", $"The important word today is {word}."];

    private static string[] Tokens(string text) =>
        Regex.Matches(text.Normalize(), @"[\p{L}\p{N}][\p{L}\p{M}\p{N}'’-]*").Select(match => match.Value).ToArray();

    // Accept only one changed token, exactly where the requested word occurs.
    internal static string? Candidate(string word, string expected, string actual)
    {
        var left = Tokens(expected); var right = Tokens(actual);
        if (left.Length != right.Length || left.Count(token => token.Equals(word, StringComparison.OrdinalIgnoreCase)) != 1) return null;
        var changed = Enumerable.Range(0, left.Length)
            .Where(i => !left[i].Equals(right[i], StringComparison.OrdinalIgnoreCase)).ToArray();
        return changed.Length == 1 && left[changed[0]].Equals(word, StringComparison.OrdinalIgnoreCase) && IsWord(right[changed[0]])
            ? right[changed[0]] : null;
    }

    internal static bool Matches(string expected, string actual) =>
        Tokens(expected).SequenceEqual(Tokens(actual), StringComparer.OrdinalIgnoreCase);
}
