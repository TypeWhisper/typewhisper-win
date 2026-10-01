using System.Globalization;
using System.Text.RegularExpressions;
using TypeWhisper.Core;

namespace TypeWhisper.Core.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LocCollection
{
    public const string Name = "Interface language";
}

// Loc holds the language of the whole process, so these tests must not run beside others that read it.
[Collection(LocCollection.Name)]
public sealed partial class LocTests : IDisposable
{
    private static readonly string[] Translated = ["de", "ja", "zh-Hans"];
    private readonly CultureInfo _culture = CultureInfo.CurrentUICulture;
    private readonly CultureInfo? _defaultCulture = CultureInfo.DefaultThreadCurrentUICulture;

    public void Dispose()
    {
        Loc.Use("en");
        CultureInfo.CurrentUICulture = _culture;
        CultureInfo.DefaultThreadCurrentUICulture = _defaultCulture;
    }

    [Theory]
    [InlineData("de", "en-US", "de")]
    [InlineData(null, "de-AT", "de")]
    [InlineData(null, "ja-JP", "ja")]
    [InlineData(null, "zh-TW", "zh-Hans")]
    [InlineData(null, "fr-FR", "en")]
    [InlineData("fr", "ja-JP", "ja")]
    public void Resolve_PrefersTheSavedLanguageAndFallsBackToTheSystem(string? saved, string system, string expected) =>
        Assert.Equal(expected, Loc.Resolve(saved, CultureInfo.GetCultureInfo(system)));

    [Fact]
    public void T_TranslatesKnownTextAndKeepsUnknownTextInEnglish()
    {
        Loc.Use("de");

        Assert.Equal("de", Loc.Language);
        Assert.Equal("Jetzt neustarten", Loc.T("Restart Now"));
        Assert.Equal("Text without a translation", Loc.T("Text without a translation"));
        Assert.Equal("de", CultureInfo.CurrentUICulture.Name);
    }

    [Fact]
    public void T_FillsPlaceholders()
    {
        Loc.Use("en");

        Assert.Equal("3 of 7", Loc.T("{0} of {1}", 3, 7));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Catalog_TranslatesEveryTextUsedInTheSource(string language)
    {
        var catalog = Loc.Catalog(language);
        var texts = SourceTexts().ToArray();
        var missing = texts.Where(text => !catalog.ContainsKey(text)).Order().ToArray();

        Assert.Contains("Restart Now", texts);
        Assert.True(missing.Length == 0,
            $"{language}.json lacks {missing.Length} text(s). Run eng/Import-MacTranslations.py and translate the rest:\n" + string.Join("\n", missing));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Catalog_KeepsThePlaceholdersOfTheEnglishText(string language)
    {
        var broken = Loc.Catalog(language)
            .Where(entry => string.IsNullOrWhiteSpace(entry.Value) || !Placeholders(entry.Key).SetEquals(Placeholders(entry.Value)))
            .Select(entry => entry.Key).Order().ToArray();

        Assert.True(broken.Length == 0, $"{language}.json has empty translations or different placeholders:\n" + string.Join("\n", broken));
    }

    [Fact]
    public void Source_PassesOnlyPlainStringLiteralsAsText()
    {
        // An interpolated or verbatim string cannot be looked up; pass a literal with numbered placeholders.
        var offenders = SourceFiles().Where(file => UnsupportedCall().IsMatch(File.ReadAllText(file))).ToArray();

        Assert.True(offenders.Length == 0, "Loc.T needs a plain string literal in:\n" + string.Join("\n", offenders));
    }

    public static TheoryData<string> Languages() => new(Translated);

    private static HashSet<string> Placeholders(string text) =>
        Placeholder().Matches(text.Replace("{{", "").Replace("}}", "")).Select(match => match.Value).ToHashSet();

    private static IEnumerable<string> SourceTexts() => SourceFiles()
        .SelectMany(file => Call().Matches(File.ReadAllText(file)))
        .Select(match => Regex.Unescape(match.Groups[1].Value))
        .Distinct();

    private static IEnumerable<string> SourceFiles()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "TypeWhisper.slnx")) && !Directory.Exists(Path.Combine(root, ".git")) && !File.Exists(Path.Combine(root, ".git")))
            root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("The repository root was not found.");
        var separator = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{separator}obj{separator}") && !file.Contains($"{separator}bin{separator}"));
    }

    [GeneratedRegex("""Loc\.T\(\s*"((?:[^"\\]|\\.)*)" """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex Call();

    [GeneratedRegex("""Loc\.T\(\s*[$@]""")]
    private static partial Regex UnsupportedCall();

    [GeneratedRegex(@"\{\d+(:[^}]*)?\}")]
    private static partial Regex Placeholder();
}
