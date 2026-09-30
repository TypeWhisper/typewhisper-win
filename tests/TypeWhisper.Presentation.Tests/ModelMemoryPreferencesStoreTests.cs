using TypeWhisper.Presentation;
using Xunit;

public sealed class ModelMemoryPreferencesStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typewhisper-model-memory-" + Guid.NewGuid().ToString("N"));
    private string PreferencesPath => Path.Combine(_directory, "model-memory.json");

    [Fact]
    public void MissingProfileReleasesModelsAfterTenMinutesWithoutCreatingFiles()
    {
        var store = new ModelMemoryPreferencesStore(PreferencesPath);
        Assert.Equal(600, store.AutoUnloadSeconds);
        Assert.Null(store.Error);
        Assert.False(Directory.Exists(_directory));
    }

    [Theory]
    [InlineData(ModelMemoryPreferencesStore.Never)]
    [InlineData(ModelMemoryPreferencesStore.Immediately)]
    [InlineData(120)]
    [InlineData(3600)]
    public void SavedChoiceSurvivesReload(int seconds)
    {
        var store = new ModelMemoryPreferencesStore(PreferencesPath);
        Assert.Null(store.Save(seconds));
        Assert.Equal(seconds, new ModelMemoryPreferencesStore(PreferencesPath).AutoUnloadSeconds);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void UnofferedDurationIsRejectedAndKeepsTheSetting()
    {
        var store = new ModelMemoryPreferencesStore(PreferencesPath);
        Assert.Null(store.Save(300));
        Assert.NotNull(store.Save(45));
        Assert.Equal(300, store.AutoUnloadSeconds);
        Assert.Equal(300, new ModelMemoryPreferencesStore(PreferencesPath).AutoUnloadSeconds);
    }

    [Theory]
    [InlineData("{\"AutoUnloadSeconds\":45}")]
    [InlineData("{\"AutoUnloadSeconds\":\"600\"}")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("broken")]
    public void InvalidFileUsesTheDefaultAndCanBeRepaired(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PreferencesPath, json);
        var store = new ModelMemoryPreferencesStore(PreferencesPath);
        Assert.Equal(600, store.AutoUnloadSeconds);
        Assert.NotNull(store.Error);
        Assert.Equal(json, File.ReadAllText(PreferencesPath));
        Assert.Null(store.Save(ModelMemoryPreferencesStore.Never));
        Assert.Null(store.Error);
        Assert.Equal(0, new ModelMemoryPreferencesStore(PreferencesPath).AutoUnloadSeconds);
    }

    [Fact]
    public void FailedSavePreservesThePreviousSettingAndRemovesTemporaryFile()
    {
        var store = new ModelMemoryPreferencesStore(PreferencesPath);
        Assert.Null(store.Save(1800));
        File.Delete(PreferencesPath);
        Directory.CreateDirectory(PreferencesPath);
        Assert.NotNull(store.Save(120));
        Assert.Equal(1800, store.AutoUnloadSeconds);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
        catch (IOException) { /* A leftover temp folder must not fail the test run. */ }
        catch (UnauthorizedAccessException) { /* A leftover temp folder must not fail the test run. */ }
    }
}
