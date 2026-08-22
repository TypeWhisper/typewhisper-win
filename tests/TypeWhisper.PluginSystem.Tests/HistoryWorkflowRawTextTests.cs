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
    public void ReplacingRecord_NotifiesDistinctRawTranscriptionPredicate()
    {
        var original = new TranscriptionRecord
        {
            Id = "successful-workflow",
            Timestamp = DateTime.UtcNow,
            RawText = "The original transcription.",
            FinalText = "The original transcription.",
            ProfileName = "Coding Prompt Assistant"
        };
        var entry = new HistoryEntryViewModel(original, null!);
        var changedProperties = new List<string?>();
        entry.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        entry.ReplaceRecord(original with { FinalText = "The transformed workflow result." });

        Assert.True(entry.HasDistinctWorkflowRawText);
        Assert.Contains(nameof(HistoryEntryViewModel.HasDistinctWorkflowRawText), changedProperties);
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
