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
    public void Dispose_DetachesLanguageChangedHandler()
    {
        var viewModel = CreateViewModel();

        Assert.Contains(GetLanguageChangedSubscribers(), handler => ReferenceEquals(handler.Target, viewModel));

        viewModel.Dispose();

        Assert.DoesNotContain(GetLanguageChangedSubscribers(), handler => ReferenceEquals(handler.Target, viewModel));
    }

    private static DictionaryViewModel CreateViewModel()
    {
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(AppSettings.Default);

        var dictionary = Mock.Of<IDictionaryService>(service =>
            service.Entries == Array.Empty<DictionaryEntry>());

        return new DictionaryViewModel(dictionary, settings.Object);
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
