using System.Xml.Linq;

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
        Assert.Contains("DictationGermanOutputVariant", xaml);
        Assert.Contains("Settings.GermanOutputVariantOptions", xaml);
        Assert.Contains("Settings.HasSelectedGermanLanguage", xaml);
        Assert.Contains("GermanOutputVariant.Switzerland", viewModel);
    }

    [Fact]
    public void AudioSection_GridRowsCoverEveryDirectChildRowIndex()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "AudioSection.xaml");
        var document = XDocument.Parse(xaml);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        foreach (var grid in document.Descendants(presentation + "Grid"))
        {
            var rowCount = grid.Element(presentation + "Grid.RowDefinitions")?
                .Elements(presentation + "RowDefinition")
                .Count() ?? 0;
            var availableRows = Math.Max(1, rowCount);
            var directChildRows = grid.Elements()
                .Where(element => element.Name != presentation + "Grid.RowDefinitions"
                    && element.Name != presentation + "Grid.ColumnDefinitions")
                .Select(element => int.TryParse(element.Attribute("Grid.Row")?.Value, out var row) ? row : 0)
                .ToList();

            Assert.All(directChildRows, row => Assert.InRange(row, 0, availableRows - 1));
        }
    }
}
