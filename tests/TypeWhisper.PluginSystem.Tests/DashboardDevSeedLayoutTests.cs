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
    }
}
