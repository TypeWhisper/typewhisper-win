using TypeWhisper.Presentation;
using Xunit;

public sealed class DictationProvenanceTests
{
    [Theory]
    [InlineData("german", "auto", "de")]
    [InlineData("de", "en", "de")]
    [InlineData("DE-ch", "en", "de")]
    [InlineData("eng", "de", "en")]
    [InlineData(null, "de", "de")]
    [InlineData("unrecognized", "fr", "fr")]
    [InlineData(null, "auto", null)]
    [InlineData("", "auto", null)]
    [InlineData("unrecognized", "auto", null)]
    public void UsesProviderEvidenceAndNeverInventsAnAutomaticLanguage(string? detected, string? configured, string? expected)
        => Assert.Equal(expected, DictationProvenance.ResolveLanguage(detected, configured));
}
