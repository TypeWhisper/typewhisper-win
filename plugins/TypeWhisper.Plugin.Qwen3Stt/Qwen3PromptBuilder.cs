namespace TypeWhisper.Plugin.Qwen3Stt;

internal static class Qwen3PromptBuilder
{
    public static Qwen3Prompt Build(string context, string? languageName, int audioTokenCount)
    {
        var audioPads = string.Concat(Enumerable.Repeat("<|audio_pad|>", Math.Max(1, audioTokenCount)));
        var prompt =
            "<|im_start|>system\n" +
            context +
            "<|im_end|>\n" +
            "<|im_start|>user\n" +
            "<|audio_start|>" +
            audioPads +
            "<|audio_end|><|im_end|>\n" +
            "<|im_start|>assistant\n";

        if (!string.IsNullOrWhiteSpace(languageName))
            prompt += $"language {languageName}<asr_text>";

        var audioOffset = prompt.IndexOf("<|audio_pad|>", StringComparison.Ordinal);
        return new Qwen3Prompt(prompt, audioOffset < 0 ? 0 : audioOffset);
    }
}

internal sealed record Qwen3Prompt(string Text, int AudioOffset);
