using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services.Localization;

namespace TypeWhisper.PluginSystem.Tests;

public class AppLocalizationResourcesTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Theory]
    [MemberData(nameof(AppLocalizationLanguages))]
    public void AppLocalization_HasSameKeysAndFormatPlaceholdersAsEnglish(string language)
    {
        var localizationDir = Path.Join(AppContext.BaseDirectory, "Resources", "Localization");
        var english = LoadLocalization(localizationDir, "en");
        var localized = LoadLocalization(localizationDir, language);

        Assert.Equal(english.Keys.OrderBy(k => k), localized.Keys.OrderBy(k => k));

        foreach (var key in english.Keys)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(localized[key]),
                $"{language} value for {key} must not be empty.");
            Assert.Equal(FormatPlaceholders(english[key]), FormatPlaceholders(localized[key]));
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

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("ja")]
    [InlineData("ru")]
    public void IndustryPresetLocalizationKeys_ArePresent(string language)
    {
        var localizationDir = Path.Join(AppContext.BaseDirectory, "Resources", "Localization");
        var localization = LoadLocalization(localizationDir, language);

        foreach (var key in IndustryPreset.All.Select(preset => $"IndustryPreset.{preset.Id}.Name"))
        {
            Assert.True(localization.TryGetValue(key, out var value), $"{language} should define {key}.");
            Assert.False(string.IsNullOrWhiteSpace(value));
        }
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

    public static IEnumerable<object[]> AppLocalizationLanguages()
    {
        var localizationDir = Path.Join(AppContext.BaseDirectory, "Resources", "Localization");
        Assert.True(Directory.Exists(localizationDir), "Localization resources should be copied to the test output.");

        return Directory.EnumerateFiles(localizationDir, "*.json")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Where(language => !string.Equals(language, "en", StringComparison.OrdinalIgnoreCase))
            .OrderBy(language => language)
            .Select(language => new object[] { language! })
            .ToArray();
    }

    private static string[] FormatPlaceholders(string value) =>
        Regex.Matches(value, @"\{\d+(?::[^}]*)?\}")
            .Select(match => match.Value)
            .OrderBy(placeholder => placeholder)
            .ToArray();
}
