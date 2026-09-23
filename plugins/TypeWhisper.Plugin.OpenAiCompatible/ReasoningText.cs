using System.Text.RegularExpressions;

namespace TypeWhisper.Plugin.OpenAiCompatible;

// Some servers (Ollama, LM Studio, vLLM without a reasoning parser) return a model's
// reasoning inline in content instead of reasoning_content (#342). Qwen3/DeepSeek chat
// templates may also open the block in the prompt, so only the closing tag is visible.
internal static class ReasoningText
{
    private static readonly (string Open, string Close)[] Blocks = [("<thinking>", "</thinking>"), ("<think>", "</think>")];
    // Template-opened reasoning ends with the tag on its own line; "Use </think> to close" is answer text.
    private static readonly Regex ImplicitClose = new(@"</think(?:ing)?>[ \t]*(?:\r?\n|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static string StripLeading(string text)
    {
        var remaining = text.TrimStart();
        var stripped = false;
        // Remove consecutive leading blocks so reasoning-only output is reported as empty.
        while (Blocks.FirstOrDefault(block => remaining.StartsWith(block.Open, StringComparison.OrdinalIgnoreCase)) is { Open: not null } block)
        {
            var end = remaining.IndexOf(block.Close, block.Open.Length, StringComparison.OrdinalIgnoreCase);
            if (end < 0) return stripped ? remaining : text; // Truncated reasoning: keep it visible rather than guess.
            remaining = remaining[(end + block.Close.Length)..].TrimStart();
            stripped = true;
        }
        if (stripped) return remaining;
        if (Blocks.Any(block => remaining.Contains(block.Open, StringComparison.OrdinalIgnoreCase)) ||
            ImplicitClose.Match(remaining) is not { Success: true } close) return text;
        return StripLeading(remaining[(close.Index + close.Length)..].Trim());
    }
}
