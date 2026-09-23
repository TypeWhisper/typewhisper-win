namespace TypeWhisper.Plugin.OpenAiCompatible;

// Some servers (Ollama, LM Studio, vLLM without a reasoning parser) return a model's
// reasoning inline in content instead of reasoning_content (#342). Qwen3/DeepSeek chat
// templates may also open the block in the prompt, so only the closing tag is visible.
internal static class ReasoningText
{
    private static readonly (string Open, string Close)[] Blocks = [("<think>", "</think>"), ("<thinking>", "</thinking>")];

    internal static string StripLeading(string text)
    {
        var trimmed = text.TrimStart();
        foreach (var (open, close) in Blocks)
        {
            var end = trimmed.IndexOf(close, StringComparison.OrdinalIgnoreCase);
            if (end < 0) continue;
            var explicitOpen = trimmed.StartsWith(open, StringComparison.OrdinalIgnoreCase);
            // A closing tag after ordinary answer text is content, not a leading reasoning block.
            if (!explicitOpen && trimmed.IndexOf(open, 0, end, StringComparison.OrdinalIgnoreCase) >= 0) continue;
            return trimmed[(end + close.Length)..].Trim();
        }
        return text;
    }
}
