using System.IO;
using System.Text.Json;
using Microsoft.ML.Tokenizers;

namespace TypeWhisper.Plugin.Qwen3Stt;

internal sealed class QwenTokenizer
{
    public const int EndOfTextTokenId = 151643;
    public const int ImStartTokenId = 151644;
    public const int ImEndTokenId = 151645;
    public const int AudioStartTokenId = 151669;
    public const int AudioEndTokenId = 151670;
    public const int AudioPadTokenId = 151676;

    private readonly BpeTokenizer _bpe;
    private readonly IReadOnlyDictionary<string, int> _specialTokenIds;
    private readonly HashSet<int> _specialIds;

    private QwenTokenizer(BpeTokenizer bpe, IReadOnlyDictionary<string, int> specialTokenIds)
    {
        _bpe = bpe;
        _specialTokenIds = specialTokenIds;
        _specialIds = specialTokenIds.Values.ToHashSet();
    }

    public static QwenTokenizer Load(string modelDirectory)
    {
        var tokenizerJsonPath = Path.Combine(modelDirectory, "tokenizer.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(tokenizerJsonPath));
        var root = doc.RootElement;
        var model = root.GetProperty("model");

        var vocab = ParseVocabulary(model.GetProperty("vocab"));
        var merges = ParseMerges(model.GetProperty("merges"));
        var special = ParseSpecialTokens(root, vocab);

        var vocabPath = Path.Combine(modelDirectory, ".typewhisper-qwen-vocab.json");
        var mergesPath = Path.Combine(modelDirectory, ".typewhisper-qwen-merges.txt");
        WriteTokenizerFiles(vocabPath, mergesPath, vocab, merges);

        var bpe = BpeTokenizer.Create(new BpeOptions(vocabPath, mergesPath)
        {
            ByteLevel = true,
            SpecialTokens = special,
        });
        return new QwenTokenizer(bpe, special);
    }

    public IReadOnlyList<int> EncodePrompt(string text)
    {
        var ids = new List<int>();
        var index = 0;
        while (index < text.Length)
        {
            var next = FindNextSpecialToken(text, index);
            if (next is null)
            {
                ids.AddRange(_bpe.EncodeToIds(text[index..]));
                break;
            }

            var (token, position) = next.Value;
            if (position > index)
                ids.AddRange(_bpe.EncodeToIds(text[index..position]));

            ids.Add(_specialTokenIds[token]);
            index = position + token.Length;
        }

        return ids;
    }

    public string Decode(IEnumerable<int> ids)
    {
        var filtered = ids.Where(id => !_specialIds.Contains(id)).ToArray();
        return filtered.Length == 0 ? string.Empty : _bpe.Decode(filtered);
    }

    private (string Token, int Position)? FindNextSpecialToken(string text, int start)
    {
        string? bestToken = null;
        var bestPosition = int.MaxValue;

        foreach (var token in _specialTokenIds.Keys)
        {
            var position = text.IndexOf(token, start, StringComparison.Ordinal);
            if (position >= 0 && position < bestPosition)
            {
                bestToken = token;
                bestPosition = position;
            }
        }

        return bestToken is null ? null : (bestToken, bestPosition);
    }

    private static Dictionary<string, int> ParseVocabulary(JsonElement vocabElement)
    {
        var vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var prop in vocabElement.EnumerateObject())
            vocab[prop.Name] = prop.Value.GetInt32();
        return vocab;
    }

    private static IReadOnlyList<string> ParseMerges(JsonElement mergesElement)
    {
        var merges = new List<string>();
        foreach (var merge in mergesElement.EnumerateArray())
        {
            if (merge.ValueKind == JsonValueKind.String)
            {
                merges.Add(merge.GetString() ?? "");
            }
            else if (merge.ValueKind == JsonValueKind.Array && merge.GetArrayLength() == 2)
            {
                merges.Add($"{merge[0].GetString()} {merge[1].GetString()}");
            }
        }
        return merges;
    }

    private static Dictionary<string, int> ParseSpecialTokens(JsonElement root, Dictionary<string, int> vocab)
    {
        var special = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["<|im_start|>"] = ImStartTokenId,
            ["<|im_end|>"] = ImEndTokenId,
            ["<|audio_start|>"] = AudioStartTokenId,
            ["<|audio_end|>"] = AudioEndTokenId,
            ["<|audio_pad|>"] = AudioPadTokenId,
            ["<|endoftext|>"] = EndOfTextTokenId,
            ["<asr_text>"] = 151704,
        };

        if (root.TryGetProperty("added_tokens", out var addedTokens))
        {
            foreach (var token in addedTokens.EnumerateArray())
            {
                if (!token.TryGetProperty("content", out var contentEl)
                    || !token.TryGetProperty("id", out var idEl))
                {
                    continue;
                }

                var content = contentEl.GetString();
                if (!string.IsNullOrEmpty(content))
                {
                    special[content] = idEl.GetInt32();
                    vocab[content] = idEl.GetInt32();
                }
            }
        }

        return special;
    }

    private static void WriteTokenizerFiles(
        string vocabPath,
        string mergesPath,
        Dictionary<string, int> vocab,
        IReadOnlyList<string> merges)
    {
        if (!File.Exists(vocabPath))
        {
            var ordered = vocab
                .OrderBy(pair => pair.Value)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var json = JsonSerializer.Serialize(ordered);
            File.WriteAllText(vocabPath, json);
        }

        if (!File.Exists(mergesPath))
        {
            File.WriteAllLines(mergesPath, ["#version: 0.2", .. merges]);
        }
    }
}
