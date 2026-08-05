namespace TypeWhisper.PluginSystem.Tests;

public sealed class DictationOutputPersistenceTests
{
    [Fact]
    public void ProcessSingleJobAsync_PersistsCompletedTextBeforeOutputDelivery()
    {
        var processJob = ReadProcessSingleJobAsync();

        var finalTextIndex = processJob.IndexOf("var finalText = pipelineResult.Text;", StringComparison.Ordinal);
        var recentIndex = processJob.IndexOf("_recentTranscriptions.RecordTranscription(", StringComparison.Ordinal);
        var historyIndex = processJob.IndexOf("_history.TryAddRecord(historyRecord)", StringComparison.Ordinal);
        var actionOutputIndex = processJob.IndexOf("actionPlugin.ExecuteAsync(", StringComparison.Ordinal);
        var actionSuccessCheckIndex = processJob.IndexOf("if (!actionResult.Success)", StringComparison.Ordinal);
        var pasteOutputIndex = processJob.IndexOf("_textInsertion.InsertTextAsync(", StringComparison.Ordinal);
        var recoveryDecisionIndex = processJob.LastIndexOf("await job.RecoveryLease.DiscardAsync()", StringComparison.Ordinal);

        Assert.True(finalTextIndex >= 0, "The job must finish post-processing before persisting output.");
        Assert.True(recentIndex > finalTextIndex, "Recent transcription persistence should use the final post-processed text.");
        Assert.True(historyIndex > finalTextIndex, "History persistence should use the final post-processed text.");
        Assert.True(recentIndex < actionOutputIndex, "Recent transcription persistence must happen before action-plugin output.");
        Assert.True(historyIndex < actionOutputIndex, "History persistence must happen before action-plugin output.");
        Assert.True(recentIndex < pasteOutputIndex, "Recent transcription persistence must happen before paste/clipboard output.");
        Assert.True(historyIndex < pasteOutputIndex, "History persistence must happen before paste/clipboard output.");
        Assert.True(actionSuccessCheckIndex > actionOutputIndex, "Action output must be confirmed before the job succeeds.");
        Assert.True(recoveryDecisionIndex > actionSuccessCheckIndex, "Recovery audio must remain pending until action output succeeds.");
    }

    [Fact]
    public void ProcessSingleJobAsync_UsesInsertionTextOnlyForDirectTextInsertion()
    {
        var processJob = ReadProcessSingleJobAsync();
        var normalizedProcessJob = processJob.Replace("\r\n", "\n");

        var finalTextIndex = processJob.IndexOf("var finalText = pipelineResult.Text;", StringComparison.Ordinal);
        var insertionTextIndex = processJob.IndexOf(
            "var insertionText = DictationInsertionTextFormatter.TextForInsertion(finalText);",
            StringComparison.Ordinal);
        var directInsertionArgs = System.Text.RegularExpressions.Regex
            .Matches(processJob, @"_textInsertion\.InsertTextAsync\(\s*(?<arg>\w+),")
            .Select(match => match.Groups["arg"].Value)
            .ToArray();
        var actionOutputIndex = processJob.IndexOf("actionPlugin.ExecuteAsync(finalText,", StringComparison.Ordinal);
        var eventTextAssignmentIndex = processJob.IndexOf("textInsertedEventText = insertionText;", StringComparison.Ordinal);
        var eventIndex = processJob.IndexOf("Text = textInsertedEventText,", StringComparison.Ordinal);

        Assert.True(insertionTextIndex > finalTextIndex, "Insertion text must be derived after post-processing.");
        Assert.Equal(["insertionText", "insertionText"], directInsertionArgs);
        Assert.True(actionOutputIndex > 0, "Action plugins should continue receiving the clean final text.");
        Assert.True(eventTextAssignmentIndex > insertionTextIndex, "Direct insertion should capture the inserted text for the event.");
        Assert.True(eventIndex > eventTextAssignmentIndex, "TextInsertedEvent should report the text that was inserted.");
        Assert.Contains("LastTranscribedText = finalText;", processJob);
        Assert.Contains("CompleteApiDictationSession(job.ApiSessionId, new ApiDictationTranscription(\n                finalText,", normalizedProcessJob);
        Assert.Contains("_recentTranscriptions.RecordTranscription(\n                recordId,\n                finalText,", normalizedProcessJob);
        Assert.Contains("FinalText = finalText,", processJob);
        Assert.Contains("_speechFeedback.AnnounceTranscriptionComplete(finalText, detectedLanguage);", processJob);
    }

