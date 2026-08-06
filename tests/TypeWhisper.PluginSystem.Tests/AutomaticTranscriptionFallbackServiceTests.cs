using System.Net.Http;
using System.Reflection;
using System.Windows.Controls;
using Moq;
using TypeWhisper.Core.Interfaces;
using TypeWhisper.Core.Models;
using TypeWhisper.PluginSDK;
using TypeWhisper.PluginSDK.Models;
using TypeWhisper.Windows.Services;
using TypeWhisper.Windows.Services.Plugins;

namespace TypeWhisper.PluginSystem.Tests;

public sealed class AutomaticTranscriptionFallbackServiceTests : IDisposable
{
    private readonly FakeSettingsService _settings;
    private readonly PluginManager _pluginManager;
    private readonly ModelManagerService _modelManager;
    private readonly Mock<IDictionaryService> _dictionary = new();

    public AutomaticTranscriptionFallbackServiceTests()
    {
        _settings = new FakeSettingsService(AppSettings.Default with
        {
            DictationRecoveryAutomaticFallbackEnabled = true,
            DictationRecoveryEngineId = "recovery",
            DictationRecoveryModelId = "recovery-model",
            DictationRecoveryLanguage = "de",
            DictationRecoveryTask = "translate"
        });
        _pluginManager = TestPluginManagerFactory.Create(_settings);
        _modelManager = new ModelManagerService(_pluginManager, _settings, _dictionary.Object);
    }

    [Fact]
    public async Task EligibleFailure_UsesConfiguredAlternativeExactlyOnceAndReturnsActualMetadata()
    {
        var engine = new FakeTranscriptionEngine("recovery", "recovery-model")
        {
            SupportsTranslationValue = true,
            Result = new PluginTranscriptionResult("fallback text", "de", 1.5)
        };
        SetTranscriptionEngines(engine);
        var sut = CreateService(hasLicense: true);

        var result = await sut.TryTranscribeAsync(
            [0.1f, 0.2f],
            primaryEngineId: "primary",
            new PluginRequestException("network", PluginRequestFailureKind.Network),
            CancellationToken.None);

        var fallback = Assert.IsType<AutomaticTranscriptionFallbackResult>(result);
        Assert.Equal("fallback text", fallback.Result.Text);
        Assert.Equal("recovery", fallback.EngineId);
        Assert.Equal("recovery-model", fallback.ModelId);
        Assert.Equal(TranscriptionTask.Translate, fallback.Task);
        Assert.Equal(1, engine.TranscriptionCallCount);
        Assert.True(engine.LastTranslate);
        Assert.Equal("de", Assert.Single(engine.LastLanguageHints));
    }

