using System.Text.Json;

namespace TypeWhisper.Core.Translation;

public sealed record MarianConfig(
    int DecoderStartTokenId,
    int EosTokenId,
    int VocabSize,
    int MaxLength)
{
    public static MarianConfig Load(string configJsonPath)
    {
        var json = File.ReadAllText(configJsonPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return new MarianConfig(
            DecoderStartTokenId: root.GetProperty("decoder_start_token_id").GetInt32(),
            EosTokenId: root.TryGetProperty("eos_token_id", out var eos) ? eos.GetInt32() : 0,
            VocabSize: root.TryGetProperty("vocab_size", out var vocab) ? vocab.GetInt32() : 65536,
            MaxLength: root.TryGetProperty("max_length", out var maxLen) ? maxLen.GetInt32() : 512
        );
    }
}
