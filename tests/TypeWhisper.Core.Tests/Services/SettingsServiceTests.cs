using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;

namespace TypeWhisper.Core.Tests.Services;

public class SettingsServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _filePath;

    public SettingsServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"tw_settings_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _filePath = Path.Combine(_tempDir, "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void Load_NoFile_ReturnsDefaults()
    {
        var sut = new SettingsService(_filePath);

        Assert.Equal(AppSettings.Default.Language, sut.Current.Language);
        Assert.False(sut.Current.HasCompletedOnboarding);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips()
    {
        var sut = new SettingsService(_filePath);
        var settings = AppSettings.Default with
        {
            Language = "de",
            HasCompletedOnboarding = true,
            VocabularyBoostingEnabled = true,
            FileTranscriptionEngineOverride = "groq",
            FileTranscriptionModelOverride = "whisper-large-v3",
            WatchFolderPath = @"C:\Watch",
            WatchFolderOutputPath = @"C:\Output",
            WatchFolderOutputFormat = "srt",
            WatchFolderAutoStart = true,
            WatchFolderDeleteSource = true,
            WatchFolderLanguage = "en",
            WatchFolderEngineOverride = "mock",
            WatchFolderModelOverride = "tiny",
            RecentTranscriptionsHotkey = "Ctrl+Alt+H",
            CopyLastTranscriptionHotkey = "Ctrl+Alt+C",
            TranscribeShortQuietClipsAggressively = true
        };

        sut.Save(settings);

        var sut2 = new SettingsService(_filePath);
        Assert.Equal("de", sut2.Current.Language);
        Assert.True(sut2.Current.HasCompletedOnboarding);
        Assert.True(sut2.Current.VocabularyBoostingEnabled);
        Assert.Equal("groq", sut2.Current.FileTranscriptionEngineOverride);
        Assert.Equal("whisper-large-v3", sut2.Current.FileTranscriptionModelOverride);
        Assert.Equal(@"C:\Watch", sut2.Current.WatchFolderPath);
        Assert.Equal(@"C:\Output", sut2.Current.WatchFolderOutputPath);
        Assert.Equal("srt", sut2.Current.WatchFolderOutputFormat);
        Assert.True(sut2.Current.WatchFolderAutoStart);
        Assert.True(sut2.Current.WatchFolderDeleteSource);
        Assert.Equal("en", sut2.Current.WatchFolderLanguage);
        Assert.Equal("mock", sut2.Current.WatchFolderEngineOverride);
        Assert.Equal("tiny", sut2.Current.WatchFolderModelOverride);
        Assert.Equal("Ctrl+Alt+H", sut2.Current.RecentTranscriptionsHotkey);
        Assert.Equal("Ctrl+Alt+C", sut2.Current.CopyLastTranscriptionHotkey);
        Assert.True(sut2.Current.TranscribeShortQuietClipsAggressively);
    }

    [Fact]
    public void Load_LegacySettings_UsesEmptyRecentTranscriptionHotkeyDefaults()
    {
        File.WriteAllText(_filePath, """
        {
          "language": "en"
        }
        """);

        var sut = new SettingsService(_filePath);

        Assert.Equal("", sut.Current.RecentTranscriptionsHotkey);
        Assert.Equal("", sut.Current.CopyLastTranscriptionHotkey);
    }

    [Fact]
    public void Save_CreatesBackupFile()
    {
        var sut = new SettingsService(_filePath);
        var first = AppSettings.Default with { Language = "en" };
        sut.Save(first);

        var second = AppSettings.Default with { Language = "fr" };
        sut.Save(second);

        var bakPath = _filePath + ".bak";
        Assert.True(File.Exists(bakPath));

        var bakJson = File.ReadAllText(bakPath);
        Assert.Contains("en", bakJson);
    }

    [Fact]
    public void Save_DoesNotLeaveTemp()
    {
        var sut = new SettingsService(_filePath);
        sut.Save(AppSettings.Default with { Language = "de" });

        Assert.False(File.Exists(_filePath + ".tmp"));
    }

    [Fact]
    public void Load_CorruptPrimary_FallsBackToBackup()
    {
        // Write valid backup
        var backup = AppSettings.Default with { Language = "de", HasCompletedOnboarding = true };
        var json = JsonSerializer.Serialize(backup, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        File.WriteAllText(_filePath + ".bak", json);

        // Write corrupt primary
        File.WriteAllText(_filePath, "{{not valid json!!");

        var sut = new SettingsService(_filePath);

        Assert.Equal("de", sut.Current.Language);
        Assert.True(sut.Current.HasCompletedOnboarding);
    }

    [Fact]
    public void Load_CorruptPrimaryAndBackup_ReturnsDefaults()
    {
        File.WriteAllText(_filePath, "{{corrupt}}");
        File.WriteAllText(_filePath + ".bak", "{{also corrupt}}");

        var sut = new SettingsService(_filePath);

        Assert.Equal(AppSettings.Default.Language, sut.Current.Language);
    }

    [Fact]
    public void Load_CorruptPrimary_RestoresPrimaryFromBackup()
    {
        var backup = AppSettings.Default with { Language = "de" };
        var json = JsonSerializer.Serialize(backup, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        File.WriteAllText(_filePath + ".bak", json);
        File.WriteAllText(_filePath, "{{corrupt}}");

        _ = new SettingsService(_filePath);

        // Primary should now be restored from backup
        var primaryJson = File.ReadAllText(_filePath);
        Assert.Contains("de", primaryJson);
    }

    [Fact]
    public void Save_FiresSettingsChangedEvent()
    {
        var sut = new SettingsService(_filePath);
        AppSettings? received = null;
        sut.SettingsChanged += s => received = s;

        var settings = AppSettings.Default with { Language = "es" };
        sut.Save(settings);

        Assert.NotNull(received);
        Assert.Equal("es", received!.Language);
    }

    [Fact]
    public void Load_LegacyHistoryRetentionDays_MigratesToMinutes()
    {
        File.WriteAllText(_filePath, """
        {
          "language": "en",
          "historyRetentionDays": 7
        }
        """);

        var sut = new SettingsService(_filePath);

        Assert.Equal(HistoryRetentionMode.Duration, sut.Current.HistoryRetentionMode);
        Assert.Equal(7 * 24 * 60, sut.Current.HistoryRetentionMinutes);
    }

    [Fact]
    public void Load_LegacyForeverRetention_MigratesToExplicitMode()
    {
        File.WriteAllText(_filePath, """
        {
          "language": "en",
          "historyRetentionDays": 9999
        }
        """);

        var sut = new SettingsService(_filePath);

        Assert.Equal(HistoryRetentionMode.Forever, sut.Current.HistoryRetentionMode);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsMinuteBasedRetention()
    {
        var sut = new SettingsService(_filePath);
        sut.Save(AppSettings.Default with
        {
            HistoryRetentionMode = HistoryRetentionMode.Duration,
            HistoryRetentionMinutes = 60
        });

        var loaded = new SettingsService(_filePath);

        Assert.Equal(HistoryRetentionMode.Duration, loaded.Current.HistoryRetentionMode);
        Assert.Equal(60, loaded.Current.HistoryRetentionMinutes);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsUntilAppClosesMode()
    {
        var sut = new SettingsService(_filePath);
        sut.Save(AppSettings.Default with
        {
            HistoryRetentionMode = HistoryRetentionMode.UntilAppCloses
        });

        var loaded = new SettingsService(_filePath);

        Assert.Equal(HistoryRetentionMode.UntilAppCloses, loaded.Current.HistoryRetentionMode);
    }
}
