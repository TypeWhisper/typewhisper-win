namespace TypeWhisper.PluginSystem.Tests;

public sealed class ModelAccelerationLayoutTests
{
    [Fact]
    public void AudioAndModelsSections_BindAccelerationControlsVisibility()
    {
        var audioXaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "AudioSection.xaml");
        var modelsXaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "ModelsSection.xaml");

        Assert.True(
            TestFile.CountOccurrences(audioXaml, "ModelManager.IsAccelerationSectionVisible") >= 2,
            "Audio settings should hide the acceleration row and its separator for cloud engines.");
        Assert.Contains("Visibility=\"{Binding ModelManager.IsAccelerationSectionVisible", modelsXaml);
    }

    [Fact]
    public void AudioAndModelsSections_BindAccelerationRestartPrompt()
    {
        var audioXaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "AudioSection.xaml");
        var modelsXaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "ModelsSection.xaml");

        Assert.Contains("ModelManager.IsAccelerationRestartRequired", audioXaml);
        Assert.Contains("ModelManager.RestartForAccelerationCommand", audioXaml);
        Assert.Contains("ModelManager.IsAccelerationRestartRequired", modelsXaml);
        Assert.Contains("ModelManager.RestartForAccelerationCommand", modelsXaml);
    }

    [Fact]
    public void AudioSection_BindsModelStatusProgressToBusyState()
    {
        var audioXaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "AudioSection.xaml");

        Assert.Contains("ModelManager.IsActiveModelBusy", audioXaml);
    }

    [Fact]
    public void ModelsSection_OpensHostRenderedDownloadRequirements()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "ModelsSection.xaml");
        var codeBehind = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "ModelsSection.xaml.cs");

        Assert.Contains("HasDownloadRequirements", xaml);
        Assert.Contains("Click=\"Requirements_Click\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"ModelDownloadRequirements\"", xaml);
        Assert.Contains("ModelDownloadRequirementsControl", codeBehind);
        Assert.Contains("GetDownloadRequirementsProvider", codeBehind);
    }
}
