using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace TypeWhisper.Plugin.SupertonicTts;

internal sealed class SupertonicTextProcessor
{
    public static readonly ISet<string> SupportedLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "en", "ko", "ja", "ar", "bg", "cs", "da", "de", "el", "es", "et", "fi", "fr", "hi",
        "hr", "hu", "id", "it", "lt", "lv", "nl", "pl", "pt", "ro", "ru", "sk", "sl",
        "sv", "tr", "uk", "vi"
    };

    private readonly long[] _indexer;

    public SupertonicTextProcessor(string unicodeIndexerPath)
    {
        var json = File.ReadAllText(unicodeIndexerPath);
        _indexer = JsonSerializer.Deserialize<long[]>(json)
            ?? throw new InvalidOperationException("Failed to load Supertonic unicode indexer.");
    }

    public SupertonicTextFeatures Process(IReadOnlyList<string> texts, IReadOnlyList<string> languages)
    {
        if (texts.Count != languages.Count)
            throw new ArgumentException("Text and language counts must match.");

        var processed = texts.Select((text, index) => PreprocessText(text, languages[index])).ToArray();
        var maxLength = Math.Max(1, processed.Max(text => text.Length));
        var ids = new long[processed.Length * maxLength];
        var mask = new float[processed.Length * maxLength];

        for (var batch = 0; batch < processed.Length; batch++)
        {
            var text = processed[batch];
            for (var i = 0; i < text.Length; i++)
            {
                var codeUnit = text[i];
                ids[batch * maxLength + i] = codeUnit < _indexer.Length ? _indexer[codeUnit] : 0;
                mask[batch * maxLength + i] = 1.0f;
            }
        }

        return new SupertonicTextFeatures(
            new DenseTensor<long>(ids, new[] { processed.Length, maxLength }),
            new DenseTensor<float>(mask, new[] { processed.Length, 1, maxLength }));
    }

    private static string PreprocessText(string text, string language)
    {
        if (!SupportedLanguages.Contains(language))
            throw new ArgumentException($"Unsupported Supertonic language: {language}");

        text = text.Normalize(NormalizationForm.FormKD);
        text = RemoveEmojiCodePoints(text);

        foreach (var (from, to) in Replacements)
            text = text.Replace(from, to);

        text = Regex.Replace(text, @"[♥☆♡©\\]", "");
        text = text.Replace("@", " at ", StringComparison.Ordinal);
        text = text.Replace("e.g.,", "for example, ", StringComparison.OrdinalIgnoreCase);
        text = text.Replace("i.e.,", "that is, ", StringComparison.OrdinalIgnoreCase);

        text = Regex.Replace(text, @"\s+([,.!?;:'])", "$1");
        while (text.Contains("\"\"", StringComparison.Ordinal))
            text = text.Replace("\"\"", "\"", StringComparison.Ordinal);
        while (text.Contains("''", StringComparison.Ordinal))
            text = text.Replace("''", "'", StringComparison.Ordinal);
        while (text.Contains("``", StringComparison.Ordinal))
            text = text.Replace("``", "`", StringComparison.Ordinal);

        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length == 0)
            text = ".";
        if (!Regex.IsMatch(text, "[.!?;:,'\"')\\]}…。」』〗〉》›»]$"))
            text += ".";

        return $"<{language}>{text}";
    }

    private static string RemoveEmojiCodePoints(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            int codePoint;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                codePoint = char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }
            else
            {
                codePoint = text[i];
            }

            if (IsEmoji(codePoint))
                continue;

            builder.Append(char.ConvertFromUtf32(codePoint));
        }

        return builder.ToString();
    }

    private static bool IsEmoji(int codePoint) =>
        codePoint is >= 0x1F600 and <= 0x1F64F
        || codePoint is >= 0x1F300 and <= 0x1F5FF
        || codePoint is >= 0x1F680 and <= 0x1F6FF
        || codePoint is >= 0x1F700 and <= 0x1F77F
        || codePoint is >= 0x1F780 and <= 0x1F7FF
        || codePoint is >= 0x1F800 and <= 0x1F8FF
        || codePoint is >= 0x1F900 and <= 0x1F9FF
        || codePoint is >= 0x1FA00 and <= 0x1FA6F
        || codePoint is >= 0x1FA70 and <= 0x1FAFF
        || codePoint is >= 0x2600 and <= 0x26FF
        || codePoint is >= 0x2700 and <= 0x27BF
        || codePoint is >= 0x1F1E6 and <= 0x1F1FF;

    private static IReadOnlyList<(string From, string To)> Replacements { get; } =
    [
        ("–", "-"),
        ("‑", "-"),
        ("—", "-"),
        ("_", " "),
        ("\u201C", "\""),
        ("\u201D", "\""),
        ("\u2018", "'"),
        ("\u2019", "'"),
        ("´", "'"),
        ("`", "'"),
        ("[", " "),
        ("]", " "),
        ("|", " "),
        ("/", " "),
        ("#", " "),
        ("→", " "),
        ("←", " "),
    ];
}

internal sealed record SupertonicTextFeatures(DenseTensor<long> TextIds, DenseTensor<float> TextMask);
