namespace TypeWhisper.PluginSystem.Tests;

public sealed class DictationPasteTargetTests
{
    [Fact]
    public void RecordingStart_CapturesExactFieldOnlyWhenOptionAndAutoPasteAreEnabled()
    {
        var source = ReadDictationViewModel();
        var startRecording = TestFile.ExtractBlock(source, "private async Task StartRecording()", 16000);

        Assert.Contains("settingsSnapshot.AutoPaste && settingsSnapshot.LockPasteToFocusedField", startRecording);
        Assert.Contains("_textInsertion.CaptureTarget(_capturedWindowHandle)", startRecording);
        Assert.Contains(": null;", startRecording);
    }

    [Fact]
    public void TranscriptionJob_CarriesCapturedFieldToBothDirectInsertionPaths()
    {
        var source = ReadDictationViewModel();
        var processJob = TestFile.ExtractBlock(source, "private async Task ProcessSingleJobAsync", 26000);

        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(
            processJob,
            @"job\.CapturedTextInsertionTarget,").Count);
        Assert.Contains("TextInsertionTarget? CapturedTextInsertionTarget", source);
    }

    private static string ReadDictationViewModel() =>
        TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "ViewModels",
            "DictationViewModel.cs");
}
