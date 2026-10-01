using TypeWhisper.Presentation;
using Xunit;

public sealed class InterfaceLanguageStoreTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), "typewhisper-interface-language-" + Guid.NewGuid().ToString("N"));
    private string PreferencesPath => Path.Join(_directory, "interface-language.json");

    [Fact]
    public void MissingProfileHasNoSavedLanguageAndCreatesNoFiles()
    {
        Assert.Null(new InterfaceLanguageStore(PreferencesPath).Saved);
        Assert.False(Directory.Exists(_directory));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("ja")]
    [InlineData("zh-Hans")]
    public void SavedLanguageSurvivesReload(string language)
    {
        var store = new InterfaceLanguageStore(PreferencesPath);
        Assert.Null(store.Save(language));
        Assert.Equal(language, store.Saved);
        Assert.Equal(language, new InterfaceLanguageStore(PreferencesPath).Saved);
    }

    [Fact]
    public void UnofferedLanguageIsRejectedAndKeepsTheChoice()
    {
        var store = new InterfaceLanguageStore(PreferencesPath);
        Assert.Null(store.Save("de"));
        Assert.NotNull(store.Save("fr"));
        Assert.Equal("de", store.Saved);
        Assert.Equal("de", new InterfaceLanguageStore(PreferencesPath).Saved);
    }

    [Theory]
    [InlineData("{\"Language\":\"fr\"}")]
    [InlineData("{\"Language\":7}")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("not json")]
    public void InvalidFileHasNoSavedLanguage(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PreferencesPath, content);
        Assert.Null(new InterfaceLanguageStore(PreferencesPath).Saved);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
