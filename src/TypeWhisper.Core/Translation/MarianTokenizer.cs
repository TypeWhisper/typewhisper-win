using System.Text.Json;

namespace TypeWhisper.Core.Translation;

/// <summary>
/// Unigram (SentencePiece) tokenizer for Marian/Opus-MT models.
/// Parses tokenizer.json from HuggingFace transformers format.
/// </summary>
public sealed class MarianTokenizer
{
    private readonly Dictionary<string, (int Id, float Score)> _vocab;
    private readonly Dictionary<int, string> _idToToken;
    private readonly int _unkTokenId;
    private readonly int _eosTokenId;
    private const string MetaspacePrefix = "▁";

    private MarianTokenizer(
        Dictionary<string, (int Id, float Score)> vocab,
        Dictionary<int, string> idToToken,
        int unkTokenId,
        int eosTokenId)
    {
        _vocab = vocab;
        _idToToken = idToToken;
        _unkTokenId = unkTokenId;
        _eosTokenId = eosTokenId;
    }

    public static MarianTokenizer Load(string tokenizerJsonPath, int eosTokenId)
    {
        var json = File.ReadAllText(tokenizerJsonPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var vocab = new Dictionary<string, (int Id, float Score)>();
        var idToToken = new Dictionary<int, string>();

        // Parse model.vocab from tokenizer.json
        var model = root.GetProperty("model");
        var vocabArray = model.GetProperty("vocab");

        foreach (var entry in vocabArray.EnumerateArray())
        {
            var token = entry[0].GetString()!;
            var score = entry[1].GetSingle();
            var id = vocab.Count;
            vocab[token] = (id, score);
            idToToken[id] = token;
        }

        // Find unk token id
        var unkId = 0;
        if (model.TryGetProperty("unk_id", out var unkProp))
            unkId = unkProp.GetInt32();

        // Override with added_tokens if present (some tokenizer.json files define token→id mapping there)
        if (root.TryGetProperty("added_tokens", out var addedTokens))
        {
            foreach (var at in addedTokens.EnumerateArray())
            {
                var content = at.GetProperty("content").GetString()!;
                var id = at.GetProperty("id").GetInt32();
                if (!vocab.ContainsKey(content))
                {
                    vocab[content] = (id, 0f);
                    idToToken[id] = content;
                }
            }
        }

        return new MarianTokenizer(vocab, idToToken, unkId, eosTokenId);
    }

    /// <summary>
    /// Encode text to token IDs using Viterbi segmentation.
    /// Appends EOS token.
    /// </summary>
    public int[] Encode(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [_eosTokenId];

        var tokens = new List<int>();

        // Whitespace-split, then each word gets metaspace prefix
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var w = 0; w < words.Length; w++)
        {
            var word = MetaspacePrefix + words[w];
            var wordTokens = ViterbiSegment(word);
            tokens.AddRange(wordTokens);
        }

        tokens.Add(_eosTokenId);
        return tokens.ToArray();
    }

    /// <summary>
    /// Decode token IDs back to text. Replaces metaspace with space and trims.
    /// </summary>
    public string Decode(ReadOnlySpan<int> ids)
    {
        var parts = new List<string>();
        foreach (var id in ids)
        {
            if (id == _eosTokenId) break;
            if (_idToToken.TryGetValue(id, out var token))
                parts.Add(token);
        }

        var text = string.Join("", parts);
        text = text.Replace(MetaspacePrefix, " ");
        return text.Trim();
    }

    /// <summary>
    /// Viterbi algorithm for optimal Unigram segmentation.
    /// </summary>
    private List<int> ViterbiSegment(string word)
    {
        var n = word.Length;

        // best[i] = (score, tokenLength) for best segmentation ending at position i
        var bestScore = new float[n + 1];
        var bestLen = new int[n + 1];
        Array.Fill(bestScore, float.NegativeInfinity);
        bestScore[0] = 0;

        for (var end = 1; end <= n; end++)
        {
            // Try all substrings ending at 'end'
            var maxStart = Math.Max(0, end - 64); // practical limit on token length
            for (var start = maxStart; start < end; start++)
            {
                var substr = word[start..end];
                if (!_vocab.TryGetValue(substr, out var entry)) continue;
                var candidate = bestScore[start] + entry.Score;
                if (candidate > bestScore[end])
                {
                    bestScore[end] = candidate;
                    bestLen[end] = end - start;
                }
            }

            // If no token found ending at this position, fall back to single character as UNK
            if (bestScore[end] == float.NegativeInfinity)
            {
                bestScore[end] = bestScore[end - 1] + -100f; // large penalty
                bestLen[end] = 1;
            }
        }

        // Backtrack to find the tokens
        var result = new List<int>();
        var pos = n;
        while (pos > 0)
        {
            var len = bestLen[pos];
            var substr = word[(pos - len)..pos];
            if (_vocab.TryGetValue(substr, out var entry))
                result.Add(entry.Id);
            else
                result.Add(_unkTokenId);
            pos -= len;
        }

        result.Reverse();
        return result;
    }
}
