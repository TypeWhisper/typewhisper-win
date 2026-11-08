using System.Text;

namespace TypeWhisper.Plugin.Games;

/// <summary>
/// Turns dictated text into single-line game chat messages.
/// </summary>
internal static class ChatMessageSplitter
{
    private const string SentenceEnds = ".!?…";
    private const string FullWidthSentenceEnds = "。！？";

    /// <summary>
    /// Flattens the text to one line, drops leading slashes that games run as commands, and splits it at sentence or
    /// word boundaries into messages of at most <paramref name="maxBytes"/> UTF-8 bytes.
    /// </summary>
    /// <remarks>Games such as World of Warcraft count bytes, so an umlaut takes two of the 255 and a CJK character three.</remarks>
    public static IReadOnlyList<string> Split(string text, int? maxBytes)
    {
        var rest = Clean(Flatten(text));
        if (rest.Length == 0) return [];
        if (maxBytes is not > 0 || Encoding.UTF8.GetByteCount(rest) <= maxBytes) return [rest];
        var parts = new List<string>();
        while (Encoding.UTF8.GetByteCount(rest) > maxBytes)
        {
            var cut = BreakIndex(rest, Fit(rest, maxBytes.Value));
            var part = rest[..cut].TrimEnd();
            if (part.Length > 0) parts.Add(part);
            rest = Clean(rest[cut..]);
        }
        if (rest.Length > 0) parts.Add(rest);
        return parts;
    }

    // Enter sends a chat message, so line breaks and other control characters become spaces. A vertical bar starts
    // an escape code in World of Warcraft, which rejects the message, so it becomes a slash.
    private static string Flatten(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Select(raw => raw == '|' ? '/' : raw))
        {
            var space = char.IsWhiteSpace(c) || char.IsControl(c);
            if (!space) builder.Append(c);
            else if (builder.Length > 0 && builder[^1] != ' ') builder.Append(' ');
        }
        return builder.ToString();
    }

    // Flatten leaves single spaces, so slashes and spaces are stripped together: "/ /logout" must not become "/logout".
    private static string Clean(string text) => text.TrimStart('/', ' ').TrimEnd();

    // The number of leading UTF-16 characters whose UTF-8 form fits into max bytes, never splitting a surrogate pair
    // and always at least one character, so a tiny limit still makes progress.
    private static int Fit(string text, int max)
    {
        var bytes = 0;
        for (var i = 0; i < text.Length;)
        {
            var pair = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
            var size = pair ? 4 : text[i] < 0x80 ? 1 : text[i] < 0x800 ? 2 : 3;
            if (bytes + size > max) return i > 0 ? i : pair ? 2 : 1;
            bytes += size;
            i += pair ? 2 : 1;
        }
        return text.Length;
    }

    // Returns the length of the next message; fit is shorter than the text.
    private static int BreakIndex(string text, int fit)
    {
        int sentence = -1, word = -1;
        for (var i = 1; i <= fit && i < text.Length; i++)
        {
            var before = text[i - 1];
            if (text[i] == ' ')
            {
                word = i;
                if (SentenceEnds.Contains(before)) sentence = i;
            }
            else if (FullWidthSentenceEnds.Contains(before)) sentence = word = i;
        }
        if (sentence >= fit / 2) return sentence;
        if (word > 0) return word;
        return fit;
    }
}
