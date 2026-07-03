namespace TypeWhisper.PluginSystem.Tests;

public sealed class DashboardDevSeedLayoutTests
{
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
        Assert.Contains("await Task.Yield();", viewModel);
        Assert.Contains("IsDevelopmentSeedFailure", viewModel);
    }
}
