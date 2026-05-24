namespace TypeWhisper.PluginSystem.Tests;

public sealed class DictationOutputPersistenceTests
{
    [Fact]
    public void ProcessSingleJobAsync_PersistsCompletedTextBeforeOutputDelivery()
    {
        var processJob = ReadProcessSingleJobAsync();

        var finalTextIndex = processJob.IndexOf("var finalText = pipelineResult.Text;", StringComparison.Ordinal);
        var recentIndex = processJob.IndexOf("_recentTranscriptions.RecordTranscription(", StringComparison.Ordinal);
        var historyIndex = processJob.IndexOf("_history.AddRecord(", StringComparison.Ordinal);
        var actionOutputIndex = processJob.IndexOf("actionPlugin.ExecuteAsync(", StringComparison.Ordinal);
        var pasteOutputIndex = processJob.IndexOf("_textInsertion.InsertTextAsync(", StringComparison.Ordinal);

        Assert.True(finalTextIndex >= 0, "The job must finish post-processing before persisting output.");
        Assert.True(recentIndex > finalTextIndex, "Recent transcription persistence should use the final post-processed text.");
        Assert.True(historyIndex > finalTextIndex, "History persistence should use the final post-processed text.");
        Assert.True(recentIndex < actionOutputIndex, "Recent transcription persistence must happen before action-plugin output.");
        Assert.True(historyIndex < actionOutputIndex, "History persistence must happen before action-plugin output.");
        Assert.True(recentIndex < pasteOutputIndex, "Recent transcription persistence must happen before paste/clipboard output.");
        Assert.True(historyIndex < pasteOutputIndex, "History persistence must happen before paste/clipboard output.");
    }

    [Fact]
    public void ProcessSingleJobAsync_PreservesCompletedApiSessionWhenOutputDeliveryFails()
    {
        var processJob = ReadProcessSingleJobAsync();

        var completionIndex = processJob.IndexOf(
            "CompleteApiDictationSession(job.ApiSessionId,",
            StringComparison.Ordinal);
        var catchIndex = processJob.IndexOf("catch (Exception ex)", completionIndex, StringComparison.Ordinal);
        var completedGuardIndex = processJob.IndexOf(
            "GetApiDictationSession(completedApiSessionId)?.Status == ApiDictationSessionStatus.Completed",
            catchIndex,
            StringComparison.Ordinal);
        var failIndex = processJob.IndexOf(
            "FailApiDictationSession(job.ApiSessionId, ex.Message);",
            catchIndex,
            StringComparison.Ordinal);

        Assert.True(completionIndex >= 0, "API dictation sessions must be completed before output delivery.");
        Assert.True(catchIndex > completionIndex, "The output failure path should remain after session completion.");
        Assert.True(completedGuardIndex > catchIndex, "Output failures must check for an already-completed API session.");
        Assert.True(completedGuardIndex < failIndex, "The completed-session guard must run before marking a session failed.");
        Assert.Contains("if (!apiSessionAlreadyCompleted)", processJob);
    }

    [Fact]
    public void ProcessSingleJobAsync_ConstrainsAudioHistoryFileNameBeforeCombining()
    {
        var processJob = ReadProcessSingleJobAsync();

        Assert.Contains(
            "Path.Combine(TypeWhisperEnvironment.AudioPath, Path.GetFileName(audioFileName))",
            processJob);
    }

    [Fact]
    public void ProcessSingleJobAsync_AudioHistorySaveOnlyCatchesExpectedIoFailures()
    {
        var processJob = ReadProcessSingleJobAsync();

        Assert.Contains("catch (IOException)", processJob);
        Assert.Contains("catch (UnauthorizedAccessException)", processJob);
        Assert.DoesNotContain("catch\r\n                {", processJob);
        Assert.DoesNotContain("catch\n                {", processJob);
    }

    private static string ReadProcessSingleJobAsync()
    {
        var source = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "ViewModels",
            "DictationViewModel.cs");

        return TestFile.ExtractBlock(source, "private async Task ProcessSingleJobAsync", 26000);
    }
}
