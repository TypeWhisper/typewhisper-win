using System.Reflection;
using System.Text.Json;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class DictionaryViewModelTests
{
    [Fact]
    public void Corrections_WithExactReplacement_AreGroupedInSourceOrder()
    {
        var first = Correction("1", "recieve", "receive");
        var term = Term("2", "TypeWhisper");
        var second = Correction("3", "receeve", "receive", isEnabled: false, caseSensitive: true);
        var viewModel = CreateViewModel(CreateDictionaryMock([first, term, second]).Object);

        Assert.Equal(2, viewModel.VisibleDisplayItems.Count);
        var group = viewModel.VisibleDisplayItems[0];
        Assert.True(group.IsGroupedCorrection);
        Assert.Equal([first, second], group.Entries);
        Assert.Same(term, viewModel.VisibleDisplayItems[1].PrimaryEntry);
        Assert.Equal(3, viewModel.EntryCount);
        Assert.False(group.IsExpanded);
        Assert.False(group.Entries[1].IsEnabled);
        Assert.True(group.Entries[1].CaseSensitive);
    }

    [Fact]
    public void Corrections_WithDifferentReplacementCasing_OrEmptyReplacement_StaySeparate()
    {
        var entries = new[]
        {
            Correction("1", "type whisper", "TypeWhisper"),
            Correction("2", "typewhisper", "typewhisper"),
            Correction("3", "empty-one", ""),
            Correction("4", "empty-two", "")
        };
        var viewModel = CreateViewModel(CreateDictionaryMock(entries).Object);

        Assert.Equal(4, viewModel.VisibleDisplayItems.Count);
        Assert.All(viewModel.VisibleDisplayItems, item => Assert.False(item.IsGroupedCorrection));
    }

    [Fact]
    public void SearchByAlias_ShowsAndExpandsTheCompleteGroup()
    {
        var first = Correction("1", "recieve", "receive");
        var second = Correction("2", "receeve", "receive");
        var unrelated = Correction("3", "teh", "the");
        var viewModel = CreateViewModel(CreateDictionaryMock([first, second, unrelated]).Object);

        viewModel.SearchText = "recieve";

        var group = Assert.Single(viewModel.VisibleDisplayItems);
        Assert.Equal([first, second], group.Entries);
        Assert.True(group.IsExpanded);
        Assert.Equal(2, viewModel.EntryCount);

        viewModel.SearchText = "receive";

        group = Assert.Single(viewModel.VisibleDisplayItems);
        Assert.False(group.IsExpanded);
    }

    [Fact]
    public void Tabs_FilterTermsAndCorrectionGroups()
    {
        var term = Term("1", "TypeWhisper");
        var first = Correction("2", "recieve", "receive");
        var second = Correction("3", "receeve", "receive");
        var viewModel = CreateViewModel(CreateDictionaryMock([term, first, second]).Object);

        viewModel.SelectedTab = 1;
        Assert.Same(term, Assert.Single(viewModel.VisibleDisplayItems).PrimaryEntry);

        viewModel.SelectedTab = 2;
        var group = Assert.Single(viewModel.VisibleDisplayItems);
        Assert.Equal([first, second], group.Entries);
    }

    [Fact]
    public void AutoLearnedTab_ShowsOnlyAutoLearnedCorrections()
    {
        var manual = Correction("1", "teh", "the");
        var automatic = Correction("2", "recieve", "receive", source: DictionaryEntrySource.AutoLearned);
        var term = Term("3", "TypeWhisper");
        var viewModel = CreateViewModel(CreateDictionaryMock([manual, automatic, term]).Object);

        viewModel.SelectedTab = 3;

        Assert.Same(automatic, Assert.Single(viewModel.VisibleDisplayItems).PrimaryEntry);
        Assert.Equal(1, viewModel.EntryCount);
    }

    [Fact]
    public void ClearAutoLearnedCorrections_DeletesOnlyAutomaticCorrections()
    {
        var manual = Correction("manual", "teh", "the");
        var automatic = Correction("automatic", "recieve", "receive", source: DictionaryEntrySource.AutoLearned);
        var pack = Term("pack:test:React", "React");
        var dictionary = CreateDictionaryMock([manual, automatic, pack]);
        var viewModel = CreateViewModel(dictionary.Object);
        viewModel.ConfirmReset = (_, _) => true;

        viewModel.ClearAutoLearnedCorrectionsCommand.Execute(null);

        dictionary.Verify(service => service.DeleteEntries(
            It.Is<IEnumerable<string>>(ids => ids.SequenceEqual(new[] { automatic.Id }))), Times.Once);
    }

    [Fact]
    public void ResetCustomDictionary_PreservesPackEntries()
    {
        var term = Term("term", "TypeWhisper");
        var manual = Correction("manual", "teh", "the");
        var automatic = Correction("automatic", "recieve", "receive", source: DictionaryEntrySource.AutoLearned);
        var pack = Term("pack:test:React", "React");
        var dictionary = CreateDictionaryMock([term, manual, automatic, pack]);
        var viewModel = CreateViewModel(dictionary.Object);
        viewModel.ConfirmReset = (_, _) => true;

        viewModel.ResetCustomDictionaryCommand.Execute(null);

        dictionary.Verify(service => service.DeleteEntries(
            It.Is<IEnumerable<string>>(ids => ids.SequenceEqual(new[] { term.Id, manual.Id, automatic.Id }))), Times.Once);
    }

    [Fact]
    public void DeactivateAllTermPacks_PreservesCustomEntriesAndClearsPackState()
    {
        var manual = Term("manual", "TypeWhisper");
        var packEntry = Term("pack:test:React", "React");
        var dictionary = CreateDictionaryMock([manual, packEntry]);
        var settings = CreateSettingsMock(AppSettings.Default with { EnabledPackIds = ["test"] });
        var viewModel = new DictionaryViewModel(dictionary.Object, settings.Object);
        viewModel.ConfirmReset = (_, _) => true;

        viewModel.DeactivateAllTermPacksCommand.Execute(null);

        dictionary.Verify(service => service.DeleteEntries(
            It.Is<IEnumerable<string>>(ids => ids.SequenceEqual(new[] { packEntry.Id }))), Times.Once);
        settings.Verify(service => service.Save(
            It.Is<AppSettings>(candidate => candidate.EnabledPackIds.Length == 0)), Times.Once);
    }

    [Fact]
    public void CancellingResetActions_ChangesNothing()
    {
        var automatic = Correction("automatic", "recieve", "receive", source: DictionaryEntrySource.AutoLearned);
        var pack = Term("pack:test:React", "React");
        var dictionary = CreateDictionaryMock([automatic, pack]);
        var settings = CreateSettingsMock(AppSettings.Default with { EnabledPackIds = ["test"] });
        var viewModel = new DictionaryViewModel(dictionary.Object, settings.Object);
        viewModel.ConfirmReset = (_, _) => false;

        viewModel.ClearAutoLearnedCorrectionsCommand.Execute(null);
        viewModel.ResetCustomDictionaryCommand.Execute(null);
        viewModel.DeactivateAllTermPacksCommand.Execute(null);

        dictionary.Verify(service => service.DeleteEntries(It.IsAny<IEnumerable<string>>()), Times.Never);
        settings.Verify(service => service.Save(It.IsAny<AppSettings>()), Times.Never);
    }

    [Fact]
    public void EmptyResetCategories_DisableTheirCommands()
    {
        var viewModel = CreateViewModel();

        Assert.False(viewModel.ClearAutoLearnedCorrectionsCommand.CanExecute(null));
        Assert.False(viewModel.ResetCustomDictionaryCommand.CanExecute(null));
        Assert.False(viewModel.DeactivateAllTermPacksCommand.CanExecute(null));
    }

    [Fact]
    public void PrepareAlias_ReusesTheExistingAddFlow()
    {
        var existing = Correction("1", "recieve", "receive");
        DictionaryEntry? added = null;
        var dictionary = CreateDictionaryMock([existing]);
        dictionary
            .Setup(service => service.AddEntry(It.IsAny<DictionaryEntry>()))
            .Callback<DictionaryEntry>(entry => added = entry);
        var viewModel = CreateViewModel(dictionary.Object);

        viewModel.NewOriginal = "old";
        viewModel.NewCaseSensitive = true;
        viewModel.PrepareAliasCommand.Execute(viewModel.VisibleDisplayItems[0].Replacement);

        Assert.Equal(DictionaryEntryType.Correction, viewModel.NewEntryType);
        Assert.Equal("", viewModel.NewOriginal);
        Assert.Equal("receive", viewModel.NewReplacement);
        Assert.False(viewModel.NewCaseSensitive);

        viewModel.NewOriginal = "receeve";
        viewModel.AddEntryCommand.Execute(null);

        Assert.NotNull(added);
        Assert.Equal("receeve", added.Original);
        Assert.Equal("receive", added.Replacement);
    }

    [Fact]
    public void AliasActions_TargetOnlyTheSelectedEntry()
    {
        var first = Correction("1", "recieve", "receive");
        var second = Correction("2", "receeve", "receive", isEnabled: false);
        var dictionary = CreateDictionaryMock([first, second]);
        var viewModel = CreateViewModel(dictionary.Object);

        viewModel.StartEditCommand.Execute(second);
        viewModel.ToggleEnabledCommand.Execute(second);
        viewModel.DeleteEntryCommand.Execute(second);

        Assert.Same(second, viewModel.EditEntry);
        dictionary.Verify(service => service.UpdateEntry(
            It.Is<DictionaryEntry>(entry => entry.Id == second.Id && entry.IsEnabled)), Times.Once);
        dictionary.Verify(service => service.DeleteEntry(second.Id), Times.Once);
        dictionary.Verify(service => service.DeleteEntry(first.Id), Times.Never);
    }

    [Fact]
    public void DictionarySection_RendersGroupedAndSingleCorrections()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "DictionarySection.xaml");

        Assert.Contains("ItemsSource=\"{Binding Dictionary.VisibleEntries}\"", xaml);
        Assert.Contains("Command=\"{Binding ToggleCommand}\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"{loc:Str Dictionary.ToggleAliases}\"", xaml);
        Assert.Contains("Visibility=\"{Binding IsExpanded, Converter={StaticResource BoolToVis}}\"", xaml);
        Assert.Contains("ItemsSource=\"{Binding Entries}\"", xaml);
        Assert.Contains("Command=\"{Binding DataContext.Dictionary.PrepareAliasCommand", xaml);
        Assert.Contains("Click=\"PrepareAlias_Click\"", xaml);
        Assert.Contains("DataContext=\"{Binding PrimaryEntry}\"", xaml);
        Assert.DoesNotContain("<ui:CardExpander", xaml);
        Assert.DoesNotContain("Dictionary.FilteredEntries", xaml);
        Assert.Contains("Dictionary.TabAutoLearned", xaml);
        Assert.Contains("Dictionary.ClearAutoLearnedCorrectionsCommand", xaml);
        Assert.Contains("Dictionary.ResetCustomDictionaryCommand", xaml);
        Assert.Contains("Dictionary.DeactivateAllTermPacksCommand", xaml);
    }

    [Fact]
    public void DictionaryPresentation_IsLocalizedInEverySupportedLanguage()
    {
        foreach (var language in new[] { "en", "de", "ja", "ru" })
        {
            var json = TestFile.ReadProjectFile(
                "src",
                "TypeWhisper.Windows",
                "Resources",
                "Localization",
                $"{language}.json");
            using var document = JsonDocument.Parse(json);

            Assert.False(string.IsNullOrWhiteSpace(
                document.RootElement.GetProperty("Dictionary.AddAlias").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(
                document.RootElement.GetProperty("Dictionary.ToggleAliases").GetString()));
            foreach (var key in new[]
            {
                "Dictionary.TabAutoLearned",
                "Dictionary.AutoLearned",
                "Dictionary.ClearAutoLearned",
                "Dictionary.ResetCustomDictionary",
                "Dictionary.DeactivateAllPacks",
                "Dictionary.ResetConfirmTitle"
            })
            {
                Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty(key).GetString()));
            }
        }
    }

    [Fact]
    public void AddEntry_PreservesEmptyCorrectionReplacement()
    {
        DictionaryEntry? added = null;
        var dictionary = CreateDictionaryMock();
        dictionary
            .Setup(service => service.AddEntry(It.IsAny<DictionaryEntry>()))
            .Callback<DictionaryEntry>(entry => added = entry);
        var viewModel = CreateViewModel(dictionary.Object);

        viewModel.NewOriginal = "teh";
        viewModel.NewReplacement = "";
        viewModel.NewEntryType = DictionaryEntryType.Correction;
        viewModel.AddEntryCommand.Execute(null);

        Assert.NotNull(added);
        Assert.Equal("", added.Replacement);
    }

    [Fact]
    public void SaveEdit_PreservesEmptyCorrectionReplacement()
    {
        var entry = new DictionaryEntry
        {
            Id = "correction-1",
            EntryType = DictionaryEntryType.Correction,
            Original = "teh",
            Replacement = "the"
        };
        DictionaryEntry? updated = null;
        var dictionary = CreateDictionaryMock([entry]);
        dictionary
            .Setup(service => service.UpdateEntry(It.IsAny<DictionaryEntry>()))
            .Callback<DictionaryEntry>(candidate => updated = candidate);
        var viewModel = CreateViewModel(dictionary.Object);

        viewModel.StartEditCommand.Execute(entry);
        viewModel.EditReplacement = "";
        viewModel.SaveEditCommand.Execute(null);

        Assert.NotNull(updated);
        Assert.Equal("", updated.Replacement);
    }

    [Fact]
    public void Dispose_DetachesLanguageChangedHandler()
    {
        var viewModel = CreateViewModel();

        Assert.Contains(GetLanguageChangedSubscribers(), handler => ReferenceEquals(handler.Target, viewModel));

        viewModel.Dispose();

        Assert.DoesNotContain(GetLanguageChangedSubscribers(), handler => ReferenceEquals(handler.Target, viewModel));
    }

    private static DictionaryViewModel CreateViewModel()
    {
        return CreateViewModel(CreateDictionaryMock().Object);
    }

    private static DictionaryViewModel CreateViewModel(IDictionaryService dictionary)
    {
        var settings = CreateSettingsMock(AppSettings.Default);

        return new DictionaryViewModel(dictionary, settings.Object);
    }

    private static Mock<ISettingsService> CreateSettingsMock(AppSettings initial)
    {
        var current = initial;
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(() => current);
        settings.Setup(service => service.Save(It.IsAny<AppSettings>()))
            .Callback<AppSettings>(value => current = value);
        return settings;
    }

    private static Mock<IDictionaryService> CreateDictionaryMock(IReadOnlyList<DictionaryEntry>? entries = null)
    {
        var dictionary = new Mock<IDictionaryService>();
        dictionary
            .SetupGet(service => service.Entries)
            .Returns(entries ?? Array.Empty<DictionaryEntry>());
        return dictionary;
    }

    private static DictionaryEntry Correction(
        string id,
        string original,
        string replacement,
        bool isEnabled = true,
        bool caseSensitive = false,
        DictionaryEntrySource source = DictionaryEntrySource.Manual) => new()
        {
            Id = id,
            EntryType = DictionaryEntryType.Correction,
            Original = original,
            Replacement = replacement,
            IsEnabled = isEnabled,
            CaseSensitive = caseSensitive,
            Source = source
        };

    private static DictionaryEntry Term(string id, string original) => new()
    {
        Id = id,
        EntryType = DictionaryEntryType.Term,
        Original = original
    };

    private static IReadOnlyList<Delegate> GetLanguageChangedSubscribers()
    {
        var eventField = typeof(Loc).GetField(
            "LanguageChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var handler = (EventHandler?)eventField?.GetValue(Loc.Instance);
        return handler?.GetInvocationList() ?? [];
    }
}
