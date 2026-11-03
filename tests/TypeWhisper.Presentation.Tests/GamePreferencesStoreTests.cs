using TypeWhisper.Presentation;
using Xunit;

public sealed class GamePreferencesStoreTests : IDisposable
{
    private readonly string _directory = Path.Join(Path.GetTempPath(), "typewhisper-games-" + Guid.NewGuid().ToString("N"));
    private string PreferencesPath => Path.Join(_directory, "games.json");

    [Fact]
    public void MissingProfileLeavesGamesOffWithoutCreatingFiles()
    {
        var store = new GamePreferencesStore(PreferencesPath);

        Assert.False(store.Current.WorldOfWarcraft.Enabled);
        Assert.Null(store.Error);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void SavedSettingsSurviveReload()
    {
        var store = new GamePreferencesStore(PreferencesPath);
        var next = new GamePreferences { WorldOfWarcraft = new() { Enabled = true, Folder = @"D:\Games\World of Warcraft" } };

        Assert.Null(store.Save(next));

        Assert.Equal(next, new GamePreferencesStore(PreferencesPath).Current);
    }

    [Fact]
    public void SettingsThatMovedToTheAddOnAreIgnored()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PreferencesPath, "{\"WorldOfWarcraft\":{\"Enabled\":true,\"OpenKey\":\"Enter\",\"Send\":false,\"ShowInGame\":false,\"ReplyWorkflowId\":\"x\"}}");

        var store = new GamePreferencesStore(PreferencesPath);

        Assert.Null(store.Error);
        Assert.True(store.Current.WorldOfWarcraft.Enabled);
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("null")]
    [InlineData("{\"WorldOfWarcraft\":null}")]
    public void InvalidFileLeavesGamesOffAndCanBeRepaired(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PreferencesPath, json);

        var store = new GamePreferencesStore(PreferencesPath);

        Assert.False(store.Current.WorldOfWarcraft.Enabled);
        Assert.NotNull(store.Error);
        Assert.Null(store.Save(new GamePreferences { WorldOfWarcraft = new() { Enabled = true } }));
        Assert.True(new GamePreferencesStore(PreferencesPath).Current.WorldOfWarcraft.Enabled);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
