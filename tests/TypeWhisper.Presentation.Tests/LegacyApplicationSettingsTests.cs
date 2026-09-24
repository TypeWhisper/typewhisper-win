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
        Assert.Equal(AppUpdateChannel.Daily, new AppUpdatePreferences(Path.Combine(_root, "updates.json"), "1.1.0").Channel);
        using var selection = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "Dictation/settings.json")));
        Assert.Equal("com.typewhisper.groq", selection.RootElement.GetProperty("Provider").GetString());
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
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
