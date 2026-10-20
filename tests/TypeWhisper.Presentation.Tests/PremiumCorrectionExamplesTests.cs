using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class PremiumCorrectionExamplesTests
{
    [Theory]
    [InlineData("en", "teh → the", "Pria → Priya")]
    [InlineData("de", "Standart → Standard", "Prija → Priya")]
    [InlineData("de-DE", "Standart → Standard", "Prija → Priya")]
    [InlineData("ja", "こんにちわ → こんにちは", "斎藤 → 齋藤")]
    [InlineData("zh-Hans", "因该 → 应该", "李华 → 李桦")]
    [InlineData("fr", "teh → the", "Pria → Priya")]
    [InlineData("", "teh → the", "Pria → Priya")]
    public void ExamplesFollowTheInterfaceLanguage(string language, string typo, string name)
    {
        Assert.Equal(typo, PremiumCorrectionExamples.Primary(language));
        Assert.Equal(typo + "  ·  " + name, PremiumCorrectionExamples.Line(language));
        var examples = PremiumCorrectionExamples.For(language);
        Assert.Equal([PremiumCorrectionKind.Typo, PremiumCorrectionKind.Name, PremiumCorrectionKind.Spelling], examples.Select(e => e.Kind));
        Assert.All(examples, example => Assert.NotEqual(example.Before, example.After));
    }
}
