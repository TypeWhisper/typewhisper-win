using System.Text;
using TypeWhisper.PluginSDK.Models;

namespace TypeWhisper.Plugin.WhisperCpp;

/// <summary>
/// Turns dictionary terms into Whisper's initial prompt within its 224-token window. Whisper.net exposes
/// no tokenizer, so the prompt is limited to 224 UTF-8 bytes: every token of Whisper's byte-level
/// tokenizer covers at least one byte, so the window holds whatever the terms contain.
/// </summary>
internal static class WhisperPrompt
{
    internal const int MaxTokens = 224;

    /// <summary>Keeps whole terms in order while they fit, or returns null without terms.</summary>
    internal static string? Create(string? prompt)
    {
        var kept = new StringBuilder();
        var bytes = 0;
        foreach (var term in PluginDictionaryTerms.ParsePrompt(prompt))
        {
            var addition = (kept.Length == 0 ? "" : ", ") + term;
            var cost = Encoding.UTF8.GetByteCount(addition);
            if (bytes + cost > MaxTokens) break;
            kept.Append(addition);
            bytes += cost;
        }
        return kept.Length == 0 ? null : kept.ToString();
    }
}
