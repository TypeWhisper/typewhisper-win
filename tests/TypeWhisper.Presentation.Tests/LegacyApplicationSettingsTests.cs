using System.Text.Json;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services;
using TypeWhisper.Presentation;
using Xunit;

namespace TypeWhisper.Presentation.Tests;

public sealed class LegacyApplicationSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "legacy-settings-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public void ConvertsActualLegacySerializationAndPreservesUserChoices()
    {
        var original = new AppSettings
        {
            AutoPaste = false, SaveToHistoryEnabled = false, Mode = Core.Models.RecordingMode.PushToTalk,
            MainDictationHotkeys = ["Ctrl+Shift+F9", "Ctrl+Alt+D"],
            MicrophonePriorityList = [new("device-id", "USB microphone")],
            Language = "de", LanguageHints = ["de", "en"], HasCompletedOnboarding = true,
            SelectedModelId = "plugin:com.typewhisper.groq:whisper-large-v3-turbo",
            TranscriptionNumberNormalizationEnabled = false, HistoryRetentionMode = HistoryRetentionMode.Forever,
            SoundFeedbackEnabled = false, VocabularyBoostingEnabled = true
        };
        var json = JsonSerializer.Serialize(original, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        LegacyApplicationSettings.Write(_root, SettingsService.ParseForMigration(json));
        var output = new DictationOutputPreferencesStore(Path.Combine(_root, "dictation-output.json"));
        Assert.Null(output.Error); Assert.False(output.Current.AutoPaste); Assert.False(output.Current.SaveToHistory);
        Assert.Equal(Presentation.RecordingMode.Hold, new RecordingModePreferencesStore(Path.Combine(_root, "recording-mode.json")).Current);
        Assert.Equal("de,en", new DictationTextPreferencesStore(Path.Combine(_root, "dictation-text.json")).Current.PreferredLanguageHints);
        Assert.Equal("Ctrl+Shift+F9,Ctrl+Alt+D", File.ReadAllText(Path.Combine(_root, "dictation-hotkeys.txt")));
        Assert.True(new SetupPreferencesStore(Path.Combine(_root, "setup.json")).Current.Completed);
        // The installed track decides; the upgrade saves no channel of its own.
        Assert.False(File.Exists(Path.Join(_root, "updates.json")));
        var updates = new AppUpdatePreferences(Path.Join(_root, "updates.json"), "1.2.0");
        Assert.Equal(AppUpdateChannel.Stable, updates.Channel);
        Assert.False(updates.HasExplicitChoice);
        using var selection = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "Dictation/settings.json")));
        Assert.Equal("com.typewhisper.groq", selection.RootElement.GetProperty("Provider").GetString());
    }
    [Fact]
    public void LegacyCommaKeyDoesNotSplitTheShortcutList()
    {
        LegacyApplicationSettings.Write(_root, new AppSettings { MainDictationHotkeys = ["Ctrl+,", "Ctrl+\\"] });
        Assert.Equal("Ctrl+Comma,Ctrl+\\", File.ReadAllText(Path.Combine(_root, "dictation-hotkeys.txt")));
    }
    [Fact]
    public void UnknownModelDoesNotSelectDefaultCloudOrLocalProvider()
    {
        LegacyApplicationSettings.Write(_root, new AppSettings { SelectedModelId = "old-unknown-model" });
        Assert.Contains("legacy-unavailable", File.ReadAllText(Path.Combine(_root, "Dictation/settings.json")));
    }
    [Theory]
    [InlineData(true, 8978, false, 8978)]
    [InlineData(true, 9123, true, 9123)]
    [InlineData(false, 80, false, 8978)]
    public void LocalApiChoiceIsCarriedOver(bool enabled, int port, bool authentication, int expectedPort)
    {
        LegacyApplicationSettings.Write(_root, new AppSettings
        { ApiServerEnabled = enabled, ApiServerPort = port, ApiServerRequiresAuthentication = authentication });
        // Same shape and default (case-sensitive) options as the WinUI HTTP API settings reader.
        var api = JsonSerializer.Deserialize<HttpApiPreferences>(File.ReadAllText(Path.Combine(_root, "http-api.json")))!;
        Assert.Equal(new HttpApiPreferences(enabled, expectedPort, authentication), api);
    }
    private sealed record HttpApiPreferences(bool Enabled = false, int Port = 8978, bool RequireAuthentication = false);
    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"autoPaste\":true,\"autoPaste\":false}")]
    public void InvalidSettingsAreNotSilentlyReplacedWithDefaults(string json) =>
        Assert.Throws<JsonException>(() => SettingsService.ParseForMigration(json));
    // An upgrade before this release wrote "Daily" first and the report last; a choice saved in the app comes later.
    private (string Updates, string Report) MigratedProfile(DateTime upgradedAt, DateTime updatesWrittenAt, string channel = "\"Daily\"")
    {
        Directory.CreateDirectory(_root);
        var updates = Path.Join(_root, "updates.json");
        var report = Path.Join(_root, "legacy-migration-report.json");
        File.WriteAllText(updates, channel);
        File.WriteAllText(report, "{}");
        File.SetLastWriteTimeUtc(updates, updatesWrittenAt);
        File.SetLastWriteTimeUtc(report, upgradedAt);
        return (updates, report);
    }
    private static readonly DateTime UpgradedAt = new(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("1.2.1")]
    [InlineData("1.3.0-rc.1")]
    public void MigratedDailyChannelIsRemovedFromStableAndCandidateInstallations(string version)
    {
        var (updates, report) = MigratedProfile(UpgradedAt, UpgradedAt.AddMinutes(-3));
        Assert.True(LegacyApplicationSettings.RepairMigratedUpdateChannel(updates, report, version));
        Assert.False(File.Exists(updates));
        var preferences = new AppUpdatePreferences(updates, version);
        Assert.False(preferences.HasExplicitChoice);
        Assert.NotEqual(AppUpdateChannel.Daily, preferences.Channel);
        // Once removed there is nothing left to repair.
        Assert.False(LegacyApplicationSettings.RepairMigratedUpdateChannel(updates, report, version));
    }

    [Fact]
    public void DailyChosenInTheAppAfterTheUpgradeIsKept()
    {
        var (updates, report) = MigratedProfile(UpgradedAt, UpgradedAt.AddDays(1));
        Assert.False(LegacyApplicationSettings.RepairMigratedUpdateChannel(updates, report, "1.2.1"));
        Assert.Equal(AppUpdateChannel.Daily, new AppUpdatePreferences(updates, "1.2.1").Channel);
    }

    [Theory]
    [InlineData("\"Stable\"", "1.2.1")]
    [InlineData("\"ReleaseCandidate\"", "1.2.1")]
    [InlineData("\"Daily\"", "1.3.0-daily.20261112.155")]
    public void OtherChoicesAndDailyInstallationsAreKept(string channel, string version)
    {
        var (updates, report) = MigratedProfile(UpgradedAt, UpgradedAt.AddMinutes(-3), channel);
        Assert.False(LegacyApplicationSettings.RepairMigratedUpdateChannel(updates, report, version));
        Assert.Equal(channel, File.ReadAllText(updates));
    }

    [Fact]
    public void ProfilesWithoutAnUpgradeReportAreKept()
    {
        var (updates, report) = MigratedProfile(UpgradedAt, UpgradedAt.AddMinutes(-3));
        File.Delete(report);
        Assert.False(LegacyApplicationSettings.RepairMigratedUpdateChannel(updates, report, "1.2.1"));
        Assert.True(File.Exists(updates));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
