namespace TypeWhisper.Plugin.Qwen3Stt;

public static class Qwen3ContextBiasFormatter
{
    public const string BaseInstruction =
        "Transcribe only words that are spoken in the audio. Do not append acknowledgements, continuations, or filler words after speech ends.";

    public static string Format(string? prompt)
    {
        var terms = ExtractTerms(prompt);
        return terms.Count == 0
            ? BaseInstruction
            : $"{BaseInstruction}\nTechnical terms: {string.Join(", ", terms)}.";
    }

    private static IReadOnlyList<string> ExtractTerms(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var part in prompt.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var term = part.Trim();
            if (term.Length == 0
                || term.Length > 80
                || term.IndexOfAny(['<', '>', '{', '}', '[', ']', '\r', '\n']) >= 0
                || !seen.Add(term))
            {
                continue;
            }

            result.Add(term);
            if (result.Count == 200)
                break;
        }

        return result;
    }
}
