namespace TypeWhisper.PluginSystem.Tests;

public sealed class AudioSectionLanguageTests
{
    [Fact]
    public void SpokenLanguageSelector_UsesOrderedLanguageHintList()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "AudioSection.xaml");

        var viewModel = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "ViewModels",
            "SettingsViewModel.cs");

        Assert.Contains("Settings.AvailableLanguageHints", xaml);
        Assert.Contains("Settings.SelectedLanguageHints", xaml);
        Assert.Contains("new(\"zh\", \"中文\")", viewModel);
    }
}
