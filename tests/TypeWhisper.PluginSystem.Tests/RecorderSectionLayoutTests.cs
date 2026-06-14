using System.IO;
using System.Text.Json;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class RecorderSectionLayoutTests
{
    private static readonly string[] RecorderExplanationKeys =
    [
        "Recorder.SourcesTitle",
        "Recorder.SourcesHint",
        "Recorder.MicrophoneHint",
        "Recorder.SystemAudioHint",
        "Recorder.SystemAudioOutputHint",
        "Recorder.OutputTitle",
        "Recorder.OutputHint",
        "Recorder.FormatHint",
        "Recorder.TracksHint",
        "Recorder.DuckingHint",
        "Recorder.TranscriptionHint",
        "Recorder.ModeTitle",
        "Recorder.ModeHint",
        "Recorder.CaptureTitle",
        "Recorder.CaptureHint",
        "Recorder.RecordingsHint"
    ];

    [Fact]
    public void RecorderSection_GroupsRecorderControlsWithInlineExplanations()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "RecorderSection.xaml");

        Assert.Contains("MaxWidth=\"820\"", xaml);
        Assert.Contains("SettingsGroupPanelStyle", xaml);
        Assert.Contains("Recorder.SourcesTitle", xaml);
        Assert.Contains("Recorder.OutputTitle", xaml);
        Assert.Contains("Recorder.CaptureTitle", xaml);
        Assert.Contains("Recorder.RecordingsHint", xaml);
        Assert.Contains("Recorder.MicrophoneHint", xaml);
        Assert.Contains("Recorder.SystemAudioHint", xaml);
        Assert.Contains("Recorder.SystemAudioOutputHint", xaml);
        Assert.Contains("Recorder.FormatHint", xaml);
        Assert.Contains("Recorder.TracksHint", xaml);
        Assert.Contains("Recorder.DuckingHint", xaml);
        Assert.Contains("Recorder.TranscriptionHint", xaml);
        Assert.Contains("Recorder.ModeTitle", xaml);
        Assert.Contains("Recorder.ModeHint", xaml);
        Assert.DoesNotContain("HorizontalAlignment=\"Center\" Margin=\"0,0,0,24\"", xaml);
        Assert.DoesNotContain("MaxWidth=\"600\"", xaml);
    }

    [Fact]
    public void RecorderSection_UsesProcessingActionIconForTranscriptionRetry()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "RecorderSection.xaml");
        var transcribeButton = TestFile.ExtractBlock(xaml, "TranscribeRecordingCommand", 420);

        Assert.DoesNotContain("Symbol=\"Play24\"", transcribeButton);
        Assert.Contains("Text=\"&#xE72C;\"", transcribeButton);
        Assert.Contains("FontFamily=\"Segoe MDL2 Assets\"", transcribeButton);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("ja")]
    [InlineData("ru")]
    public void RecorderSection_ExplanationTextIsLocalized(string language)
    {
        var localization = LoadLocalization(language);

        foreach (var key in RecorderExplanationKeys)
        {
            Assert.True(localization.TryGetValue(key, out var value), $"{language} should define {key}.");
            Assert.False(string.IsNullOrWhiteSpace(value), $"{language} value for {key} should not be empty.");
        }
    }

    private static Dictionary<string, string> LoadLocalization(string language)
    {
        var path = TestFile.ProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Resources",
            "Localization",
            $"{language}.json");
        var json = File.ReadAllText(path);
        var localization = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

        Assert.NotNull(localization);
        return localization;
    }
}
