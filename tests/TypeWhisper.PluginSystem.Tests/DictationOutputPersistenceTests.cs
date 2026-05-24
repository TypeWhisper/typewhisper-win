namespace TypeWhisper.PluginSystem.Tests;

public sealed class DictationOutputPersistenceTests
{
    [Fact]
    public void ProcessSingleJobAsync_PersistsCompletedTextBeforeOutputDelivery()
    {
        var source = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "ViewModels",
            "DictationViewModel.cs");
        var processJob = TestFile.ExtractBlock(source, "private async Task ProcessSingleJobAsync", 18000);

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
}
