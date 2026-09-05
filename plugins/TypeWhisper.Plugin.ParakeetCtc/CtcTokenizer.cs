using System.Text;
using System.Text.Json;

namespace TypeWhisper.Plugin.ParakeetCtc;

// Merge ordering and lowercase/NFKC preprocessing follow FluidAudio 0.15.5.
// Unknown characters fail closed rather than fabricating acoustic evidence.
public sealed class CtcTokenizer
{
    private readonly Dictionary<string, int> _vocabulary;
    private readonly Dictionary<(string, string), int> _ranks = [];
    public int BlankId { get; }
    public CtcTokenizer(string path)
    {
        var entries = File.ReadAllLines(path).Select(line =>
        {
            var split = line.LastIndexOf(' ');
            return (Text: line[..split], Id: int.Parse(line[(split + 1)..], System.Globalization.CultureInfo.InvariantCulture));
        }).ToArray();
        BlankId = entries.Single(e => e.Text is "<blk>" or "<eps>").Id;
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(path)!, "tokenizer.json")));
        var model = json.RootElement.GetProperty("model");
        if (model.GetProperty("type").GetString() != "BPE") throw new InvalidDataException("Expected BPE tokenizer.");
        _vocabulary = model.GetProperty("vocab").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32(), StringComparer.Ordinal);
        var exported = entries.ToDictionary(e => e.Text, e => e.Id, StringComparer.Ordinal);
        if (_vocabulary.Any(p => !exported.TryGetValue(p.Key, out var id) || id != p.Value))
            throw new InvalidDataException("Tokenizer IDs do not match the acoustic model vocabulary.");
        var rank = 0;
        foreach (var merge in model.GetProperty("merges").EnumerateArray())
        {
            var pair = merge.GetString()!.Split(' ', 2);
            if (pair.Length != 2) throw new InvalidDataException("Invalid BPE merge.");
            _ranks.TryAdd((pair[0], pair[1]), rank++);
        }
    }

    public int[] Encode(string text, bool boundary = true)
    {
        if (text.Length == 0 || text.Length > 160) return [];
        var input = (boundary ? "▁" : "") + text.ToLowerInvariant().Normalize(NormalizationForm.FormKC).Replace(' ', '▁');
        var pieces = input.EnumerateRunes().Select(r => r.ToString()).ToList();
        while (pieces.Count > 1)
        {
            var best = int.MaxValue; (string Left, string Right) pair = default;
            for (var i = 0; i < pieces.Count - 1; i++)
                if (_ranks.TryGetValue((pieces[i], pieces[i + 1]), out var rank) && rank < best)
                { best = rank; pair = (pieces[i], pieces[i + 1]); }
            if (best == int.MaxValue) break;
            var merged = new List<string>();
            for (var i = 0; i < pieces.Count; i++)
                if (i + 1 < pieces.Count && pieces[i] == pair.Left && pieces[i + 1] == pair.Right)
                { merged.Add(pair.Left + pair.Right); i++; }
                else merged.Add(pieces[i]);
            pieces = merged;
        }
        if (pieces.Any(p => !_vocabulary.ContainsKey(p))) return [];
        return pieces.Select(p => _vocabulary[p]).ToArray();
    }
}
