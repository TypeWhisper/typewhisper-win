using TypeWhisper.Plugin.ParakeetCtc;
using TypeWhisper.PluginSDK;
using Xunit;

namespace TypeWhisper.PluginSDK.Portable.Tests;

public sealed class CtcVocabularySpanTests
{
    private static string[] Spans(string text, string term, float threshold = .52f) =>
        CtcVocabularySpans.Find(text, term, threshold, out _, out _, out _).Select(span => text.Substring(span.Start, span.Length)).ToArray();

    // Each transcript is a German dictation where the previous window rule replaced a neighbouring word with the term.
    [Theory]
    [InlineData("Die Anwendung verbindet Postgres SQL mit einem Cluster in Kubanesis.", "PostgreSQL", "Postgres SQL", "Postgres SQL mit")]
    [InlineData("Type Visper nutzt für diesen Test ein Modell von OpenAI.", "TypeWhisper", "Type Visper", "Type Visper nutzt")]
    [InlineData("Die Anwendung verbindet Postgres SQL mit einem Cluster in Kubanesis.", "Kubernetes", "Kubanesis", "in Kubanesis")]
    public void WindowsDoNotReachBeyondTheWordsThatResembleTheTerm(string text, string term, string expected, string swallowing)
    {
        var spans = Spans(text, term);
        Assert.Contains(expected, spans);
        Assert.DoesNotContain(swallowing, spans);
    }

    [Fact]
    public void TermAlreadyWrittenExactlyIsNeitherReplacedNorExtended()
    {
        Assert.Empty(Spans("Der Build schlug auf GitHub Actions zweimal fehl.", "GitHub Actions"));
        Assert.Empty(Spans("Ein Modell von OpenAI.", "OpenAI"));
    }

    [Fact]
    public void DifferentlyCasedSpellingRemainsAReplacementCandidate()
    {
        Assert.Equal(["Openai"], Spans("Ein Modell von Openai.", "OpenAI"));
    }

    [Fact]
    public void ThresholdStillRejectsUnrelatedWords()
    {
        var spans = CtcVocabularySpans.Find("Bitte schick mir den Entwurf.", "Kubernetes", .52f, out var similarityRejected, out _, out var best);
        Assert.Empty(spans);
        Assert.True(similarityRejected > 0);
        Assert.True(best < .52);
    }

    [Fact]
    public void OverlappingProposalsKeepTheWindowClosestToTheTerm()
    {
        // "Visper" alone sounds like "TypeWhisper" too and scores higher, but replacing it would leave "Type TypeWhisper".
        var text = "Type Visper nutzt";
        VocabularyReplacement Proposal(string original, double score) => new(text.IndexOf(original, StringComparison.Ordinal), original.Length, "TypeWhisper", score);

        var selected = CtcVocabularySpans.Select([
            (Proposal("Visper", 9), CtcVocabularySpans.Similarity("Visper", "TypeWhisper")),
            (Proposal("Type Visper", 6), CtcVocabularySpans.Similarity("Type Visper", "TypeWhisper"))]);

        var replacement = Assert.Single(selected);
        Assert.Equal((0, "Type Visper".Length), (replacement.Start, replacement.Length));
    }

    [Fact]
    public void EqualSimilarityFallsBackToTheAcousticScoreAndKeepsDisjointProposals()
    {
        var selected = CtcVocabularySpans.Select([
            (new VocabularyReplacement(0, 4, "Alpha", 1), .8),
            (new VocabularyReplacement(2, 4, "Beta", 3), .8),
            (new VocabularyReplacement(10, 3, "Gamma", 2), .6)]);

        Assert.Equal(["Beta", "Gamma"], selected.Select(r => r.Term));
    }
}
