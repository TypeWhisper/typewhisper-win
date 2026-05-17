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
}
