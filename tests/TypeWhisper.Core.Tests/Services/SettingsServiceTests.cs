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
            LocalModelAcceleration = AppSettings.LocalModelAccelerationNvidiaCuda,
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
            WorkflowPaletteHotkey = "Ctrl+Alt+W",
            TranscribeShortQuietClipsAggressively = true,
            IndicatorStyle = IndicatorStyle.EdgeDock,
            LiveTranscriptionEnabled = false,
            LiveTranscriptionFontSize = 15.5,
            PreviewBubbleAutoHideMilliseconds = 3750,
            SelectedIndustryPresetId = "architecture"
        };

        sut.Save(settings);

        var sut2 = new SettingsService(_filePath);
        Assert.Equal("de", sut2.Current.Language);
        Assert.True(sut2.Current.HasCompletedOnboarding);
        Assert.True(sut2.Current.VocabularyBoostingEnabled);
        Assert.Equal("groq", sut2.Current.FileTranscriptionEngineOverride);
        Assert.Equal("whisper-large-v3", sut2.Current.FileTranscriptionModelOverride);
        Assert.Equal(AppSettings.LocalModelAccelerationNvidiaCuda, sut2.Current.LocalModelAcceleration);
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
        Assert.Equal("Ctrl+Alt+W", sut2.Current.WorkflowPaletteHotkey);
        Assert.True(sut2.Current.TranscribeShortQuietClipsAggressively);
        Assert.Equal(IndicatorStyle.EdgeDock, sut2.Current.IndicatorStyle);
        Assert.False(sut2.Current.LiveTranscriptionEnabled);
        Assert.Equal(15.5, sut2.Current.LiveTranscriptionFontSize);
        Assert.Equal(3750, sut2.Current.PreviewBubbleAutoHideMilliseconds);
        Assert.Equal("architecture", sut2.Current.SelectedIndustryPresetId);
    }

    [Fact]
    public void Load_UnknownLocalModelAcceleration_FallsBackToAuto()
    {
        File.WriteAllText(_filePath, """
        {
          "language": "en",
          "localModelAcceleration": "directml"
        }
        """);

        var sut = new SettingsService(_filePath);

        Assert.Equal(AppSettings.LocalModelAccelerationAuto, sut.Current.LocalModelAcceleration);
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
        Assert.Equal("", sut.Current.WorkflowPaletteHotkey);
    }

    [Fact]
    public void Load_LegacySettings_UsesStatusIslandIndicatorDefault()
    {
        File.WriteAllText(_filePath, """
        {
          "language": "en"
        }
        """);

        var sut = new SettingsService(_filePath);

        Assert.Equal(IndicatorStyle.StatusIsland, sut.Current.IndicatorStyle);
    }

    [Fact]
    public void Load_LegacySettings_UsesDefaultLiveTranscriptionFontSize()
    {
        File.WriteAllText(_filePath, """
        {
          "language": "en"
        }
        """);

        var sut = new SettingsService(_filePath);

        Assert.Equal(AppSettings.Default.LiveTranscriptionFontSize, sut.Current.LiveTranscriptionFontSize);
    }

    [Theory]
    [InlineData(2.0, 10.0)]
    [InlineData(44.0, 18.0)]
    public void Load_OutOfRangeLiveTranscriptionFontSize_ClampsToSupportedRange(double value, double expected)
    {
        File.WriteAllText(_filePath, $$"""
        {
          "language": "en",
          "liveTranscriptionFontSize": {{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}}
        }
        """);

        var sut = new SettingsService(_filePath);

        Assert.Equal(expected, sut.Current.LiveTranscriptionFontSize);
    }

    [Fact]
    public void Load_UnknownIndicatorStyle_FallsBackToStatusIsland()
    {
        File.WriteAllText(_filePath, """
        {
          "language": "en",
          "indicatorStyle": 999
        }
        """);

        var sut = new SettingsService(_filePath);

        Assert.Equal(IndicatorStyle.StatusIsland, sut.Current.IndicatorStyle);
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
