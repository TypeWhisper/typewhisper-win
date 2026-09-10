using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class SetupPreferencesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "setup-prefs-" + Guid.NewGuid());
    private string StorePath => Path.Combine(_directory, "setup.json");

    [Fact]
    public void ProgressAndCompletionSurviveReloadWithoutChangingOtherSettings()
    {
        Directory.CreateDirectory(_directory);
        var other = Path.Combine(_directory, "audio.json");
        File.WriteAllText(other, "unchanged audio settings");
        var store = new SetupPreferencesStore(StorePath);
        Assert.False(File.Exists(StorePath));
        Assert.Null(store.Save(3));
        Assert.Equal(3, new SetupPreferencesStore(StorePath).Current.Step);
        Assert.Null(store.Save(4, true));
        Assert.True(new SetupPreferencesStore(StorePath).Current.Completed);
        Assert.Equal("unchanged audio settings", File.ReadAllText(other));
    }

    [Fact]
    public void FailedPublicationRetainsPreviousProgressAndCleansTemporaryFile()
    {
        Directory.CreateDirectory(StorePath);
        var store = new SetupPreferencesStore(StorePath);
        var previous = store.Current;
        Assert.NotNull(store.Save(4, true));
        Assert.Same(previous, store.Current);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        Assert.True(Directory.Exists(StorePath));
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("{\"Step\":99,\"Completed\":true}")]
    [InlineData("{\"Step\":0,\"FutureOption\":true}")]
    [InlineData("{\"Completed\":true}")]
    [InlineData("{\"Step\":4,\"Completed\":false,\"Completed\":true}")]
    [InlineData("{\"Step\":0,\"Completed\":true}")]
    public void InvalidStoredProgressNeverClaimsCompletion(string json)
    {
        Directory.CreateDirectory(_directory); File.WriteAllText(StorePath, json);
        var store = new SetupPreferencesStore(StorePath);
        Assert.NotNull(store.Error);
        Assert.False(store.Current.Completed);
        Assert.Equal(json, File.ReadAllText(StorePath));
    }

    [Fact]
    public void CompletionCannotBeSavedForAnEarlierStep()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SetupPreferencesStore(StorePath).Save(2, true));
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void DirectoryAtStorePathIsAnExplicitLoadFailure()
    {
        Directory.CreateDirectory(StorePath);
        Assert.NotNull(new SetupPreferencesStore(StorePath).Error);
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, false)]
    public void FinishRequiresIdleShortcutModelAndUsableMicrophone(bool busy, bool shortcut, bool model, bool microphone)
    {
        Assert.NotNull(SetupReadiness.Validate(busy, shortcut, model, microphone));
    }

    [Fact]
    public void AvailableFallbackAllowsFinishAndMetadataRefreshPreservesWriteError()
    {
        // A disconnected preferred device does not block an available system/default fallback.
        Assert.Null(SetupReadiness.Validate(false, true, true, true));
        var state = new SetupReadiness();
        state.ReportPersistence("Could not save setup progress.");
        Assert.Equal("Could not save setup progress.", state.Message(SetupReadiness.Validate(false, true, true, true)));
        Assert.Equal("Could not save setup progress.", state.Message(SetupReadiness.Validate(true, true, true, true)));
        state.ReportPersistence(null);
        Assert.Contains("ready", state.Message(null));
    }
    [Theory]
    [InlineData(0, false, false, false, false, true)]
    [InlineData(1, false, false, false, true, true)]
    [InlineData(1, false, true, true, false, false)]
    [InlineData(2, false, true, false, true, true)]
    [InlineData(2, false, false, true, true, false)]
    [InlineData(3, true, true, true, true, false)]
    [InlineData(3, false, true, false, true, false)]
    [InlineData(3, false, true, true, true, true)]
    [InlineData(4, false, true, true, false, false)]
    public void StepValidationReportsProblemsWhereTheyCanBeFixed(int step, bool busy, bool shortcut, bool model, bool microphone, bool ready)
    {
        Assert.Equal(ready, SetupReadiness.ValidateStep(step, busy, shortcut, model, microphone) is null);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