    [Fact]
    public void ProcessSingleJobAsync_PreservesCompletedApiSessionWhenOutputDeliveryFails()
    {
        var processJob = ReadProcessSingleJobAsync();

        var completionIndex = processJob.IndexOf(
            "CompleteApiDictationSession(job.ApiSessionId,",
            StringComparison.Ordinal);
        Assert.True(completionIndex >= 0, "API dictation sessions must be completed before output delivery.");

        var catchIndex = processJob.IndexOf("catch (Exception ex)", completionIndex, StringComparison.Ordinal);
        Assert.True(catchIndex > completionIndex, "The output failure path should remain after session completion.");

        var completedGuardIndex = processJob.IndexOf(
            "GetApiDictationSession(completedApiSessionId)?.Status == ApiDictationSessionStatus.Completed",
            catchIndex,
            StringComparison.Ordinal);
        Assert.True(completedGuardIndex > catchIndex, "Output failures must check for an already-completed API session.");

        var failIndex = processJob.IndexOf(
            "FailApiDictationSession(job.ApiSessionId, failureMessage);",
            catchIndex,
            StringComparison.Ordinal);

        Assert.True(failIndex > catchIndex, "The output failure path must still fail unfinished API sessions.");
        Assert.True(completedGuardIndex < failIndex, "The completed-session guard must run before marking a session failed.");
        Assert.Contains("if (!apiSessionAlreadyCompleted)", processJob);
    }

    [Fact]
    public void ProcessSingleJobAsync_DiscardsOnlyExplicitCancellationAndPreservesOtherFailures()
    {
        var processJob = ReadProcessSingleJobAsync();

        Assert.Contains(
            "catch (OperationCanceledException) when (ct.IsCancellationRequested)",
            processJob);
        Assert.Contains("await job.RecoveryLease.DiscardAsync();", processJob);
        Assert.Contains("recoveryDescriptor = await job.RecoveryLease.PreserveAsync();", processJob);
    }

    [Fact]
    public void HistoryAudioHelpers_GenerateNamesInternallyAndValidateNamesBeforeDeleting()
    {
        var source = ReadDictationViewModel();
        var writeAudio = TestFile.ExtractBlock(source, "private static async Task<string?> WriteHistoryAudioAsync", 1200);
        var deleteAudio = TestFile.ExtractBlock(source, "private static void DeleteHistoryAudio", 1400);

        Assert.Contains("var fileName = $\"{Guid.NewGuid():N}.wav\";", writeAudio);
        Assert.Contains("Path.Combine(TypeWhisperEnvironment.AudioPath, fileName)", writeAudio);
        Assert.Contains(
            "!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)",
            deleteAudio);
        Assert.Contains("Path.IsPathRooted(fileName)", deleteAudio);
        Assert.Contains("Path.GetFullPath(Path.Combine(audioRoot, fileName))", deleteAudio);
        Assert.Contains("string.Equals(parent, audioRoot, StringComparison.OrdinalIgnoreCase)", deleteAudio);
    }

    [Fact]
    public void ProcessSingleJobAsync_AudioHistorySaveOnlyCatchesExpectedIoFailures()
    {
        var source = ReadDictationViewModel();
        var audioSaveBlock = TestFile.ExtractBlock(
            source,
            "private static async Task<string?> WriteHistoryAudioAsync",
            1200);

        Assert.Contains("catch (IOException)", audioSaveBlock);
        Assert.Contains("catch (UnauthorizedAccessException)", audioSaveBlock);
        Assert.DoesNotMatch(@"catch\s*\{", audioSaveBlock);
    }

    private static string ReadProcessSingleJobAsync()
    {
        var source = ReadDictationViewModel();

        return TestFile.ExtractBlock(source, "private async Task ProcessSingleJobAsync", 26000);
    }

    private static string ReadDictationViewModel() =>
        TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "ViewModels",
            "DictationViewModel.cs");
}