    [Fact]
    public async Task MissingLicense_DoesNotStartFallback()
    {
        var engine = new FakeTranscriptionEngine("recovery", "recovery-model")
        {
            SupportsTranslationValue = true
        };
        SetTranscriptionEngines(engine);

        var result = await CreateService(hasLicense: false).TryTranscribeAsync(
            [0.1f],
            "primary",
            new TimeoutException(),
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(0, engine.TranscriptionCallCount);
    }

    [Fact]
    public async Task SameEngineOrUnavailableModel_DoesNotStartFallback()
    {
        var engine = new FakeTranscriptionEngine("recovery", "recovery-model")
        {
            SupportsTranslationValue = true
        };
        SetTranscriptionEngines(engine);

        var sameEngine = await CreateService(hasLicense: true).TryTranscribeAsync(
            [0.1f],
            "recovery",
            new TimeoutException(),
            CancellationToken.None);
        _settings.Save(_settings.Current with { DictationRecoveryModelId = "missing" });
        var missingModel = await CreateService(hasLicense: true).TryTranscribeAsync(
            [0.1f],
            "primary",
            new TimeoutException(),
            CancellationToken.None);

        Assert.Null(sameEngine);
        Assert.Null(missingModel);
        Assert.Equal(0, engine.TranscriptionCallCount);
    }

    [Fact]
    public async Task UnconfiguredEngineOrUnsupportedTranslationDoesNotStartFallback()
    {
        var engine = new FakeTranscriptionEngine("recovery", "recovery-model");
        SetTranscriptionEngines(engine);

        var unsupportedTranslation = await CreateService(hasLicense: true).TryTranscribeAsync(
            [0.1f],
            "primary",
            new TimeoutException(),
            CancellationToken.None);
        engine.IsConfigured = false;
        engine.SupportsTranslationValue = true;
        var unconfigured = await CreateService(hasLicense: true).TryTranscribeAsync(
            [0.1f],
            "primary",
            new TimeoutException(),
            CancellationToken.None);

        Assert.Null(unsupportedTranslation);
        Assert.Null(unconfigured);
        Assert.Equal(0, engine.TranscriptionCallCount);
    }

    [Fact]
    public async Task FallbackFailureStartsOnlyOneAlternativeRequestAndPreservesBothErrors()
    {
        var primary = new TimeoutException("primary timeout");
        var fallback = new HttpRequestException("fallback unavailable");
        var engine = new FakeTranscriptionEngine("recovery", "recovery-model")
        {
            SupportsTranslationValue = true,
            Failure = fallback
        };
        SetTranscriptionEngines(engine);

        var error = await Assert.ThrowsAsync<TranscriptionFallbackException>(() =>
            CreateService(hasLicense: true).TryTranscribeAsync(
                [0.1f],
                "primary",
                primary,
                CancellationToken.None));

        Assert.Equal(1, engine.TranscriptionCallCount);
        var aggregate = Assert.IsType<AggregateException>(error.InnerException);
        Assert.Contains(primary, aggregate.InnerExceptions);
        Assert.Contains(fallback, aggregate.InnerExceptions);
    }

    [Theory]
    [InlineData(PluginRequestFailureKind.Network, true)]
    [InlineData(PluginRequestFailureKind.Timeout, true)]
    [InlineData(PluginRequestFailureKind.RateLimit, true)]
    [InlineData(PluginRequestFailureKind.ServerError, true)]
    [InlineData(PluginRequestFailureKind.Authentication, true)]
    [InlineData(PluginRequestFailureKind.Configuration, true)]
    [InlineData(PluginRequestFailureKind.RequestTooLarge, false)]
    [InlineData(PluginRequestFailureKind.InvalidRequest, false)]
    [InlineData(PluginRequestFailureKind.Unknown, false)]
    public void FailureClassification_MatchesFallbackPolicy(
        PluginRequestFailureKind kind,
        bool expected)
    {
        var exception = new PluginRequestException("failure", kind);

        var actual = AutomaticTranscriptionFallbackService.ShouldAttemptFallback(
            exception,
            CancellationToken.None);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CancellationAndUnsupportedTaskAreNotEligible()
    {
        Assert.False(AutomaticTranscriptionFallbackService.ShouldAttemptFallback(
            new OperationCanceledException(),
            CancellationToken.None));
        Assert.False(AutomaticTranscriptionFallbackService.ShouldAttemptFallback(
            new InvalidOperationException("Translation is not supported"),
            CancellationToken.None));
    }

    [Fact]
    public void ProviderTimeoutCancellationIsEligible()
    {
        Assert.True(AutomaticTranscriptionFallbackService.ShouldAttemptFallback(
            new TaskCanceledException("provider timeout"),
            CancellationToken.None));
    }

    [Fact]
    public void KnownModelLoadFailureIsEligible()
    {
        Assert.True(AutomaticTranscriptionFallbackService.ShouldAttemptFallback(
            new ModelManagerRequestException(409, "Configured model is not downloaded"),
            CancellationToken.None));
    }

    public void Dispose()
    {
        _modelManager.Dispose();
        _pluginManager.Dispose();
    }

    private AutomaticTranscriptionFallbackService CreateService(bool hasLicense) =>
        new(_modelManager, _settings, _dictionary.Object, () => hasLicense);

    private void SetTranscriptionEngines(params ITranscriptionEnginePlugin[] engines)
    {
        var field = typeof(PluginManager).GetField(
            "_transcriptionEngines",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(PluginManager).FullName, "_transcriptionEngines");
        field.SetValue(_pluginManager, engines.ToList());
    }

    private sealed class FakeTranscriptionEngine(string providerId, string modelId) :
        ITranscriptionEnginePlugin,
        ITranscriptionEngineSelectionIdentity
    {
        public string PluginId => providerId;
        public string PluginName => providerId;
        public string PluginVersion => "1.0.0";
        public string TranscriptionSelectionId => providerId;
        public string ProviderId => providerId;
        public string ProviderDisplayName => providerId;
        public bool IsConfigured { get; set; } = true;
        public IReadOnlyList<PluginModelInfo> TranscriptionModels { get; } =
            [new PluginModelInfo(modelId, modelId)];
        public string? SelectedModelId { get; private set; }
        public bool SupportsTranslation => SupportsTranslationValue;
        public bool SupportsTranslationValue { get; set; }
        public PluginTranscriptionResult Result { get; set; } = new("fallback", "en", 1);
        public Exception? Failure { get; set; }
        public int TranscriptionCallCount { get; private set; }
        public bool LastTranslate { get; private set; }
        public IReadOnlyList<string> LastLanguageHints { get; private set; } = [];

        public Task ActivateAsync(IPluginHostServices host) => Task.CompletedTask;
        public Task DeactivateAsync() => Task.CompletedTask;
        public UserControl? CreateSettingsView() => null;
        public void SelectModel(string selectedModelId) => SelectedModelId = selectedModelId;
        public Task LoadModelAsync(string selectedModelId, CancellationToken ct)
        {
            SelectedModelId = selectedModelId;
            return Task.CompletedTask;
        }

        public Task<PluginTranscriptionResult> TranscribeAsync(
            byte[] wavAudio,
            string? language,
            bool translate,
            string? prompt,
            CancellationToken ct) =>
            TranscribeWithLanguageHintsAsync(
                wavAudio,
                string.IsNullOrWhiteSpace(language) ? [] : [language],
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
            TranscriptionCallCount++;
            LastTranslate = translate;
            LastLanguageHints = languageHints;
            if (Failure is not null)
                return Task.FromException<PluginTranscriptionResult>(Failure);
            return Task.FromResult(Result);
        }

        public void Dispose() { }
    }
}
