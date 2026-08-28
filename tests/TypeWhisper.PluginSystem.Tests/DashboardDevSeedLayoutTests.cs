namespace TypeWhisper.PluginSystem.Tests;

public sealed class DashboardDevSeedLayoutTests
{
    [Fact]
    public void DashboardSection_DoesNotDuplicateStatisticsPage()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "DashboardSection.xaml");

        Assert.DoesNotContain("DashboardRange", xaml);
        Assert.DoesNotContain("Dashboard.WordsCount", xaml);
        Assert.DoesNotContain("Dashboard.AverageWpm", xaml);
        Assert.DoesNotContain("Dashboard.AppsUsed", xaml);
        Assert.DoesNotContain("Dashboard.TimeSaved", xaml);
        Assert.DoesNotContain("Dashboard.ChartData", xaml);
    }

    [Fact]
    public void DashboardSection_ProvidesUsefulNavigationWithoutDuplicatingMetrics()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "DashboardSection.xaml");

        Assert.Contains("DashboardOpenStatistics", xaml);
        Assert.Contains("SettingsRoute.Statistics", xaml);
        Assert.Contains("SettingsRoute.Dictation", xaml);
        Assert.Contains("SettingsRoute.Shortcuts", xaml);
        Assert.Contains("SettingsRoute.History", xaml);
        Assert.Contains("SettingsRoute.Workflows", xaml);
        Assert.Contains("NavigateToRouteCommand", xaml);
        Assert.Contains("controls:WaveformLogo", xaml);
        Assert.Contains("Width=\"112\"", xaml);
    }

    [Fact]
    public void DashboardSection_ExposesDevelopmentClearAndSeedCard()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "DashboardSection.xaml");

        Assert.Contains("Dashboard.DevSeedTitle", xaml);
        Assert.Contains("Dashboard.DevSeedDescription", xaml);
        Assert.Contains("Dashboard.ClearAndSeed", xaml);
        Assert.Contains("ClearAndSeedDevelopmentDataCommand", xaml);
        Assert.Contains("IsDevelopmentBuild", xaml);
        Assert.Contains("DevelopmentSeedStatusText", xaml);
        Assert.Contains("IsDevelopmentSeedFailure", xaml);
        Assert.Contains("DangerBrush", xaml);
        Assert.Contains("DataTrigger", xaml);
    }

    [Fact]
    public void SettingsWindowViewModel_YieldsBeforeDevelopmentSeedWork()
    {
        var viewModel = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "ViewModels",
            "SettingsWindowViewModel.cs");

        Assert.Contains("private async Task ClearAndSeedDevelopmentData()", viewModel);
        Assert.Contains("await Dispatcher.Yield(DispatcherPriority.Background);", viewModel);
        Assert.Contains("IsDevelopmentSeedFailure", viewModel);
    }
}
