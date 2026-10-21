using System.Text;
using System.Text.RegularExpressions;

namespace TypeWhisper.Plugin.GigaAm;

/// <summary>SentencePiece pieces with "▁" marking the start of a word, one "&lt;piece&gt; &lt;id&gt;" per line.</summary>
internal sealed partial class GigaAmVocabulary
{
    private const char WordStart = '▁';
    private readonly Dictionary<int, string> _pieces;

    private GigaAmVocabulary(Dictionary<int, string> pieces, int blankId)
    {
        _pieces = pieces;
        BlankId = blankId;
    }

    public int BlankId { get; }

    public static GigaAmVocabulary Load(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    public static GigaAmVocabulary Parse(string text)
    {
        var pieces = new Dictionary<int, string>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;
            // A piece contains no spaces, so the id follows the last one.
            var separator = line.LastIndexOf(' ');
            if (separator <= 0 || !int.TryParse(line.AsSpan(separator + 1), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var id) || !pieces.TryAdd(id, line[..separator]))
                throw new InvalidDataException("The GigaAM vocabulary file is invalid.");
        }
        var blank = pieces.Where(piece => piece.Value == "<blk>").Select(piece => (int?)piece.Key).FirstOrDefault()
            ?? throw new InvalidDataException("The GigaAM vocabulary file is invalid.");
        return new(pieces, blank);
    }

    /// <summary>
    /// Joins the pieces like SentencePiece. onnx-asr's own detokenizer drops the space before dashes and quotes
    /// ("Европа— …"); this keeps it ("Европа — …"), as the macOS plugin does.
    /// </summary>
    public string Text(IEnumerable<int> tokens)
    {
        var joined = new StringBuilder();
        foreach (var token in tokens)
            if (_pieces.TryGetValue(token, out var piece)) joined.Append(piece);
        return NormalizeSpacing(joined.Replace(WordStart, ' ').ToString());
    }

    /// <summary>Collapses runs of spaces and removes spaces before closing and after opening punctuation.</summary>
    internal static string NormalizeSpacing(string text)
    {
        var result = Whitespace().Replace(text, " ");
        result = SpaceBeforeClosing().Replace(result, "$1");
        result = SpaceAfterOpening().Replace(result, "$1");
        return result.Trim(' ');
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@" +([.,!?:;…»)\]])")]
    private static partial Regex SpaceBeforeClosing();

    [GeneratedRegex(@"([«(\[]) +")]
    private static partial Regex SpaceAfterOpening();
}

/// <summary>The files of the istupakov/gigaam-v3-onnx repository the plugin uses (int8 weights).</summary>
internal static class GigaAmModelFiles
{
    public const string Encoder = "v3_e2e_rnnt_encoder.int8.onnx";
    public const string Decoder = "v3_e2e_rnnt_decoder.int8.onnx";
    public const string Joint = "v3_e2e_rnnt_joint.int8.onnx";
    public const string Vocabulary = "v3_e2e_rnnt_vocab.txt";
}
