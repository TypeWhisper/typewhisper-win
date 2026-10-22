using System.Globalization;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

// Loc holds the language of the whole process, so these tests must not run beside others that read it.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ModelDownloadRequirementLanguageCollection
{
    public const string Name = "Model download requirement language";
}

[Collection(ModelDownloadRequirementLanguageCollection.Name)]
public sealed class ModelDownloadRequirementTitleTests : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
    private readonly CultureInfo? _defaultCulture = CultureInfo.DefaultThreadCurrentUICulture;

    public void Dispose()
    {
        Loc.Use("en");
        CultureInfo.CurrentUICulture = _culture;
        CultureInfo.DefaultThreadCurrentUICulture = _defaultCulture;
    }

    [Theory]
    [InlineData("Access token", true, "Access token")]
    [InlineData("Access token", false, "Access token (optional)")]
    [InlineData("Hugging Face token (optional)", false, "Hugging Face token (optional)")]
    [InlineData("Hugging Face token (Optional) ", false, "Hugging Face token (Optional) ")]
    public void MarksAnOptionalRequirementOnceInEnglish(string title, bool isRequired, string expected)
    {
        Loc.Use("en");

        Assert.Equal(expected, ModelDownloadRequirementTitle.For(title, isRequired));
    }

    [Fact]
    public void KeepsTheMarkerOfAGermanPluginTitle()
    {
        Loc.Use("de");

        Assert.Equal("Hugging-Face-Token (optional)", ModelDownloadRequirementTitle.For("Hugging-Face-Token (optional)", false));
        Assert.Equal("Zugangstoken (optional)", ModelDownloadRequirementTitle.For("Zugangstoken", false));
    }

    [Fact]
    public void KeepsTheMarkerOfATranslatedOrEnglishFallbackTitle()
    {
        Loc.Use("ja");

        Assert.Equal("Hugging Face token (optional)", ModelDownloadRequirementTitle.For("Hugging Face token (optional)", false));
        Assert.Equal("トークン（任意）", ModelDownloadRequirementTitle.For("トークン（任意）", false));
        Assert.Equal("トークン（任意）", ModelDownloadRequirementTitle.For("トークン", false));
    }
}
