using System.Globalization;
using System.Text;
using TypeWhisper.PluginSDK;

namespace TypeWhisper.PluginHost;

// Plugin output is a proposal, never an authoritative replacement transcript.
public static class VocabularyResultValidator
{
    public static string Apply(VocabularyRescoreRequest request, VocabularyRescoreResult result)
    {
        if (result is null || result.RecordingId != request.RecordingId || result.Replacements is null)
            throw new InvalidDataException("The plugin returned an invalid recording identity or result.");
        var terms = request.Terms.Select(t => t.Text).ToHashSet(StringComparer.Ordinal);
        var boundaries = StringInfo.ParseCombiningCharacters(request.Text).Append(request.Text.Length).ToHashSet();
        var replacements = result.Replacements.ToArray();
        if (replacements.Length > request.Text.Length || replacements.Any(r => r is null))
            throw new InvalidDataException("Invalid replacement collection.");
        Array.Sort(replacements, (a, b) => a.Start.CompareTo(b.Start));
        var end = 0;
        foreach (var replacement in replacements)
        {
            if (replacement.Start < end || replacement.Length <= 0 || replacement.Start < 0 ||
                replacement.Start > request.Text.Length - replacement.Length ||
                !double.IsFinite(replacement.Score) || !terms.Contains(replacement.Term) ||
                !boundaries.Contains(replacement.Start) || !boundaries.Contains(replacement.Start + replacement.Length))
                throw new InvalidDataException("The plugin returned an invalid vocabulary replacement.");
            end = replacement.Start + replacement.Length;
        }
        var text = new StringBuilder(request.Text);
        foreach (var replacement in replacements.Reverse())
            text.Remove(replacement.Start, replacement.Length).Insert(replacement.Start, replacement.Term);
        return text.ToString();
    }
}
