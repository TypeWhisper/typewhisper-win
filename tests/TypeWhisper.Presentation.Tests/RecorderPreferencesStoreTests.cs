using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class RecorderPreferencesStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "recorder-preferences-" + Guid.NewGuid());
    private string SettingsPath => Path.Combine(_directory, "recorder.json");

    [Fact]
    public void MissingFileUsesOriginalDefaultsWithoutWriting()
    {
        var store = new RecorderPreferencesStore(SettingsPath);
        Assert.True(store.Current.MicrophoneEnabled);
        Assert.False(store.Current.SystemAudioEnabled);
        Assert.Null(store.Current.OutputDeviceId);
        Assert.Null(store.Error);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void SourcesAndUnavailableDeviceSurviveReloadWithoutChangingEarlierSnapshot()
    {
        var store = new RecorderPreferencesStore(SettingsPath);
        var snapshot = store.Current;
        var changed = 0;
        store.Changed += () => changed++;
        Assert.Null(store.Save(new() { MicrophoneEnabled = false, SystemAudioEnabled = true, OutputDeviceId = "capture:unavailable-device" }));
        var reloaded = new RecorderPreferencesStore(SettingsPath);
        Assert.Equal(store.Current, reloaded.Current);
        Assert.Equal("capture:unavailable-device", reloaded.Current.OutputDeviceId);
        Assert.Equal(new RecorderPreferences(), snapshot);
        Assert.Equal(1, changed);
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("null")]
    [InlineData("{\"MicrophoneEnabled\":\"yes\"}")]
    [InlineData("{\"OutputDeviceId\":\"bad\\nendpoint\"}")]
    public void InvalidFileUsesDefaultsAndPreservesOriginalBytes(string contents)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, contents);
        var before = File.ReadAllBytes(SettingsPath);
        var store = new RecorderPreferencesStore(SettingsPath);
        Assert.Equal(new RecorderPreferences(), store.Current);
        Assert.NotNull(store.Error);
        Assert.Equal(before, File.ReadAllBytes(SettingsPath));
    }

    [Fact]
    public void FailedAtomicWriteRetainsCurrentReferenceAndCleansTemporaryFile()
    {
        var store = new RecorderPreferencesStore(SettingsPath);
        Assert.Null(store.Save(new() { SystemAudioEnabled = true }));
        var snapshot = store.Current;
        File.Delete(SettingsPath);
        Directory.CreateDirectory(SettingsPath);
        Assert.NotNull(store.Save(new() { MicrophoneEnabled = false }));
        Assert.Same(snapshot, store.Current);
        Assert.True(Directory.Exists(SettingsPath));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void InvalidSelectionPreservesSavedBytesAndDefaultOutputNormalizesToNull()
    {
        var store = new RecorderPreferencesStore(SettingsPath);
        Assert.Null(store.Save(new() { OutputDeviceId = "" }));
        Assert.Null(store.Current.OutputDeviceId);
        var before = File.ReadAllBytes(SettingsPath);
        Assert.NotNull(store.Save(new() { OutputDeviceId = "invalid\nendpoint" }));
        Assert.Equal(before, File.ReadAllBytes(SettingsPath));
        Assert.Null(store.Current.OutputDeviceId);
        Assert.Null(store.Save(new() { MicrophoneEnabled = false, SystemAudioEnabled = false }));
        Assert.False(new RecorderPreferencesStore(SettingsPath).Current.MicrophoneEnabled);
    }

    [Fact]
    public void ObserverFailureDoesNotTurnSuccessfulPersistenceIntoFailure()
    {
        var store = new RecorderPreferencesStore(SettingsPath);
        var notified = false;
        store.Changed += () => throw new InvalidOperationException("observer failed");
        store.Changed += () => notified = true;
        Assert.Null(store.Save(new() { SystemAudioEnabled = true }));
        Assert.True(notified);
        Assert.True(new RecorderPreferencesStore(SettingsPath).Current.SystemAudioEnabled);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
