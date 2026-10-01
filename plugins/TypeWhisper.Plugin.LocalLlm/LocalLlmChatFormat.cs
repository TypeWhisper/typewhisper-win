using TypeWhisper.PluginSDK;

namespace TypeWhisper.Plugin.LocalLlm;

internal enum LocalLlmModelFamily { Gemma4, Qwen35, Lfm25 }

/// <summary>One prompt piece; only trusted template pieces may contain special tokens.</summary>
internal readonly record struct PromptSegment(string Text, bool IsTemplate);

internal static class LocalLlmChatFormat
{
    /// <summary>Extra output room for models that always reason before answering.</summary>
    internal const int ReasoningReserveTokens = 2048;

    // Generation prompts follow each model's published chat template with thinking disabled where possible.
    // LFM2.5 has no non-thinking mode, so its reasoning is removed from the answer instead.
    internal static IReadOnlyList<PromptSegment> Format(LocalLlmModelFamily family, string systemPrompt, string userText)
    {
        var (turn, end, system, user, answer) = family switch
        {
            LocalLlmModelFamily.Gemma4 => ("<|turn>", "<turn|>\n", "system\n", "user\n", "model\n"),
            LocalLlmModelFamily.Qwen35 => ("<|im_start|>", "<|im_end|>\n", "system\n", "user\n", "assistant\n<think>\n\n</think>\n\n"),
            LocalLlmModelFamily.Lfm25 => ("<|im_start|>", "<|im_end|>\n", "system\n", "user\n", "assistant\n<think>"),
            _ => throw new ArgumentOutOfRangeException(nameof(family)),
        };
        var segments = new List<PromptSegment>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            segments.Add(new(turn + system, true));
            segments.Add(new(systemPrompt.Trim(), false));
            segments.Add(new(end, true));
        }
        segments.Add(new(turn + user, true));
        segments.Add(new(userText.Trim(), false));
        segments.Add(new(end + turn + answer, true));
        return segments;
    }

    internal static IReadOnlyList<string> StopMarkers(LocalLlmModelFamily family) => family == LocalLlmModelFamily.Gemma4
        ? ["<turn|>", "<eos>"]
        : ["<|im_end|>", "<|endoftext|>"];

    internal static bool Reasons(LocalLlmModelFamily family) => family == LocalLlmModelFamily.Lfm25;

    internal static string ExtractAnswer(LocalLlmModelFamily family, string output)
    {
        if (family == LocalLlmModelFamily.Gemma4) return StripReasoning(output, "<|channel>", "<channel|>");
        if (Reasons(family) && !output.Contains("</think>", StringComparison.Ordinal))
            throw new PluginRequestException("The local model stopped before it finished reasoning. Try again or choose another model.",
                PluginRequestFailureKind.OutputTruncated, isTransient: false);
        return StripReasoning(output, "<think>", "</think>");
    }

    private static string StripReasoning(string text, string open, string close)
    {
        // Reasoning opened by the generation prompt ends at the first close marker without its own opener.
        var end = text.IndexOf(close, StringComparison.Ordinal);
        if (end >= 0 && text.IndexOf(open, 0, end, StringComparison.Ordinal) < 0)
            text = text[(end + close.Length)..];
        for (var start = text.IndexOf(open, StringComparison.Ordinal); start >= 0; start = text.IndexOf(open, StringComparison.Ordinal))
        {
            var stop = text.IndexOf(close, start + open.Length, StringComparison.Ordinal);
            text = stop < 0 ? text[..start] : text[..start] + text[(stop + close.Length)..];
        }
        return text.Trim();
    }
}
