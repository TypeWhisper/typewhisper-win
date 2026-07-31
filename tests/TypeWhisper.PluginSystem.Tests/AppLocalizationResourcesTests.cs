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
            Assert.Equal(english[key].Count(c => c == '|'), localized[key].Count(c => c == '|'));
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

    [Fact]
    public void Loc_ListsSimplifiedChineseUiLanguage()
    {
        Loc.Instance.Initialize();

        Assert.Contains("zh-Hans", Loc.Instance.AvailableLanguages);
        Assert.Contains(Loc.Instance.AvailableUiLanguages,
            option => option.Code == "zh-Hans" && option.DisplayName == "简体中文");
    }

    [Theory]
    [InlineData("zh", "en,zh-Hans", "zh-Hans")]
    [InlineData("zh-CN", "en,zh-Hans", "zh-Hans")]
    [InlineData("zh-SG", "en,zh-Hans", "zh-Hans")]
    [InlineData("zh-MY", "en,zh-Hans", "zh-Hans")]
    [InlineData("zh-CHS", "en,zh-Hans", "zh-Hans")]
    [InlineData("zh-Hans-CN", "en,zh-Hans", "zh-Hans")]
    [InlineData("zh_TW", "en,zh-Hant", "zh-Hant")]
    [InlineData("zh-HK", "en,zh-Hant", "zh-Hant")]
    [InlineData("zh-MO", "en,zh-Hant", "zh-Hant")]
    [InlineData("zh-CHT", "en,zh-Hant", "zh-Hant")]
    [InlineData("zh-TW", "en,zh-Hans", "en")]
    [InlineData("zh-TW", "en,zh", "en")]
    [InlineData("zh-HK", "en,zh", "en")]
    [InlineData("en-US", "en,zh-Hans", "en")]
    [InlineData("de-DE", "de,en,zh-Hans", "de")]
    public void ResolveLanguage_MapsCulturesToAvailableResources(
        string cultureName,
        string availableLanguages,
        string expected)
    {
        var available = availableLanguages.Split(',');

        Assert.Equal(expected, Loc.ResolveLanguage(cultureName, available));
    }

    [Fact]
    public void SimplifiedChinese_PreservesWhitespaceAndTechnicalTerms()
    {
        var localizationDir = Path.Join(AppContext.BaseDirectory, "Resources", "Localization");
        var english = LoadLocalization(localizationDir, "en");
        var simplifiedChinese = LoadLocalization(localizationDir, "zh-Hans");

        foreach (var key in english.Keys)
        {
            Assert.Equal(LeadingWhitespace(english[key]), LeadingWhitespace(simplifiedChinese[key]));
            Assert.Equal(TrailingWhitespace(english[key]), TrailingWhitespace(simplifiedChinese[key]));
        }

        Assert.Contains("TypeWhisper", simplifiedChinese["Settings.Title"]);
        Assert.Contains("Ctrl+V", simplifiedChinese["General.AutoPaste"]);
        Assert.Contains("REST API", simplifiedChinese["General.RestApiEnable"]);
        Assert.Contains("NVIDIA CUDA", simplifiedChinese["Models.AccelerationNvidiaCuda"]);
        Assert.Contains("AMD ROCm", simplifiedChinese["Models.AccelerationAmdRocm"]);
        Assert.Contains("Discord", simplifiedChinese["License.ConnectDiscord"]);
        Assert.Contains("Polar", simplifiedChinese["License.CustomerPortalHint"]);
        Assert.Contains("Esc", simplifiedChinese["Status.CancelRecordingConfirm"]);
        Assert.Contains("Enter", simplifiedChinese["Profiles.ProcessNameHint"]);
        Assert.Contains("Windows", simplifiedChinese["Recorder.M4AEncoderUnavailable"]);
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
        Regex.Matches(value, @"\{[^{}\r\n]+\}")
            .Select(match => match.Value)
            .OrderBy(placeholder => placeholder)
            .ToArray();

    private static string LeadingWhitespace(string value) =>
        new(value.TakeWhile(char.IsWhiteSpace).ToArray());

    private static string TrailingWhitespace(string value) =>
        new(value.Reverse().TakeWhile(char.IsWhiteSpace).Reverse().ToArray());
}
