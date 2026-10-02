using TypeWhisper.Presentation;
using Xunit;

public sealed class InterfaceThemeStoreTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), "typewhisper-interface-theme-" + Guid.NewGuid().ToString("N"));
    private string PreferencesPath => Path.Join(_directory, "interface-theme.json");

    [Fact]
    public void MissingProfileFollowsWindowsAndCreatesNoFiles()
    {
        Assert.Equal(InterfaceTheme.System, new InterfaceThemeStore(PreferencesPath).Saved);
        Assert.False(Directory.Exists(_directory));
    }

    [Theory]
    [InlineData(InterfaceTheme.System)]
    [InlineData(InterfaceTheme.Light)]
    [InlineData(InterfaceTheme.Dark)]
    public void SavedThemeSurvivesReload(InterfaceTheme theme)
    {
        var store = new InterfaceThemeStore(PreferencesPath);
        Assert.Null(store.Save(InterfaceTheme.Dark));
        Assert.Null(store.Save(theme));
        Assert.Equal(theme, store.Saved);
        Assert.Equal(theme, new InterfaceThemeStore(PreferencesPath).Saved);
    }

    [Theory]
    [InlineData("{\"Theme\":\"Sepia\"}")]
    [InlineData("{\"Theme\":\"1\"}")]
    [InlineData("{\"Theme\":2}")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("not json")]
    public void InvalidFileFollowsWindows(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PreferencesPath, content);
        Assert.Equal(InterfaceTheme.System, new InterfaceThemeStore(PreferencesPath).Saved);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
