using TypeWhisper.Presentation;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class DictationRecoveryPreferencesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tw-recovery-preferences-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_root, "recovery.json");
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public void MissingPreferencesDoNotEnableOrWrite()
    {
        var store = new DictationRecoveryPreferencesStore(FilePath);
        Assert.False(store.Current.Enabled);
        Assert.Null(store.Error);
        Assert.False(Directory.Exists(_root));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(7)] [InlineData(30)]
    [InlineData(60)] [InlineData(90)] [InlineData(180)]
    public void ExplicitChoiceSurvivesRestartAndDisablingDoesNotDeleteAudio(int days)
    {
        var store = new DictationRecoveryPreferencesStore(FilePath);
        Assert.True(store.Save(new() { Enabled = true, RetentionDays = days }));
        var restarted = new DictationRecoveryPreferencesStore(FilePath);
        Assert.True(restarted.Current.Enabled);
        Assert.Equal(days, restarted.Current.RetentionDays);
        var audio = Path.Combine(_root, "existing.wav"); File.WriteAllText(audio, "existing audio");
        Assert.True(restarted.Save(restarted.Current with { Enabled = false }));
        Assert.Equal("existing audio", File.ReadAllText(audio));
        Assert.False(new DictationRecoveryPreferencesStore(FilePath).Current.Enabled);
    }

    [Theory]
    [InlineData("{\"Version\":2,\"Enabled\":true,\"RetentionDays\":30}")]
    [InlineData("{\"Version\":1,\"Enabled\":true,\"enabled\":false,\"RetentionDays\":30}")]
    [InlineData("{\"Version\":1,\"Enabled\":true,\"RetentionDays\":-1}")]
    [InlineData("{\"Version\":1,\"Enabled\":true}")]
    [InlineData("[]")]
    [InlineData("{\"Version\":1,\"Enabled\":true,\"RetentionDays\":30,\"Unknown\":true}")]
    public void InvalidPreferencesRemainUntouchedAndDisabled(string json)
    {
        Directory.CreateDirectory(_root); File.WriteAllText(FilePath, json);
        var store = new DictationRecoveryPreferencesStore(FilePath);
        Assert.False(store.Current.Enabled); Assert.NotNull(store.Error);
        Assert.Equal(json, File.ReadAllText(FilePath));
    }

    [Fact]
    public void FailedWriteKeepsPreviousChoiceAndCleansTemporaryFile()
    {
        var store = new DictationRecoveryPreferencesStore(FilePath);
        Assert.True(store.Save(new() { Enabled = true, RetentionDays = 7 }));
        File.Delete(FilePath); Directory.CreateDirectory(FilePath);
        Assert.False(store.Save(new() { Enabled = false }));
        Assert.True(store.Current.Enabled); Assert.Equal(7, store.Current.RetentionDays);
        Assert.NotNull(store.Error); Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void PermissionRequiresBothStartAndCurrentOptIn()
    {
        var off = new DictationRecoveryPreferences(); var on = off with { Enabled = true };
        Assert.False(off.CanPreserveWith(on)); Assert.False(on.CanPreserveWith(off));
        Assert.True(on.CanPreserveWith(on));
        Assert.False(on.CanPreserveWith(on with { RetentionDays = -1 }));
    }

    [Fact]
    public void InvalidSavePreservesExistingBytesAndEnabledState()
    {
        var store = new DictationRecoveryPreferencesStore(FilePath);
        Assert.True(store.Save(new() { Enabled = true }));
        var before = File.ReadAllBytes(FilePath);
        Assert.False(store.Save(new() { Enabled = false, RetentionDays = -1 }));
        Assert.Equal(before, File.ReadAllBytes(FilePath)); Assert.True(store.Current.Enabled);
    }

    [Fact]
    public void OversizedFileIsPreservedWithoutEnablingRecovery()
    {
        Directory.CreateDirectory(_root); var text = new string(' ', 4097); File.WriteAllText(FilePath, text);
        var store = new DictationRecoveryPreferencesStore(FilePath);
        Assert.False(store.Current.Enabled); Assert.NotNull(store.Error); Assert.Equal(text, File.ReadAllText(FilePath));
    }
}
