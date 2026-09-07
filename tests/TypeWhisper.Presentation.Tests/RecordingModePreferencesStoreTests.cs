using TypeWhisper.Presentation;
using Xunit;

public sealed class RecordingModePreferencesStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "typewhisper-mode-" + Guid.NewGuid().ToString("N"));
    private string PreferencesPath => Path.Combine(_directory, "recording-mode.json");

    [Fact]
    public void MissingProfileDefaultsToHybridWithoutCreatingFiles()
    {
        var store = new RecordingModePreferencesStore(PreferencesPath);
        Assert.Equal(RecordingMode.Hybrid, store.Current);
        Assert.Null(store.Error);
        Assert.False(Directory.Exists(_directory));
    }

    [Theory]
    [InlineData(RecordingMode.Hybrid)]
    [InlineData(RecordingMode.Toggle)]
    [InlineData(RecordingMode.Hold)]
    public void SavedModeSurvivesReload(RecordingMode mode)
    {
        var store = new RecordingModePreferencesStore(PreferencesPath);
        Assert.Null(store.Save(mode));
        Assert.Equal(mode, new RecordingModePreferencesStore(PreferencesPath).Current);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("{\"Mode\":\"Unknown\"}")]
    [InlineData("{\"Mode\":99}")]
    [InlineData("{\"Mode\":\"99\"}")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("broken")]
    public void InvalidFileUsesHybridAndCanBeRepaired(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PreferencesPath, json);
        var store = new RecordingModePreferencesStore(PreferencesPath);
        Assert.Equal(RecordingMode.Hybrid, store.Current);
        Assert.NotNull(store.Error);
        Assert.Equal(json, File.ReadAllText(PreferencesPath));
        Assert.Null(store.Save(RecordingMode.Hold));
        Assert.Null(store.Error);
        Assert.Equal(RecordingMode.Hold, new RecordingModePreferencesStore(PreferencesPath).Current);
    }

    [Fact]
    public void FailedSavePreservesPreviousModeAndRemovesTemporaryFile()
    {
        var store = new RecordingModePreferencesStore(PreferencesPath);
        Assert.Null(store.Save(RecordingMode.Toggle));
        File.Delete(PreferencesPath);
        Directory.CreateDirectory(PreferencesPath);
        Assert.NotNull(store.Save(RecordingMode.Hold));
        Assert.Equal(RecordingMode.Toggle, store.Current);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void InvalidModeCannotReplaceSavedChoice()
    {
        var store = new RecordingModePreferencesStore(PreferencesPath);
        Assert.Null(store.Save(RecordingMode.Hold));
        Assert.NotNull(store.Save((RecordingMode)99));
        Assert.Equal(RecordingMode.Hold, store.Current);
        Assert.Equal(RecordingMode.Hold, new RecordingModePreferencesStore(PreferencesPath).Current);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
