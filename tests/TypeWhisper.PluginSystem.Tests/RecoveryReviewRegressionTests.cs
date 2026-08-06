using System.Text.RegularExpressions;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class RecoveryReviewRegressionTests
{
    [Fact]
    public void HistoryAtomicDefaultsFailClosed()
    {
        var source = TestFile.ReadProjectFile(
            "src", "TypeWhisper.Core", "Interfaces", "IHistoryService.cs");

        Assert.Contains("bool TryAddRecord(TranscriptionRecord record) => false;", source);
        Assert.Contains("bool TryReplaceRecord(TranscriptionRecord record) => false;", source);
        Assert.DoesNotContain("AddRecord(record);", source);
        Assert.DoesNotContain("UpdateRecord(record.Id, record.FinalText);", source);
    }

    [Fact]
    public void RecoveryRecordingCreationCleansUpPartialFiles()
    {
        var source = TestFile.ReadProjectFile(
            "src", "TypeWhisper.Core", "Services", "DictationRecoveryAudioStore.cs");
        var begin = TestFile.ExtractBlock(source, "private async Task BeginRecordingCoreAsync", 2200);

        Assert.Contains("FileStream? stream = null;", begin);
        Assert.Contains("await DisposeStreamAsync(stream).ConfigureAwait(false);", begin);
        Assert.Contains("TryDeleteSafeFile(fileName, ActiveFileNamePattern);", begin);
    }

    [Fact]
    public void HistoryRefreshSuppressionUsesDepthForOverlappingOperations()
    {
        var source = TestFile.ReadProjectFile(
            "src", "TypeWhisper.Windows", "ViewModels", "HistoryViewModel.cs");

        Assert.Contains("private int _suppressRefreshDepth;", source);
        Assert.Contains("Volatile.Read(ref _suppressRefreshDepth) > 0", source);
        Assert.Equal(2, Regex.Matches(source, "Interlocked.Increment\\(ref _suppressRefreshDepth\\)").Count);
        Assert.Equal(2, Regex.Matches(source, "Interlocked.Decrement\\(ref _suppressRefreshDepth\\)").Count);
    }

    [Fact]
    public void RecoverySettingsObserveRetentionAndUseNativeLanguageNames()
    {
        var source = TestFile.ReadProjectFile(
            "src", "TypeWhisper.Windows", "ViewModels", "RecoveryViewModel.cs");
        var applyRetention = TestFile.ExtractBlock(source, "private async Task ApplyRetentionAsync", 900);

        Assert.Contains("await _store.SetRetentionAsync(value);", applyRetention);
        Assert.Contains("ErrorText = HistoryWorkflowRetryService.SanitizeFailure(ex)", applyRetention);
        Assert.Contains("new(\"fr\", \"Français\")", source);
        Assert.Contains("new(\"es\", \"Español\")", source);
    }

    [Fact]
    public void ClipboardAndQueuedLeaseCleanupAreObservedBestEffortOperations()
    {
        var workflow = TestFile.ReadProjectFile(
            "src", "TypeWhisper.Windows", "Services", "WorkflowPostProcessingService.cs");
        var clipboard = TestFile.ExtractBlock(workflow, "private static string ReadClipboardText", 700);
        Assert.Contains("catch (ExternalException)", clipboard);

        var dictation = TestFile.ReadProjectFile(
            "src", "TypeWhisper.Windows", "ViewModels", "DictationViewModel.cs");
        Assert.Contains("DiscardRecoveryLeaseInBackground(pendingJob.RecoveryLease);", dictation);
        Assert.DoesNotContain("_ = pendingJob.RecoveryLease.DiscardAsync();", dictation);
    }

    [Fact]
    public void ClaudeResponseParserSearchesContentBlocksForUsableText()
    {
        var source = TestFile.ReadProjectFile(
            "plugins", "TypeWhisper.Plugin.Claude", "ClaudePlugin.cs");
        var process = TestFile.ExtractBlock(source, "public async Task<string> ProcessAsync", 4200);

        Assert.Contains("content.EnumerateArray()", process);
        Assert.Contains("block.TryGetProperty(\"text\", out var textElement)", process);
        Assert.Contains("PluginRequestFailureKind.EmptyResponse", process);
    }
}
