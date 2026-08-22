using TypeWhisper.Core.Models;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class HistoryWorkflowRawTextTests
{
    [Fact]
    public void SuccessfulWorkflowEntry_ExposesDistinctRawTranscription()
    {
        var record = new TranscriptionRecord
        {
            Id = "successful-workflow",
            Timestamp = DateTime.UtcNow,
            RawText = "The complete raw transcription remains available.",
            FinalText = "The transformed workflow result.",
            ProfileName = "Coding Prompt Assistant"
        };

        var entry = new HistoryEntryViewModel(record, null!);

        Assert.True(entry.HasDistinctWorkflowRawText);
    }

    [Fact]
    public void History_ExposesDistinctRawTextForSuccessfulWorkflowEntries()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "HistorySection.xaml");

        Assert.Contains(
            "Visibility=\"{Binding HasDistinctWorkflowRawText, Converter={StaticResource BoolToVis}}\"",
            xaml);
        Assert.Contains("StringFormat=HistoryEntry.{0}", xaml);
        Assert.Contains("StringFormat=HistoryToggle.{0}", xaml);
        Assert.Contains("StringFormat=HistorySuccessfulRawText.{0}", xaml);
        Assert.Contains("StringFormat=HistoryCopySuccessfulRawText.{0}", xaml);
    }
}
