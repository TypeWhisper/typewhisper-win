using System.Windows.Controls;
using TypeWhisper.Core.Models;
using TypeWhisper.Core.Services.SpokenFormatting;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Localization;
using TypeWhisper.Windows.Services.Plugins;
using TypeWhisper.Windows.ViewModels;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class SpokenFormattingViewModelTests : IDisposable
{
    private readonly FakeSettingsService _settings = new(AppSettings.Default);
    private readonly PluginManager _pluginManager;

    public SpokenFormattingViewModelTests()
    {
        Loc.Instance.Initialize();
        Loc.Instance.CurrentLanguage = "en";
        _pluginManager = TestPluginManagerFactory.Create(_settings);
    }

    [Fact]
    public void MissingSelectedModel_DisablesProfileControls()
    {
        var sut = CreateViewModel();

        Assert.False(sut.HasSelectedModel);
        Assert.True(sut.HasNoSelectedModel);
        Assert.False(sut.KeepAutomaticCommand.CanExecute(null));
        Assert.False(sut.UseFallbackCommand.CanExecute(null));
        Assert.False(sut.NativeWorksCommand.CanExecute(null));
    }

    [Fact]
    public void SelectedProfile_UsesProviderAndRawModelIdentity()
    {
        const string providerId = "com.typewhisper.sherpa-onnx";
        const string selectionId = "sherpa-profile-a";
        const string modelId = "parakeet-tdt-0.6b";
        AddTranscriptionEngine(new FakeTranscriptionEngine(
            providerId,
            "Sherpa ONNX",
            selectionId,
            new PluginModelInfo(modelId, "Parakeet TDT 0.6B")));
        _settings.Save(_settings.Current with
        {
            SelectedModelId = ModelManagerService.GetPluginModelId(selectionId, modelId),
            LanguageHints = ["de-DE"],
            Language = "de-DE"
        });

        var sut = CreateViewModel();

        Assert.True(sut.HasSelectedModel);
        Assert.Equal("Sherpa ONNX", sut.CurrentEngineDisplayName);
        Assert.Equal("Parakeet TDT 0.6B", sut.CurrentModelDisplayName);
        Assert.Equal("de", sut.SelectedLanguageCode);
        Assert.Equal(SpokenFormattingStrategy.NativeOnly, sut.SelectedStrategy);
        Assert.Equal(SpokenFormattingVerificationState.VendorHint, sut.VerificationState);

        sut.UseFallbackCommand.Execute(null);

        var profile = Assert.Single(_settings.Current.SpokenFormattingProfiles);
        Assert.Equal(providerId, profile.EngineId);
        Assert.Equal(modelId, profile.ModelId);
        Assert.Equal("de", profile.LanguageCode);
        Assert.Equal(SpokenFormattingStrategy.FallbackOnly, profile.StrategyOverride);
        Assert.Equal(SpokenFormattingVerificationState.UserVerifiedBad, profile.VerificationState);
        Assert.NotNull(profile.LastVerifiedAt);

        var verifiedAt = profile.LastVerifiedAt;
        sut.SelectedStrategy = SpokenFormattingStrategy.Automatic;
        profile = Assert.Single(_settings.Current.SpokenFormattingProfiles);

        Assert.Equal(SpokenFormattingVerificationState.UserVerifiedBad, profile.VerificationState);
        Assert.Equal(verifiedAt, profile.LastVerifiedAt);
    }

    [Fact]
    public void LanguageAndDirectStrategySelection_UpdateIndependentProfiles()
    {
        const string providerId = "engine";
        const string modelId = "model";
        AddTranscriptionEngine(new FakeTranscriptionEngine(
            providerId,
            "Engine",
            providerId,
            new PluginModelInfo(modelId, "Model")));
        _settings.Save(_settings.Current with
        {
            SelectedModelId = ModelManagerService.GetPluginModelId(providerId, modelId),
            LanguageHints = ["de"],
            Language = "de"
        });
        var sut = CreateViewModel();

        sut.SelectedStrategy = SpokenFormattingStrategy.FallbackOnly;
        sut.SelectedLanguageCode = "en";

        Assert.Equal(SpokenFormattingStrategy.NativeOnly, sut.SelectedStrategy);
        Assert.Equal(SpokenFormattingVerificationState.Unknown, sut.VerificationState);

        sut.SelectedStrategy = SpokenFormattingStrategy.Automatic;

        Assert.Collection(
            _settings.Current.SpokenFormattingProfiles.OrderBy(profile => profile.LanguageCode),
            profile =>
            {
                Assert.Equal("de", profile.LanguageCode);
                Assert.Equal(SpokenFormattingVerificationState.UserVerifiedBad, profile.VerificationState);
            },
            profile =>
            {
                Assert.Equal("en", profile.LanguageCode);
                Assert.Equal(SpokenFormattingStrategy.Automatic, profile.StrategyOverride);
                Assert.Equal(SpokenFormattingVerificationState.Unknown, profile.VerificationState);
            });
    }

    [Fact]
    public void AvailableLanguages_ContainOnlySupportedRuleLanguages()
    {
        var sut = CreateViewModel();

        Assert.Equal(["de", "en"], sut.AvailableLanguages.Select(language => language.Code));
    }

    public void Dispose() => _pluginManager.Dispose();

    private SpokenFormattingViewModel CreateViewModel()
    {
        var rules = new SpokenFormattingRulesLoader();
        var store = new SpokenFormattingProfileStore(_settings);
        return new SpokenFormattingViewModel(
            _settings,
            new ModelManagerService(_pluginManager, _settings),
            store,
            new SpokenFormattingStrategyResolver(store, rules),
            rules);
    }

    private void AddTranscriptionEngine(ITranscriptionEnginePlugin engine) =>
        TestPluginManagerFactory.SetPrivateField(
            _pluginManager,
            "_transcriptionEngines",
            new List<ITranscriptionEnginePlugin> { engine });

    private sealed class FakeTranscriptionEngine : ITranscriptionEnginePlugin, ITranscriptionEngineSelectionIdentity
    {
        public FakeTranscriptionEngine(
            string providerId,
            string displayName,
            string selectionId,
            PluginModelInfo model)
        {
            ProviderId = providerId;
            ProviderDisplayName = displayName;
            TranscriptionSelectionId = selectionId;
            TranscriptionModels = [model];
            SelectedModelId = model.Id;
        }

        public string PluginId => ProviderId;
        public string PluginName => ProviderDisplayName;
        public string PluginVersion => "1.0.0";
        public string ProviderId { get; }
        public string ProviderDisplayName { get; }
        public string TranscriptionSelectionId { get; }
        public bool IsConfigured => true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; }
        public string? SelectedModelId { get; private set; }
        public bool SupportsTranslation => false;
        public void SelectModel(string modelId) => SelectedModelId = modelId;
        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio,
            string? language,
            bool translate,
            string? prompt,
            CancellationToken ct) => throw new NotSupportedException();
        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public UserControl? CreateSettingsView() => null;
        public void Dispose() { }
    }
}
