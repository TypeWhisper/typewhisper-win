using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class DictionaryTrainingViewModelTests
{
    [Theory]
    [InlineData("Today I use Kubernets", "Today I use Kubernetes.", "Kubernets")]
    [InlineData("Today I use Kubernetes", "Today I use Kubernetes.", null)]
    [InlineData("Today I use kubernetes", "Today I use Kubernetes.", null)]
    [InlineData("Today we use Kubernets", "Today I use Kubernetes.", null)]
    [InlineData("", "Today I use Kubernetes.", null)]
    public void ExtractCandidate_IsConservativeAndIgnoresBoundaryPunctuation(
        string raw,
        string expected,
        string? candidate)
    {
        Assert.Equal(candidate, DictionaryTrainingViewModel.ExtractCandidate("Kubernetes", expected, raw));
    }

    [Fact]
    public void Save_AddsManualTermAndReviewedCorrectionsInOneBatch()
    {
        var dictionaryEntries = new List<DictionaryEntry>();
        IReadOnlyList<DictionaryEntry>? added = null;
        var dictionary = DictionaryMock(dictionaryEntries);
        dictionary.Setup(service => service.AddEntries(It.IsAny<IEnumerable<DictionaryEntry>>()))
            .Callback<IEnumerable<DictionaryEntry>>(entries => added = entries.ToArray());
        using var fixture = CreateFixture(dictionary.Object);
        fixture.ViewModel.TargetWord = "Kubernetes";
        fixture.ViewModel.Candidates.Add(new DictionaryTrainingCandidateViewModel("Kubernets", () => { }));

        fixture.ViewModel.SaveCommand.Execute(null);

        Assert.NotNull(added);
        Assert.Equal(2, added.Count);
        Assert.Contains(added, entry =>
            entry.EntryType == DictionaryEntryType.Term &&
            entry.Original == "Kubernetes" &&
            entry.Source == DictionaryEntrySource.Manual);
        Assert.Contains(added, entry =>
            entry.EntryType == DictionaryEntryType.Correction &&
            entry.Original == "Kubernets" &&
            entry.Replacement == "Kubernetes" &&
            entry.Source == DictionaryEntrySource.Manual);
        dictionary.Verify(service => service.AddEntries(It.IsAny<IEnumerable<DictionaryEntry>>()), Times.Once);
    }

    [Fact]
    public void Save_DoesNotOverwriteConflictingCorrection()
    {
        var existing = new DictionaryEntry
        {
            Id = "existing",
            EntryType = DictionaryEntryType.Correction,
            Original = "Kubernets",
            Replacement = "Kubernet"
        };
        IReadOnlyList<DictionaryEntry>? added = null;
        var dictionary = DictionaryMock([existing]);
        dictionary.Setup(service => service.AddEntries(It.IsAny<IEnumerable<DictionaryEntry>>()))
            .Callback<IEnumerable<DictionaryEntry>>(entries => added = entries.ToArray());
        using var fixture = CreateFixture(dictionary.Object);
        fixture.ViewModel.TargetWord = "Kubernetes";
        var candidate = new DictionaryTrainingCandidateViewModel("Kubernets", () => { });
        fixture.ViewModel.Candidates.Add(candidate);

        fixture.ViewModel.SaveCommand.Execute(null);

        Assert.NotNull(added);
        Assert.Single(added);
        Assert.Equal(DictionaryEntryType.Term, added[0].EntryType);
        Assert.False(candidate.CanApprove);
        Assert.False(candidate.IsApproved);
        Assert.Equal("Kubernet", existing.Replacement);
    }

    [Fact]
    public void Cancel_LeavesDictionaryUntouchedAndRestoresHotkeys()
    {
        var dictionary = DictionaryMock([]);
        using var fixture = CreateFixture(dictionary.Object);

        fixture.ViewModel.OpenCommand.Execute(null);
        Assert.False(fixture.Hotkeys.IsEnabled);

        fixture.ViewModel.CancelCommand.Execute(null);

        Assert.True(fixture.Hotkeys.IsEnabled);
        Assert.False(fixture.ViewModel.IsOpen);
        dictionary.Verify(service => service.AddEntries(It.IsAny<IEnumerable<DictionaryEntry>>()), Times.Never);
    }

    [Fact]
    public async Task Sample_UsesSelectedEngineDirectlyWithGlobalLanguageHints()
    {
        Loc.Instance.Initialize();
        Loc.Instance.CurrentLanguage = "en";
        var settings = SettingsMock(AppSettings.Default with
        {
            SelectedModelId = "plugin:training:test-model",
            LanguageHints = ["de", "en"]
        });
        var plugin = new FakeTrainingPlugin { ResponseText = "Today I am working with Kubernets." };
        var pluginManager = TestPluginManagerFactory.Create(settings.Object);
        TestPluginManagerFactory.SetPrivateField(
            pluginManager,
            "_transcriptionEngines",
            new List<ITranscriptionEnginePlugin> { plugin });
        var modelManager = new ModelManagerService(pluginManager, settings.Object);
        var captures = new FakeAudioInputCaptureFactory();
        using var audio = new AudioRecordingService(
            new FakeAudioInputDeviceProvider("Microphone"),
            captures,
            Timeout.InfiniteTimeSpan);
        var workflows = new Mock<IWorkflowService>();
        workflows.SetupGet(service => service.Workflows).Returns([]);
        using var hotkeys = new HotkeyService(settings.Object, workflows.Object);
        var dictionary = DictionaryMock([]);
        var viewModel = new DictionaryTrainingViewModel(
            dictionary.Object,
            settings.Object,
            modelManager,
            audio,
            hotkeys);
        viewModel.OpenCommand.Execute(null);
        viewModel.TargetWord = "Kubernetes";
        viewModel.BeginCommand.Execute(null);
        var sample = viewModel.Samples[0];

        await viewModel.ToggleSampleCommand.ExecuteAsync(sample);
        captures.Created.Single().RaiseData([0, 16, 0, 16], 4);
        await viewModel.ToggleSampleCommand.ExecuteAsync(sample);

        Assert.Equal(DictionaryTrainingSampleStatus.Completed, sample.Status);
        Assert.Equal(plugin.ResponseText, sample.RawTranscript);
        Assert.Equal(["de", "en"], plugin.LastLanguageHints);
        Assert.Null(plugin.LastPrompt);
        dictionary.Verify(service => service.AddEntries(It.IsAny<IEnumerable<DictionaryEntry>>()), Times.Never);
    }

    private static TrainingFixture CreateFixture(IDictionaryService dictionary)
    {
        var settings = SettingsMock(AppSettings.Default);
        var pluginManager = TestPluginManagerFactory.Create(settings.Object);
        var modelManager = new ModelManagerService(pluginManager, settings.Object);
        var audio = new AudioRecordingService(
            new FakeAudioInputDeviceProvider(),
            new FakeAudioInputCaptureFactory(),
            Timeout.InfiniteTimeSpan);
        var workflows = new Mock<IWorkflowService>();
        workflows.SetupGet(service => service.Workflows).Returns([]);
        var hotkeys = new HotkeyService(settings.Object, workflows.Object);
        var viewModel = new DictionaryTrainingViewModel(dictionary, settings.Object, modelManager, audio, hotkeys);
        return new TrainingFixture(viewModel, audio, hotkeys);
    }

    private static Mock<IDictionaryService> DictionaryMock(IReadOnlyList<DictionaryEntry> entries)
    {
        var dictionary = new Mock<IDictionaryService>();
        dictionary.SetupGet(service => service.Entries).Returns(entries);
        return dictionary;
    }

    private static Mock<ISettingsService> SettingsMock(AppSettings settings)
    {
        var service = new Mock<ISettingsService>();
        service.SetupGet(candidate => candidate.Current).Returns(settings);
        return service;
    }

    private sealed record TrainingFixture(
        DictionaryTrainingViewModel ViewModel,
        AudioRecordingService Audio,
        HotkeyService Hotkeys) : IDisposable
    {
        public void Dispose()
        {
            ViewModel.CancelCommand.Execute(null);
            Audio.Dispose();
            Hotkeys.Dispose();
        }
    }

    private sealed class FakeTrainingPlugin : ITranscriptionEnginePlugin, ITranscriptionEngineSelectionIdentity
    {
        public string ResponseText { get; set; } = "";
        public IReadOnlyList<string> LastLanguageHints { get; private set; } = [];
        public string? LastPrompt { get; private set; }
        public string PluginId => "training";
        public string PluginName => "Training";
        public string PluginVersion => "1.0.0";
        public string TranscriptionSelectionId => "training";
        public string ProviderId => "training";
        public string ProviderDisplayName => "Training engine";
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
            [new("test-model", "Test model")];
        public string? SelectedModelId { get; private set; }
        public bool SupportsTranslation => false;

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public System.Windows.Controls.UserControl? CreateSettingsView() => null;
        public void SelectModel(string modelId) => SelectedModelId = modelId;
        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio,
            string? language,
            bool translate,
            string? prompt,
            CancellationToken ct) =>
            TranscribeWithLanguageHintsAsync(
                wavAudio,
                language is null ? [] : [language],
                translate,
                prompt,
                ct);

        public Task<PluginTranscriptionResult> TranscribeWithLanguageHintsAsync(
            byte[] wavAudio,
            IReadOnlyList<string> languageHints,
            bool translate,
            string? prompt,
            CancellationToken ct)
        {
            LastLanguageHints = languageHints.ToArray();
            LastPrompt = prompt;
            return Task.FromResult(new PluginTranscriptionResult(ResponseText, "de", 1));
        }

        public void Dispose() { }
    }
}
