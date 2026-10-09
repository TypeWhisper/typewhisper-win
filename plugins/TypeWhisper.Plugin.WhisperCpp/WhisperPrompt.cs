using System.Text;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.WhisperCpp;

/// <summary>
/// Turns dictionary terms into Whisper's initial prompt within its 224-token window. Whisper.net exposes
/// no tokenizer, so the count is a conservative estimate: two ASCII characters per token, and one token
/// per UTF-8 byte otherwise, the most Whisper's byte-level tokenizer can use.
/// </summary>
internal static class WhisperPrompt
{
    internal const int MaxTokens = 224;

    /// <summary>Keeps whole terms in order while the estimate fits, or returns null without terms.</summary>
    internal static string? Create(string? prompt)
    {
        var kept = new StringBuilder();
        var tokens = 0d;
        foreach (var term in PluginDictionaryTerms.ParsePrompt(prompt))
        {
            var addition = (kept.Length == 0 ? "" : ", ") + term;
            var cost = EstimateTokens(addition);
            if (tokens + cost > MaxTokens) break;
            kept.Append(addition);
            tokens += cost;
        }
        return kept.Length == 0 ? null : kept.ToString();
    }

    internal static double EstimateTokens(string text)
    {
        var tokens = 0d;
        foreach (var rune in text.EnumerateRunes())
            tokens += rune.IsAscii ? 0.5 : rune.Utf8SequenceLength;
        return tokens;
    }
}
