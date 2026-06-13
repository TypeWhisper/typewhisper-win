namespace TypeWhisper.PluginSystem.Tests;

public sealed class AudioSectionLanguageTests
{
    [Fact]
    public void SpokenLanguageSelector_OffersChinese()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "AudioSection.xaml");

        Assert.Contains("<ComboBoxItem Content=\"中文\" Tag=\"zh\"/>", xaml);
    }
}
