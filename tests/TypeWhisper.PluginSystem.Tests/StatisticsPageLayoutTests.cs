namespace TypeWhisper.PluginSystem.Tests;

public sealed class StatisticsPageLayoutTests
{
    [Fact]
    public void StatisticsSection_ExposesHoverTooltipsAndClickableDataDetails()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "StatisticsSection.xaml");

        Assert.Contains("StatisticsCardButtonStyle", xaml);
        Assert.Contains("IsMouseOver", xaml);
        Assert.Contains("ToolTip=\"{Binding Tooltip}\"", xaml);
        Assert.Contains("SelectActivityPointCommand", xaml);
        Assert.Contains("SelectHeatmapCellCommand", xaml);
        Assert.Contains("SelectedActivityDetail", xaml);
        Assert.Contains("SelectedHeatmapDetail", xaml);
        Assert.Contains("SelectedAppDetail", xaml);
        Assert.Contains("SelectedModelDetail", xaml);
        Assert.Contains("StatisticsRangeWeek", xaml);
        Assert.Contains("StatisticsRangeMonth", xaml);
        Assert.Contains("StatisticsRangeAllTime", xaml);
        Assert.Contains("VerticalScrollBarVisibility=\"Hidden\"", xaml);
        Assert.DoesNotContain("HorizontalScrollBarVisibility=\"Auto\"", xaml);
        Assert.Contains("MaxHeight=\"285\"", xaml);
        Assert.Contains("ScrollViewer.VerticalScrollBarVisibility=\"Hidden\"", xaml);
        Assert.Contains("Width=\"72\"", xaml);
    }

    [Fact]
    public void StatisticsRoute_IsRegisteredAlongsideDashboard()
    {
        var navigation = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "ViewModels",
            "SettingsNavigation.cs");
        var window = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "SettingsWindow.xaml.cs");

        Assert.Contains("SettingsRoute.Statistics", navigation);
        Assert.Contains("text(\"Nav.Statistics\")", navigation);
        Assert.Contains("RegisterSection(SettingsRoute.Statistics", window);
    }

    [Fact]
    public void SuccessfulDictation_RecordsStatisticsAfterOutput()
    {
        var dictation = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "ViewModels",
            "DictationViewModel.cs");

        var inserted = dictation.IndexOf("_eventBus.Publish(new TextInsertedEvent", StringComparison.Ordinal);
        var statistics = dictation.IndexOf("_usageStatistics?.RecordTranscription(", StringComparison.Ordinal);
        Assert.True(inserted >= 0);
        Assert.True(statistics > inserted);
    }
}
