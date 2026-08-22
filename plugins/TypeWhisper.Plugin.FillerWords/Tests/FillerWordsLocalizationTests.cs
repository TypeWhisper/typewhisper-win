using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TypeWhisper.Plugin.FillerWords.Tests;

public sealed class FillerWordsLocalizationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Theory]
    [InlineData("de")]
    public void Localization_HasSameKeysAndFormatPlaceholdersAsEnglish(string language)
    {
        var english = LoadLocalization("en");
        var localized = LoadLocalization(language);

        Assert.Equal(english.Keys.OrderBy(key => key, StringComparer.Ordinal),
            localized.Keys.OrderBy(key => key, StringComparer.Ordinal));

        foreach (var key in english.Keys)
        {
            Assert.False(
                string.IsNullOrWhiteSpace(localized[key]),
                $"{language} value for {key} must not be empty.");
            Assert.Equal(FormatPlaceholders(english[key]), FormatPlaceholders(localized[key]));
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void Localization_HasASingularWordCountWithoutAPlaceholder(string language)
    {
        var localization = LoadLocalization(language);

        Assert.True(localization.ContainsKey("Settings.WordCountOne"));
        Assert.Empty(FormatPlaceholders(localization["Settings.WordCountOne"]));
        Assert.Contains("{0}", localization["Settings.WordCount"]);
    }

    private static Dictionary<string, string> LoadLocalization(string language)
    {
        var path = Path.GetFullPath(Path.Join(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "plugins", "TypeWhisper.Plugin.FillerWords", "Localization", $"{language}.json"));

        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException($"Could not load {path}.");
    }

    private static IEnumerable<string> FormatPlaceholders(string value) =>
        Regex.Matches(value, @"\{\d+\}").Select(match => match.Value).OrderBy(match => match, StringComparer.Ordinal);
}
