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
        var localizationDir = Path.Combine(AppContext.BaseDirectory, "Resources", "Localization");
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

    private static Dictionary<string, string> LoadLocalization(string localizationDir, string language)
    {
        var path = Path.Combine(localizationDir, $"{language}.json");
        Assert.True(File.Exists(path), $"{language}.json should be copied to the test output.");

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
