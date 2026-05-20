using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.PluginSystem.Tests;

public class AppLocalizationResourcesTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void RussianLocalization_HasSameKeysAndFormatPlaceholdersAsEnglish()
    {
        var localizationDir = Path.Join(AppContext.BaseDirectory, "Resources", "Localization");
        var english = LoadLocalization(localizationDir, "en");
        var russian = LoadLocalization(localizationDir, "ru");

        Assert.Equal(english.Keys.OrderBy(k => k), russian.Keys.OrderBy(k => k));

        foreach (var key in english.Keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(russian[key]), $"Russian value for {key} must not be empty.");
            Assert.Equal(FormatPlaceholders(english[key]), FormatPlaceholders(russian[key]));
        }
    }

    [Fact]
    public void Loc_ListsRussianUiLanguage()
    {
        Loc.Instance.Initialize();

        Assert.Contains("ru", Loc.Instance.AvailableLanguages);
        Assert.Contains(Loc.Instance.AvailableUiLanguages,
            option => option.Code == "ru" && option.DisplayName == "Русский");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("ja")]
    [InlineData("ru")]
    public void AppearanceOnlineAsrBatchLivePreviewLocalizationKeys_ArePresent(string language)
    {
        var localizationDir = Path.Join(AppContext.BaseDirectory, "Resources", "Localization");
        var localization = LoadLocalization(localizationDir, language);

        Assert.Contains("Appearance.OnlineAsrBatchLivePreview", localization.Keys);
        Assert.Contains("Appearance.OnlineAsrBatchLivePreviewHint", localization.Keys);
        Assert.False(string.IsNullOrWhiteSpace(localization["Appearance.OnlineAsrBatchLivePreview"]));
        Assert.False(string.IsNullOrWhiteSpace(localization["Appearance.OnlineAsrBatchLivePreviewHint"]));
    }

    private static Dictionary<string, string> LoadLocalization(string localizationDir, string language)
    {
        var languageFileName = Path.GetFileName($"{language}.json");
        var path = Path.Join(localizationDir, languageFileName);
        Assert.True(File.Exists(path), $"{languageFileName} should be copied to the test output.");

        var json = File.ReadAllText(path);
        var localization = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions);

        Assert.NotNull(localization);
        return localization;
    }

    private static string[] FormatPlaceholders(string value) =>
        Regex.Matches(value, @"\{\d+(?::[^}]*)?\}")
            .Select(match => match.Value)
            .OrderBy(placeholder => placeholder)
            .ToArray();
}
