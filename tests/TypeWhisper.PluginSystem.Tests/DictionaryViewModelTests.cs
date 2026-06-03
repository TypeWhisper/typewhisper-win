using System.Reflection;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class DictionaryViewModelTests
{
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
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(AppSettings.Default);

        return new DictionaryViewModel(dictionary, settings.Object);
    }

    private static Mock<IDictionaryService> CreateDictionaryMock(IReadOnlyList<DictionaryEntry>? entries = null)
    {
        var dictionary = new Mock<IDictionaryService>();
        dictionary
            .SetupGet(service => service.Entries)
            .Returns(entries ?? Array.Empty<DictionaryEntry>());
        return dictionary;
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
