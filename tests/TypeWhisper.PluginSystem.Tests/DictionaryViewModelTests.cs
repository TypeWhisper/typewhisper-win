using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services;
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
    public void ApplyingIndustryPreset_NotifiesPackResetStateAfterSavingEnabledPack()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"TypeWhisperDictionaryViewModelTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var dictionary = CreateDictionaryMock();
            var settings = CreateSettingsMock(AppSettings.Default);
            using var http = new HttpClient();
            var license = new LicenseService(http, tempDir)
            {
                CommercialStatus = LicenseStatus.Active
            };
            using var viewModel = new DictionaryViewModel(dictionary.Object, settings.Object, license);
            var remotePacksField = typeof(DictionaryViewModel).GetField(
                "_remotePacks",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(remotePacksField);
            remotePacksField.SetValue(viewModel, new[]
            {
                new TermPack("real-estate", "Real estate", "", [], RequiresCommercialLicense: true)
            });
            var notifiedWithEnabledPack = false;
            viewModel.DeactivateAllTermPacksCommand.CanExecuteChanged += (_, _) =>
                notifiedWithEnabledPack |= viewModel.EnabledPackCount == 1;

            viewModel.ApplyIndustryPreset("real-estate");

            Assert.True(notifiedWithEnabledPack);
            Assert.True(viewModel.DeactivateAllTermPacksCommand.CanExecute(null));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadingRemotePacksAfterRestart_PreservesExplicitlyDisabledPackState()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"TypeWhisperDictionaryViewModelTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var dictionary = CreateDictionaryMock();
            var settings = CreateSettingsMock(AppSettings.Default with
            {
                SelectedIndustryPresetId = "architecture",
                EnabledPackIds = [],
                VocabularyBoostingEnabled = false
            });
            using var http = new HttpClient();
            var license = new LicenseService(http, tempDir)
            {
                CommercialStatus = LicenseStatus.Active
            };
            using var viewModel = new DictionaryViewModel(dictionary.Object, settings.Object, license);
            ApplyRemotePacks(viewModel,
            [
                new TermPack(
                    "architecture",
                    "Architecture",
                    "",
                    ["Scale"],
                    RequiresCommercialLicense: true)
            ]);

            dictionary.Verify(service => service.ActivatePack(It.IsAny<TermPack>()), Times.Never);
            Assert.Empty(settings.Object.Current.EnabledPackIds);
            Assert.False(settings.Object.Current.VocabularyBoostingEnabled);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void RestoringCommercialLicense_PreservesExplicitlyDisabledPresetPack()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"TypeWhisperDictionaryViewModelTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var dictionary = CreateDictionaryMock();
            var settings = CreateSettingsMock(AppSettings.Default with
            {
                SelectedIndustryPresetId = "architecture",
                EnabledPackIds = []
            });
            using var http = new HttpClient();
            var license = new LicenseService(http, tempDir);
            using var viewModel = new DictionaryViewModel(dictionary.Object, settings.Object, license);
            ApplyRemotePacks(viewModel,
            [
                new TermPack(
                    "architecture",
                    "Architecture",
                    "",
                    ["Scale"],
                    RequiresCommercialLicense: true)
            ]);

            license.CommercialStatus = LicenseStatus.Active;

            dictionary.Verify(service => service.ActivatePack(It.IsAny<TermPack>()), Times.Never);
            Assert.Empty(settings.Object.Current.EnabledPackIds);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadingRemotePacks_ActivatesPresetSelectedWhileCatalogWasUnavailable()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"TypeWhisperDictionaryViewModelTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var dictionary = CreateDictionaryMock();
            var settings = CreateSettingsMock(AppSettings.Default);
            using var http = new HttpClient();
            var license = new LicenseService(http, tempDir)
            {
                CommercialStatus = LicenseStatus.Active
            };
            using var viewModel = new DictionaryViewModel(dictionary.Object, settings.Object, license);

            viewModel.ApplyIndustryPreset("real-estate");
            ApplyRemotePacks(viewModel,
            [
                new TermPack(
                    "real-estate",
                    "Real estate",
                    "",
                    ["Property"],
                    RequiresCommercialLicense: true)
            ]);

            dictionary.Verify(service => service.ActivatePack(
                It.Is<TermPack>(pack => pack.Id == "real-estate")), Times.Once);
            Assert.Equal(["real-estate"], settings.Object.Current.EnabledPackIds);
            Assert.True(settings.Object.Current.VocabularyBoostingEnabled);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void SelectingPresetWhileUnlicensed_ActivatesAfterCatalogThenLicense()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"TypeWhisperDictionaryViewModelTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var dictionary = CreateDictionaryMock();
            var settings = CreateSettingsMock(AppSettings.Default);
            using var http = new HttpClient();
            var license = new LicenseService(http, tempDir);
            using var viewModel = new DictionaryViewModel(dictionary.Object, settings.Object, license);

            viewModel.ApplyIndustryPreset("real-estate");
            ApplyRemotePacks(viewModel,
            [
                new TermPack(
                    "real-estate",
                    "Real estate",
                    "",
                    ["Property"],
                    RequiresCommercialLicense: true)
            ]);
            dictionary.Verify(service => service.ActivatePack(It.IsAny<TermPack>()), Times.Never);

            license.CommercialStatus = LicenseStatus.Active;

            dictionary.Verify(service => service.ActivatePack(
                It.Is<TermPack>(pack => pack.Id == "real-estate")), Times.AtLeastOnce);
            Assert.Equal(["real-estate"], settings.Object.Current.EnabledPackIds);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void SelectingPresetWhileUnlicensed_ActivatesAfterLicenseThenCatalog()
    {
        var tempDir = Path.Join(Path.GetTempPath(), $"TypeWhisperDictionaryViewModelTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var dictionary = CreateDictionaryMock();
            var settings = CreateSettingsMock(AppSettings.Default);
            using var http = new HttpClient();
            var license = new LicenseService(http, tempDir);
            using var viewModel = new DictionaryViewModel(dictionary.Object, settings.Object, license);

            viewModel.ApplyIndustryPreset("real-estate");
            license.CommercialStatus = LicenseStatus.Active;
            dictionary.Verify(service => service.ActivatePack(It.IsAny<TermPack>()), Times.Never);

            ApplyRemotePacks(viewModel,
            [
                new TermPack(
                    "real-estate",
                    "Real estate",
                    "",
                    ["Property"],
                    RequiresCommercialLicense: true)
            ]);

            dictionary.Verify(service => service.ActivatePack(
                It.Is<TermPack>(pack => pack.Id == "real-estate")), Times.Once);
            Assert.Equal(["real-estate"], settings.Object.Current.EnabledPackIds);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
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
        Assert.Contains("AutomationProperties.AutomationId=\"DictionaryTabPacks\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"DictionaryVocabularyBoosting\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"DictionarySearch\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"DictionaryNewOriginal\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"DictionaryNewIsRegex\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"DictionaryEditIsRegex\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"DictionaryNewRegexValidationError\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"DictionaryEditRegexValidationError\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"DictionaryEditScroller\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"DictionaryAddEntry\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"DictionaryEntries\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"DictionaryPacks\"", xaml);
        Assert.Contains("AutomationProperties.AutomationId=\"{Binding Pack.Id}\"", xaml);
        Assert.Contains("AutomationProperties.Name=\"{Binding Pack.Name}\"", xaml);
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
        Assert.Contains("Click=\"DataManagement_Click\"", xaml);
        Assert.DoesNotContain("Dictionary.DataManagementDescription", xaml);
        Assert.Contains("Dictionary.Training.OpenCommand", xaml);
        Assert.Contains("Dictionary.Training.ToggleSampleCommand", xaml);
        Assert.Contains("Dictionary.Training.SaveCommand", xaml);
        Assert.Contains("Visibility=\"{Binding Dictionary.Training, Converter={StaticResource BoolToVis}}\"", xaml);
        Assert.Contains("KeyboardNavigation.TabNavigation=\"Cycle\"", xaml);
        Assert.Contains("AutomationProperties.LabeledBy=\"{Binding ElementName=TrainingTargetLabel}\"", xaml);
        Assert.Contains("AutomationProperties.LabeledBy=\"{Binding ElementName=TrainingSampleLabel}\"", xaml);
        Assert.Contains("Dictionary.TrainingApproveCandidateFormat", xaml);
        Assert.Contains("ConverterParameter=0, Mode=TwoWay", xaml);
    }

    [Fact]
    public void DictionarySection_EditDialogUsesTheFullSectionOverlay()
    {
        var xaml = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Views",
            "Sections",
            "DictionarySection.xaml");
        var document = XDocument.Parse(xaml);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var rootGrid = Assert.Single(document.Root!.Elements(presentation + "Grid"));
        var editOverlay = Assert.Single(
            document.Descendants(presentation + "Border"),
            element => (string?)element.Attribute("AutomationProperties.AutomationId") == "DictionaryEditOverlay");

        Assert.Same(rootGrid, editOverlay.Parent);
        Assert.Equal("10", (string?)editOverlay.Attribute("Panel.ZIndex"));
    }

    [Fact]
    public void DictionaryPresentation_IsLocalizedInEverySupportedLanguage()
    {
        var english = ReadLocalization("en");
        var englishTrainingKeys = english.Keys
            .Where(key => key.StartsWith("Dictionary.Training", StringComparison.Ordinal))
            .Order()
            .ToArray();

        foreach (var language in new[] { "en", "de", "ja", "ru" })
        {
            var localized = ReadLocalization(language);

            Assert.False(string.IsNullOrWhiteSpace(localized["Dictionary.AddAlias"]));
            Assert.False(string.IsNullOrWhiteSpace(localized["Dictionary.ToggleAliases"]));
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
                Assert.False(string.IsNullOrWhiteSpace(localized[key]));
            }

            var localizedTrainingKeys = localized.Keys
                .Where(key => key.StartsWith("Dictionary.Training", StringComparison.Ordinal))
                .Order()
                .ToArray();
            Assert.Equal(englishTrainingKeys, localizedTrainingKeys);
            foreach (var key in englishTrainingKeys)
            {
                Assert.False(string.IsNullOrWhiteSpace(localized[key]));
                Assert.Equal(GetPlaceholders(english[key]), GetPlaceholders(localized[key]));
            }
        }

        foreach (var language in new[] { "en", "de", "ja", "ru", "zh-Hans" })
        {
            var localized = ReadLocalization(language);
            Assert.False(string.IsNullOrWhiteSpace(localized["Dictionary.Regex"]));
            Assert.Equal(["{0}"], GetPlaceholders(localized["Dictionary.InvalidRegexFormat"]));
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
    public void AddEntry_PreservesRegexOptIn()
    {
        DictionaryEntry? added = null;
        var dictionary = CreateDictionaryMock();
        dictionary
            .Setup(service => service.AddEntry(It.IsAny<DictionaryEntry>()))
            .Callback<DictionaryEntry>(entry => added = entry);
        var viewModel = CreateViewModel(dictionary.Object);

        viewModel.NewOriginal = @"\s+Doppelpunkt\b";
        viewModel.NewReplacement = ":";
        viewModel.NewIsRegex = true;
        viewModel.AddEntryCommand.Execute(null);

        Assert.NotNull(added);
        Assert.True(added.IsRegex);
        Assert.False(viewModel.HasNewRegexValidationError);
    }

    [Fact]
    public void AddEntry_RejectsInvalidRegexWithValidationError()
    {
        var dictionary = CreateDictionaryMock();
        var viewModel = CreateViewModel(dictionary.Object);

        viewModel.NewOriginal = "[";
        viewModel.NewReplacement = "replacement";
        viewModel.NewIsRegex = true;
        viewModel.AddEntryCommand.Execute(null);

        dictionary.Verify(service => service.AddEntry(It.IsAny<DictionaryEntry>()), Times.Never);
        Assert.True(viewModel.HasNewRegexValidationError);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.NewRegexValidationError));
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
    public void SaveEdit_RejectsInvalidRegexWithoutClosingEditor()
    {
        var entry = Correction("correction-1", "teh", "the");
        var dictionary = CreateDictionaryMock([entry]);
        var viewModel = CreateViewModel(dictionary.Object);

        viewModel.StartEditCommand.Execute(entry);
        viewModel.EditOriginal = "[";
        viewModel.EditIsRegex = true;
        viewModel.SaveEditCommand.Execute(null);

        dictionary.Verify(service => service.UpdateEntry(It.IsAny<DictionaryEntry>()), Times.Never);
        Assert.True(viewModel.IsEditing);
        Assert.True(viewModel.HasEditRegexValidationError);
    }

    [Fact]
    public void SaveEdit_PreservesRegexOptIn()
    {
        var entry = new DictionaryEntry
        {
            Id = "correction-1",
            EntryType = DictionaryEntryType.Correction,
            Original = @"\s+Doppelpunkt\b",
            Replacement = ":",
            IsRegex = true
        };
        DictionaryEntry? updated = null;
        var dictionary = CreateDictionaryMock([entry]);
        dictionary
            .Setup(service => service.UpdateEntry(It.IsAny<DictionaryEntry>()))
            .Callback<DictionaryEntry>(candidate => updated = candidate);
        var viewModel = CreateViewModel(dictionary.Object);

        viewModel.StartEditCommand.Execute(entry);
        Assert.True(viewModel.EditIsRegex);
        viewModel.SaveEditCommand.Execute(null);

        Assert.NotNull(updated);
        Assert.True(updated.IsRegex);
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

    private static Dictionary<string, string> ReadLocalization(string language)
    {
        var json = TestFile.ReadProjectFile(
            "src",
            "TypeWhisper.Windows",
            "Resources",
            "Localization",
            $"{language}.json");
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString() ?? "");
    }

    private static string[] GetPlaceholders(string value) =>
        System.Text.RegularExpressions.Regex.Matches(value, @"\{\d+\}")
            .Select(match => match.Value)
            .ToArray();

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

    private static void ApplyRemotePacks(DictionaryViewModel viewModel, IReadOnlyList<TermPack> packs)
    {
        var method = typeof(DictionaryViewModel).GetMethod(
            "ApplyRemotePacks",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(viewModel, [packs]);
    }

    private static IReadOnlyList<Delegate> GetLanguageChangedSubscribers()
    {
        var eventField = typeof(Loc).GetField(
            "LanguageChanged",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var handler = (EventHandler?)eventField?.GetValue(Loc.Instance);
        return handler?.GetInvocationList() ?? [];
    }
}
