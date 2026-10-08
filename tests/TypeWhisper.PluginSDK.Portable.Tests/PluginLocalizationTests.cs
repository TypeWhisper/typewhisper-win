using System.Globalization;
using System.Text.Json;
using Moq;
using TypeWhisper.PluginHost;
using TypeWhisper.PluginSDK;

public sealed class PluginLocalizationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "plugin-localization-" + Guid.NewGuid().ToString("N"));
    public PluginLocalizationTests() => Directory.CreateDirectory(Path.Combine(_directory, "Localization"));
    public void Dispose() => Directory.Delete(_directory, recursive: true);
    private void Resource(string language, Dictionary<string, string> strings) =>
        File.WriteAllText(Path.Combine(_directory, "Localization", language + ".json"), JsonSerializer.Serialize(strings));

    [Theory]
    [InlineData("ja-JP", "選択")]
    [InlineData("zh-CN", "选择")]
    [InlineData("de-AT", "Auswählen")]
    [InlineData("fr-CA", "Select")]
    public void ResourcesResolveRegionalParentsAndEnglishFallback(string language, string expected)
    {
        Resource("en", new() { ["Settings.Select"] = "Select", ["English only"] = "English only" });
        Resource("ja", new() { ["Settings.Select"] = "選択" });
        Resource("zh-Hans", new() { ["Settings.Select"] = "选择" });
        Resource("de", new() { ["Settings.Select"] = "Auswählen" });
        var localization = new PackagePluginLocalization(_directory, () => language);
        Assert.Equal(expected, localization.GetString("Settings.Select"));
        Assert.Equal(expected, localization.GetString("Select"));
        Assert.Equal("English only", localization.GetString("English only"));
        Assert.Equal("Untranslated", localization.GetString("Untranslated"));
        Assert.Contains("ja", localization.AvailableLanguages);
    }

    [Fact]
    public void CurrentLanguageChangesWithoutReloadingAndSpecificRegionWins()
    {
        Resource("de", new() { ["Color"] = "Farbe" });
        Resource("de-CH", new() { ["Color"] = "Schweizer Farbe" });
        var language = "de-AT";
        var localization = new PackagePluginLocalization(_directory, () => language);
        Assert.Equal("Farbe", localization.GetString("Color"));
        language = "de-CH";
        Assert.Equal("Schweizer Farbe", localization.GetString("Color"));
        language = "en";
        Assert.Equal("Color", localization.GetString("Color"));
    }

    [Fact]
    public void InvalidOptionalResourceDoesNotPreventEnglishFallback()
    {
        Resource("en", new() { ["Count"] = "Count: {0}" });
        File.WriteAllText(Path.Combine(_directory, "Localization", "de.json"), "{broken");
        var localization = new PackagePluginLocalization(_directory, () => "de-DE");
        Assert.Equal("Count: 3", localization.GetString("Count", 3));
    }

    [Fact]
    public void SdkHelperPrefersResourceAndPreservesLegacyGermanFallback()
    {
        Resource("ja", new() { ["Select"] = "選択" });
        var language = "ja";
        var host = new Mock<IPluginHostServices>();
        host.SetupGet(x => x.Localization).Returns(new PackagePluginLocalization(_directory, () => language));
        Assert.Equal("選択", PluginLocalization.Get(host.Object, "Select", "Auswählen"));
        language = "de-AT";
        Assert.Equal("Auswählen", PluginLocalization.Get(host.Object, "Select", "Auswählen"));
        language = "fr";
        Assert.Equal("Select", PluginLocalization.Get(host.Object, "Select", "Auswählen"));
    }

    [Fact]
    public void SdkHelperSupportsHostsWithoutLocalization()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            var host = new Mock<IPluginHostServices>();
            host.SetupGet(x => x.Localization).Throws<NotSupportedException>();
            Assert.Equal("Auswählen", PluginLocalization.Get(host.Object, "Select", "Auswählen"));
            Assert.Equal("Auswählen", PluginLocalization.Get(null, "Select", "Auswählen"));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }
}
