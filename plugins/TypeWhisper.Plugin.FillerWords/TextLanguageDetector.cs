using System.Text.RegularExpressions;

namespace TypeWhisper.Plugin.FillerWords;

/// <summary>
/// Guesses the language of dictated text from common function words. It is deliberately
/// strict: an answer needs several matching words and a clear lead over every other
/// language, so in doubt there is none.
/// </summary>
internal static class TextLanguageDetector
{
    private const int MinimumHits = 3;
    private const int MinimumLeadFactor = 3;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly Regex Word = new(
        @"\p{L}+(?:['’]\p{L}+)*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        MatchTimeout);

    /// <summary>
    /// Function words per language. A word listed for more than one language says nothing
    /// about either and is ignored. Language-bound filler words such as "um" must not appear.
    /// </summary>
    private static readonly Dictionary<string, string> LanguageByWord = BuildIndex(new Dictionary<string, string>
    {
        ["en"] = "the and is are that this it i you we they she with for not have has been be does did what think " +
                 "just can would your there about but if know like going get don't it's i'm that's let's yeah really " +
                 "because should could which when where who how we'll i've i'll",
        ["de"] = "der die das und ist nicht ich du wir sie er es ein eine einen einem einer zu mit auf für den dem des " +
                 "von sich auch noch aber dass wenn uhr schon jetzt habe haben hat bin sind war waren gut heute morgen " +
                 "nur oder kann können wird werden mir mich dir uns euch ihr im zum zur beim nach bei aus über sehr doch " +
                 "ja nein nichts etwas wer warum weil dann also diese dieser dieses gibt geht machen komme kommt",
        ["fr"] = "le la les et est une des du que qui je il elle nous vous ils elles pas ne ce cette dans sur pour avec " +
                 "mais où oui très c'est j'ai suis au aux être avoir fait sont",
        ["es"] = "el los las y es no un una por para con lo su al del está pero muy yo tú él ella también hay sí más " +
                 "cuando esto eso estoy tengo hace",
        ["pt"] = "os não em na com para por eu você ele ela nós isso isto muito foi está estou tem ao já mais também " +
                 "ontem hoje uma no",
        ["it"] = "il lo la gli le è di che un una per con sono ho ha ma anche questo questa molto perché io lui lei noi " +
                 "voi loro del della nel nella",
        ["nl"] = "de het een is van dat niet ik je we wij zij hij met op voor maar ook nog wat zijn heb heeft er naar om " +
                 "bij uit wel geen"
    });

    /// <summary>
    /// Returns the two-letter code of the language <paramref name="text"/> is written in,
    /// or null when the text does not show it reliably.
    /// </summary>
    internal static string? Detect(string text)
    {
        var hits = new Dictionary<string, int>(StringComparer.Ordinal);

        try
        {
            var languages = Word.Matches(text)
                .Select(match => LanguageByWord.GetValueOrDefault(match.Value.Replace('’', '\'').ToLowerInvariant()))
                .OfType<string>();

            foreach (var language in languages)
                hits[language] = hits.GetValueOrDefault(language) + 1;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        string? best = null;
        var bestHits = 0;
        var runnerUpHits = 0;

        foreach (var (language, count) in hits)
        {
            if (count > bestHits)
            {
                runnerUpHits = bestHits;
                best = language;
                bestHits = count;
            }
            else if (count > runnerUpHits)
            {
                runnerUpHits = count;
            }
        }

        return bestHits >= MinimumHits && bestHits >= runnerUpHits * MinimumLeadFactor ? best : null;
    }

    private static Dictionary<string, string> BuildIndex(Dictionary<string, string> wordsByLanguage) =>
        wordsByLanguage
            .SelectMany(pair => pair.Value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.Ordinal)
                .Select(word => (Word: word, Language: pair.Key)))
            .GroupBy(entry => entry.Word, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Language, StringComparer.Ordinal);
}
